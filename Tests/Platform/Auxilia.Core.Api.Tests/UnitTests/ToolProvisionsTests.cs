using Auxilia.Core.Contracts;

namespace Auxilia.Core.Api.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public sealed class ToolProvisionsTests
{
    [TestCase("copilot", new[] { "copilot@1.0.107" }, true, TestName = "a bare requirement takes any version")]
    [TestCase("copilot", new[] { "copilot" }, true, TestName = "a bare requirement takes an unversioned tool")]
    [TestCase("COPILOT", new[] { "copilot" }, true, TestName = "names compare case-insensitively")]
    [TestCase("copilot@1.0.107", new[] { "copilot@1.0.107" }, true, TestName = "a version takes exactly that version")]
    [TestCase("copilot@1.0.95", new[] { "copilot@1.0.107" }, false, TestName = "a version refuses another one")]
    [TestCase("copilot@1.0.107", new[] { "copilot" }, false, TestName = "a version refuses an unversioned tool")]
    [TestCase("copilot", new[] { "claude", "codex" }, false, TestName = "an absent tool is unmet")]
    [TestCase("copilot@", new[] { "copilot" }, false, TestName = "a malformed requirement is never met")]
    public void IsMet(string required, string[] provided, bool expected)
        => Assert.That(ToolProvisions.IsMet(required, provided), Is.EqualTo(expected));

    [Test]
    public void Unmet_ListsWhatIsMissing_InItsOrder()
        => Assert.That(
            ToolProvisions.Unmet(["claude", "copilot@1.0.95", "git"], ["claude", "copilot@1.0.107"]),
            Is.EqualTo(new[] { "copilot@1.0.95", "git" }));

    [TestCase("copilot", "copilot", null)]
    [TestCase("copilot@1.0.107", "copilot", "1.0.107")]
    public void Parse_SplitsNameAndVersion(string tool, string name, string? version)
        => Assert.That(ToolProvisions.Parse(tool), Is.EqualTo((name, version)));

    [TestCase("")]
    [TestCase("@1.0")]
    [TestCase("copilot@")]
    [TestCase("copilot@1@2")]
    [TestCase("copilot 1.0")]
    public void TryParse_RefusesWhatIsNoTool(string tool)
        => Assert.That(ToolProvisions.TryParse(tool, out _, out _), Is.False);
}
