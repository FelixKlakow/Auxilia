using Auxilia.SteeringInstance.Workflows;
using Auxilia.Workflows;
using Auxilia.Workflows.Environment;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Auxilia.SteeringInstance.Tests;

[TestFixture]
public class EnvironmentValidatorTests
{
    private EnvironmentValidator MakeValidator(RunnerProfile profile)
        => new(Options.Create(profile), NullLogger<EnvironmentValidator>.Instance);

    [Test]
    public void Validate_AllRequirementsSatisfied_ReturnsValid()
    {
        var profile = new RunnerProfile
        {
            AvailableTools = new HashSet<Tool> { Tool.Git, Tool.DotNetSdk },
            OperatingSystem = OsConstraint.Linux,
            OpenPorts = new HashSet<int> { 8080, 443 }
        };
        var manifest = new WorkflowManifest(
            "TestWorkflow", "instance-1",
            [],
            [
                new ToolRequirement(Tool.Git),
                new OsRequirement(OsConstraint.Linux),
                new PortRequirement(8080)
            ]);

        var result = MakeValidator(profile).Validate(manifest);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.UnsatisfiedRequirements, Is.Empty);
    }

    [Test]
    public void Validate_MissingTool_ReturnsInvalid()
    {
        var profile = new RunnerProfile
        {
            AvailableTools = new HashSet<Tool> { Tool.Git },
            OperatingSystem = OsConstraint.Linux,
            OpenPorts = new HashSet<int>()
        };
        var manifest = new WorkflowManifest(
            "TestWorkflow", "instance-1",
            [],
            [new ToolRequirement(Tool.DotNetSdk)]);

        var result = MakeValidator(profile).Validate(manifest);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.UnsatisfiedRequirements, Has.Count.EqualTo(1));
        Assert.That(result.UnsatisfiedRequirements[0],
            Does.Contain(nameof(Tool.DotNetSdk)));
    }

    [Test]
    public void Validate_WrongOs_ReturnsInvalid()
    {
        var profile = new RunnerProfile
        {
            AvailableTools = new HashSet<Tool>(),
            OperatingSystem = OsConstraint.Windows,
            OpenPorts = new HashSet<int>()
        };
        var manifest = new WorkflowManifest(
            "TestWorkflow", "instance-1",
            [],
            [new OsRequirement(OsConstraint.Linux)]);

        var result = MakeValidator(profile).Validate(manifest);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.UnsatisfiedRequirements, Has.Count.EqualTo(1));
        Assert.That(result.UnsatisfiedRequirements[0],
            Does.Contain(nameof(OsConstraint.Linux)));
    }

    [Test]
    public void Validate_PortUnavailable_ReturnsInvalid()
    {
        var profile = new RunnerProfile
        {
            AvailableTools = new HashSet<Tool>(),
            OperatingSystem = OsConstraint.Linux,
            OpenPorts = new HashSet<int> { 443 }
        };
        var manifest = new WorkflowManifest(
            "TestWorkflow", "instance-1",
            [],
            [new PortRequirement(8080)]);

        var result = MakeValidator(profile).Validate(manifest);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.UnsatisfiedRequirements, Has.Count.EqualTo(1));
        Assert.That(result.UnsatisfiedRequirements[0], Does.Contain("8080"));
    }

    [Test]
    public void Validate_UnknownRequirementType_TreatedAsUnsatisfied()
    {
        var profile = new RunnerProfile
        {
            AvailableTools = new HashSet<Tool>(),
            OperatingSystem = OsConstraint.Linux,
            OpenPorts = new HashSet<int>()
        };
        var manifest = new WorkflowManifest(
            "TestWorkflow", "instance-1",
            [],
            [new UnknownRequirement()]);

        var result = MakeValidator(profile).Validate(manifest);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.UnsatisfiedRequirements, Is.Not.Empty);
    }

    private sealed record UnknownRequirement : IEnvironmentRequirement;
}
