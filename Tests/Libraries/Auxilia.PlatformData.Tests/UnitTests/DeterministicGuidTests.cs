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
    public void ProviderTypeKeyedIds_AreCaseInsensitive()
    {
        // Provider types are case-insensitive platform-wide: Core store, dispatch lookup, and
        // the runner registry must converge on ONE record regardless of the caller's casing.
        Assert.Multiple(() =>
        {
            Assert.That(
                Entities.SlotProviderRecord.IdFor("GitHub-Copilot-CLI"),
                Is.EqualTo(Entities.SlotProviderRecord.IdFor("github-copilot-cli")));
            Assert.That(
                Entities.ProviderCatalogRecord.IdFor("GitHub-Copilot-CLI"),
                Is.EqualTo(Entities.ProviderCatalogRecord.IdFor("github-copilot-cli")));
        });
    }

    [Test]
    public void For_ProducesVersion5Guid()
    {
        var guid = DeterministicGuid.For("anything");
        var text = guid.ToString("D");
        Assert.That(text[14], Is.EqualTo('5'), "Version nibble must be 5.");
    }
}
