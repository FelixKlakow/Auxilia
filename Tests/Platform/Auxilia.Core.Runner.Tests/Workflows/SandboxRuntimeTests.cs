using Auxilia.Core.Runner.Workflows;

namespace Auxilia.Core.Runner.Tests.Workflows;

[TestFixture]
[Category("Unit")]
public sealed class SandboxRuntimeTests
{
    [Test]
    public void TheFirstPreferredSandboxTheDaemonOffers_IsChosen()
        => Assert.That(SandboxRuntime.Select(["runc", "kata-runtime", "runsc"], ["runsc", "kata-runtime"]),
            Is.EqualTo("runsc"));

    [Test]
    public void WithoutAnOfferedSandbox_NothingIsChosen()
        => Assert.That(SandboxRuntime.Select(["runc"], ["runsc", "kata-runtime"]), Is.Null);

    [Test]
    public async Task AnExplicitRuntime_WinsWithoutAskingTheDaemon()
        => Assert.That(await SandboxRuntime.ResolveAsync(null!,
            new DockerWorkflowLauncherSettings { ContainerRuntime = "runsc", RequireSandboxRuntime = true },
            CancellationToken.None), Is.EqualTo("runsc"));

    [Test]
    public void AProductionRunner_RequiresTheSandbox_UnlessExplicitlyOptedOut()
    {
        var trusting = new WorkflowDispatcherSettings { TrustedSigningKeys = ["key"] };

        var production = new DockerWorkflowLauncherSettings();
        production.ApplyPosture(trusting);
        var optedOut = new DockerWorkflowLauncherSettings { RequireSandboxRuntime = false };
        optedOut.ApplyPosture(trusting);
        var development = new DockerWorkflowLauncherSettings();
        development.ApplyPosture(new WorkflowDispatcherSettings());

        Assert.That((production.RequireSandboxRuntime, optedOut.RequireSandboxRuntime, development.RequireSandboxRuntime),
            Is.EqualTo(((bool?)true, (bool?)false, (bool?)false)));
    }

    [Test]
    public void OperatorLabels_AreStamped_WithoutOverridingPlatformLabels()
    {
        var settings = new DockerWorkflowLauncherSettings
        {
            ContainerLabels = new() { ["owner"] = "team-a", [DockerWorkflowLauncher.InstanceIdLabel] = "spoofed" }
        };
        var request = new WorkflowLaunchRequest("/x", new Dictionary<string, string>()) { InstanceId = Guid.Empty };

        var labels = DockerWorkflowLauncher.BuildContainerLabels(request, settings);

        Assert.That(labels["owner"], Is.EqualTo("team-a"));
        Assert.That(labels[DockerWorkflowLauncher.InstanceIdLabel], Is.EqualTo(Guid.Empty.ToString("D")));
    }

    [Test]
    public void ADigestPinnedCoordinate_IsRecognized()
    {
        Assert.That(WorkflowDispatcher.IsDigestPinned("docker://reg/img@sha256:" + new string('0', 64)), Is.True);
        Assert.That(WorkflowDispatcher.IsDigestPinned("docker://reg/img:latest"), Is.False);
        Assert.That(WorkflowDispatcher.IsDigestPinned("docker://reg/img@sha256:abc"), Is.False);
    }
}
