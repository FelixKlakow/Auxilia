using Auxilia.Workflows.Policy;
using Auxilia.Workflows.SourceControl;
using Auxilia.Workflows.TestRunner;

namespace Auxilia.Workflows.Tests.Policy;

[TestFixture]
[Category("Unit")]
public class ToolPolicySettingsTests
{
    [Test]
    public void Build_WithAllowEntry_IsAllowed_ReturnsTrue()
    {
        var settings = new Dictionary<string, string>
        {
            ["source_control.commit"] = "allow"
        };

        var policy = ToolPolicySettings.Build(settings);

        Assert.That(policy.IsAllowed(SourceControlOperation.Commit), Is.True);
    }

    [Test]
    public void Build_WithDenyEntry_IsAllowed_ReturnsFalse()
    {
        var settings = new Dictionary<string, string>
        {
            ["source_control.commit"] = "deny"
        };

        var policy = ToolPolicySettings.Build(settings);

        Assert.That(policy.IsAllowed(SourceControlOperation.Commit), Is.False);
    }

    [Test]
    public void Build_WithEmptyDictionary_IsAllowed_ReturnsFalse()
    {
        var policy = ToolPolicySettings.Build(new Dictionary<string, string>());

        Assert.That(policy.IsAllowed(SourceControlOperation.Commit), Is.False);
        Assert.That(policy.IsAllowed(TestRunnerOperation.RunTests), Is.False);
    }

    [Test]
    public void Build_WithUnknownValue_ThrowsInvalidOperationException()
    {
        var settings = new Dictionary<string, string>
        {
            ["source_control.commit"] = "maybe"
        };

        Assert.Throws<InvalidOperationException>(() => ToolPolicySettings.Build(settings));
    }
}
