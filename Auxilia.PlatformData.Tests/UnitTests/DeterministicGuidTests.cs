using Auxilia.PlatformData;

namespace Auxilia.PlatformData.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class DeterministicGuidTests
{
    [Test]
    public void For_SameInput_ProducesSameGuid()
    {
        Assert.That(DeterministicGuid.For("a", "b"), Is.EqualTo(DeterministicGuid.For("a", "b")));
    }

    [Test]
    public void For_DifferentInput_ProducesDifferentGuid()
    {
        Assert.That(DeterministicGuid.For("a", "b"), Is.Not.EqualTo(DeterministicGuid.For("a", "c")));
    }

    [Test]
    public void For_PartBoundariesMatter()
    {
        // ("ab","c") must not collide with ("a","bc") when callers separate parts properly.
        Assert.That(
            DeterministicGuid.For("slot-configuration", "ab", "", "c"),
            Is.Not.EqualTo(DeterministicGuid.For("slot-configuration", "a", "", "bc")));
    }

    [Test]
    public void For_EmptyParts_Throws()
    {
        Assert.Throws<ArgumentException>(() => DeterministicGuid.For());
    }

    [Test]
    public void For_ProducesVersion5Guid()
    {
        var guid = DeterministicGuid.For("anything");
        var text = guid.ToString("D");
        Assert.That(text[14], Is.EqualTo('5'), "Version nibble must be 5.");
    }
}
