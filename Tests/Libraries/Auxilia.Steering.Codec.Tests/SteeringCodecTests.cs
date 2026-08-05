using System.Text.Json;
using Auxilia.Steering.Codec;

namespace Auxilia.Steering.Codec.Tests;

[TestFixture]
[Category("Unit")]
public sealed class SteeringCodecTests
{
    private static readonly SteeringFrame[] AllFrames =
    [
        new SteeringCapabilities(["guidance", "halt"]),
        new SteeringFormRequested("req-1",
        [
            new SteeringFormQuestion("q1", "Approve the plan?",
                [new SteeringFormOption("yes", "Yes", "Ship it"), new SteeringFormOption("no", "No")],
                MultiSelect: false, AllowFreeText: true, Detail: "## Plan", DetailFormat: "markdown"),
        ]),
        new SteeringFormResolved("req-1"),
        new SteeringSessionEnded(true, null),
        new SteeringSessionEnded(false, "boom"),
        new SteeringTurnEnded(3, "did the thing"),
        new SteeringAttention("Waiting for you"),
        new SteeringSessionVocabulary(
            [new SteeringVocabularyModel("opus", "Opus", ["low", "high"], "high")]),
        new SteeringGuidance("look at the tests first"),
        new SteeringHalt(),
        new SteeringEnd(),
        new SteeringSetting("permission-mode", "auto-allow"),
        new SteeringFormAnswer("req-1", [new SteeringAnswer("q1", ["yes"], "also add docs")]),
    ];

    [TestCaseSource(nameof(AllFrames))]
    public void EveryFrame_RoundTrips(SteeringFrame frame)
    {
        var json = SteeringCodec.Encode(frame);
        var decoded = SteeringCodec.Decode(json);

        Assert.That(decoded, Is.Not.Null);
        Assert.That(decoded!.GetType(), Is.EqualTo(frame.GetType()));
        // Records holding lists compare those by reference, so equality is asserted on the
        // re-encoded wire form — byte-identical JSON proves the round trip lost nothing.
        Assert.That(SteeringCodec.Encode(decoded), Is.EqualTo(json));
    }

    [TestCaseSource(nameof(AllFrames))]
    public void TypeDiscriminator_IsTheFirstProperty(SteeringFrame frame)
    {
        var json = SteeringCodec.Encode(frame);

        using var doc = JsonDocument.Parse(json);
        var first = doc.RootElement.EnumerateObject().First();
        Assert.Multiple(() =>
        {
            Assert.That(first.Name, Is.EqualTo("$type"));
            Assert.That(first.Value.GetString(), Is.EqualTo(frame.Type));
        });
    }

    [Test]
    public void WirePropertyNames_AreTheProtocol_NeverDrift()
    {
        // These literals ARE the contract with every deployed steering client and workflow
        // image. A failure here is a protocol break, not a refactor.
        Assert.Multiple(() =>
        {
            Assert.That(SteeringCodec.Encode(new SteeringCapabilities(["guidance"])),
                Is.EqualTo("""{"$type":"capabilities","accepts":["guidance"]}"""));
            Assert.That(SteeringCodec.Encode(new SteeringGuidance("hi")),
                Is.EqualTo("""{"$type":"guidance","text":"hi"}"""));
            Assert.That(SteeringCodec.Encode(new SteeringSetting("model", "opus")),
                Is.EqualTo("""{"$type":"setting","key":"model","value":"opus"}"""));
            Assert.That(SteeringCodec.Encode(new SteeringTurnEnded(1, "s")),
                Is.EqualTo("""{"$type":"turn-ended","turn":1,"summary":"s"}"""));
            Assert.That(SteeringCodec.Encode(new SteeringSessionEnded(true, null)),
                Is.EqualTo("""{"$type":"session-ended","success":true,"error":null}"""));
            Assert.That(SteeringCodec.Encode(new SteeringFormAnswer("r", [new SteeringAnswer("q", ["a"], null)])),
                Is.EqualTo("""{"$type":"form-answer","requestId":"r","answers":[{"questionId":"q","selectedIds":["a"],"freeText":null}]}"""));
            Assert.That(SteeringCodec.Encode(new SteeringFormResolved("r")),
                Is.EqualTo("""{"$type":"form-resolved","requestId":"r"}"""));
            Assert.That(SteeringCodec.Encode(new SteeringAttention("m")),
                Is.EqualTo("""{"$type":"attention","message":"m"}"""));
            Assert.That(SteeringCodec.Encode(new SteeringHalt()), Is.EqualTo("""{"$type":"halt"}"""));
            Assert.That(SteeringCodec.Encode(new SteeringEnd()), Is.EqualTo("""{"$type":"end"}"""));
        });
    }

    [Test]
    public void Decode_UnknownType_ReturnsNull()
        => Assert.That(SteeringCodec.Decode("""{"$type":"hologram","x":1}"""), Is.Null);

    [Test]
    public void Decode_UnknownProperties_AreIgnored_NotFatal()
    {
        var decoded = SteeringCodec.Decode(
            """{"$type":"guidance","text":"hi","futureField":{"nested":true}}""");

        Assert.That(decoded, Is.EqualTo(new SteeringGuidance("hi")),
            "an older peer must survive a newer peer's additions");
    }

    [Test]
    public void Decode_MalformedOrNonObjectJson_ReturnsNull()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SteeringCodec.Decode("not json"), Is.Null);
            Assert.That(SteeringCodec.Decode("42"), Is.Null);
            Assert.That(SteeringCodec.Decode("[]"), Is.Null);
            Assert.That(SteeringCodec.Decode("""{"noType":true}"""), Is.Null);
        });
    }
}
