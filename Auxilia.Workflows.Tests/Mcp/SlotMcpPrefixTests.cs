using Auxilia.Workflows.Mcp;

namespace Auxilia.Workflows.Tests.Mcp;

[TestFixture]
[Category("Unit")]
public class SlotMcpPrefixTests
{
    [Test]
    public void Format_ValidInputs_ReturnsSlashSeparatedName()
    {
        var result = SlotMcpPrefix.Format("primary-scm", "list_files");
        Assert.That(result, Is.EqualTo("primary-scm/list_files"));
    }

    [Test]
    public void Format_ValidInputs_ReturnsPrefixedName()
    {
        var result = SlotMcpPrefix.Format("primary-scm", "list_files");
        Assert.That(result, Is.EqualTo("primary-scm/list_files"));
    }

    [Test]
    public void Format_NullSlotName_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => SlotMcpPrefix.Format(null!, "list_files"));
    }

    [Test]
    public void Format_EmptySlotName_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => SlotMcpPrefix.Format("", "list_files"));
    }

    [Test]
    public void Format_WhitespaceSlotName_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => SlotMcpPrefix.Format("   ", "list_files"));
    }

    [Test]
    public void Format_NullToolName_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => SlotMcpPrefix.Format("primary-scm", null!));
    }

    [Test]
    public void Format_EmptyToolName_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => SlotMcpPrefix.Format("primary-scm", ""));
    }

    [Test]
    public void Format_WhitespaceToolName_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => SlotMcpPrefix.Format("primary-scm", "   "));
    }

    [Test]
    public void Format_ToolNameContainsSlash_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => SlotMcpPrefix.Format("slot", "a/b"));
    }

    [Test]
    public void Format_DifferentSlotNames_ProduceDistinctPrefixedNames()
    {
        var result1 = SlotMcpPrefix.Format("slot-a", "read_file");
        var result2 = SlotMcpPrefix.Format("slot-b", "read_file");
        Assert.That(result1, Is.Not.EqualTo(result2));
    }
}
