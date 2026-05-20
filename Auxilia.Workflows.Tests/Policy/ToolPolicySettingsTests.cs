using Auxilia.Workflows.Policy;

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

        Assert.That(policy.IsAllowed("source_control.commit"), Is.True);
    }

    [Test]
    public void Build_WithDenyEntry_IsAllowed_ReturnsFalse()
    {
        var settings = new Dictionary<string, string>
        {
            ["source_control.commit"] = "deny"
        };

        var policy = ToolPolicySettings.Build(settings);

        Assert.That(policy.IsAllowed("source_control.commit"), Is.False);
    }

    [Test]
    public void Build_WithEmptyDictionary_IsAllowed_ReturnsFalse()
    {
        var policy = ToolPolicySettings.Build(new Dictionary<string, string>());

        Assert.That(policy.IsAllowed("source_control.commit"), Is.False);
        Assert.That(policy.IsAllowed("test_runner.run_tests"), Is.False);
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
