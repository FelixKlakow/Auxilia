using System.Text.RegularExpressions;
using Auxilia.Core.Runner.Workflows;
using Auxilia.Core.Runner.Workflows.Pods;
using Auxilia.Messaging;
using Auxilia.Workflows.Messaging;

namespace Auxilia.Core.Runner.Tests.Workflows;

/// <summary>
/// What a workflow container may do: its broker identity's permission policy (evaluated with
/// .NET regex, which agrees with RabbitMQ's PCRE on these anchored patterns) and the container
/// confinement the launcher applies.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class WorkloadConfinementTests
{
    private static readonly Guid Own = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();
    private static readonly WorkflowDispatcherSettings Settings = new();
    private static readonly BrokerPermissions Permissions = WorkloadBusAccess.For(Own, WorkloadBusTopology.IngressQueues(Settings));

    private static bool Matches(string pattern, string resource) => Regex.IsMatch(resource, pattern);

    [Test]
    public void Bus_ReadAndConfigure_CoverExactlyTheInstancesOwnQueues()
    {
        string[] own =
        [
            WorkflowQueues.ResponseQueueFor(Own), WorkflowQueues.ResourceResponseQueueFor(Own),
            WorkflowQueues.InputQueueFor(Own), WorkflowQueues.PodControlResponseQueueFor(Own),
            WorkflowQueues.CancelQueueFor(Own), WorkflowQueues.DrainQueueFor(Own),
        ];
        string[] foreign =
        [
            Settings.CommandQueueName, "workflow.cancel-commands", WorkflowQueues.SignalsQueueName,
            Settings.RegistrationQueueName, WorkflowQueues.ResponseQueueFor(Other),
            WorkflowQueues.CancelQueueFor(Other), WorkflowQueues.ResponseQueueFor(Own) + "-x",
            "amq.default", "workflow.status",
        ];

        foreach (var queue in own)
        {
            Assert.That(Matches(Permissions.Read, queue), Is.True, queue);
            Assert.That(Matches(Permissions.Configure, queue), Is.True, queue);
        }
        foreach (var queue in foreign)
        {
            Assert.That(Matches(Permissions.Read, queue), Is.False, $"must not consume {queue}");
            Assert.That(Matches(Permissions.Configure, queue), Is.False, $"must not declare/delete {queue}");
        }
    }

    [Test]
    public void Bus_Write_IsOnlyTheIngressAndTheWorkloadFacingExchanges()
    {
        foreach (var exchange in new[] { "workflow.ingress", "workflow.state", "workflow.views", "workflow.events" })
            Assert.That(Matches(Permissions.Write, exchange), Is.True, exchange);

        // The default exchange would reach EVERY queue (the run-command queue above all).
        foreach (var exchange in new[] { "amq.default", "", "workflow.status", "workflow.artifacts", "runner.heartbeats" })
            Assert.That(Matches(Permissions.Write, exchange), Is.False, $"must not publish to '{exchange}'");
    }

    [Test]
    public void Bus_TopicPermissions_PinViewsToTheOwnInstanceAndKeepReservedEventsPlatformOnly()
    {
        var views = Permissions.TopicPermissions.Single(t => t.Exchange == "workflow.views");
        Assert.That(Matches(views.Write, Own.ToString()), Is.True);
        Assert.That(Matches(views.Write, Other.ToString()), Is.False, "never another run's view");

        var events = Permissions.TopicPermissions.Single(t => t.Exchange == "workflow.events");
        Assert.That(Matches(events.Write, "build.finished"), Is.True);
        Assert.That(Matches(events.Write, "run.succeeded"), Is.False, "run.* is platform-reserved");

        var ingress = Permissions.TopicPermissions.Single(t => t.Exchange == "workflow.ingress");
        foreach (var queue in WorkloadBusTopology.IngressQueues(Settings))
            Assert.That(Matches(ingress.Write, queue), Is.True, queue);
        Assert.That(Matches(ingress.Write, Settings.CommandQueueName), Is.False);
    }

    [Test]
    public void Confinement_DropsEveryCapabilityButTheRetainedSet_AndForbidsEscalation()
    {
        var p = DockerWorkflowLauncher.BuildBakedImageContainerParameters(
            new WorkflowLaunchRequest(string.Empty, new Dictionary<string, string>(), [], DockerImageUri: "img"),
            new DockerWorkflowLauncherSettings());

        Assert.That(p.HostConfig.Privileged, Is.False);
        Assert.That(p.HostConfig.CapDrop, Is.EqualTo(new[] { "ALL" }));
        Assert.That(p.HostConfig.CapAdd, Is.EquivalentTo(new[] { "CHOWN", "DAC_OVERRIDE", "FOWNER", "SETUID", "SETGID", "KILL" }));
        Assert.That(p.HostConfig.CapAdd, Does.Not.Contain("NET_RAW").And.Not.Contain("SYS_ADMIN"));
        Assert.That(p.HostConfig.SecurityOpt, Does.Contain("no-new-privileges:true"));
        Assert.That(p.HostConfig.SecurityOpt!.Any(o => o.Contains("unconfined")), Is.False);
        Assert.That(p.HostConfig.PidsLimit, Is.EqualTo(4096));
        Assert.That(p.HostConfig.Memory, Is.EqualTo(8192L * 1024 * 1024));
        Assert.That(p.HostConfig.ExtraHosts, Is.Null, "no host-gateway mapping unless an operator opts in");
    }

    [Test]
    public void Confinement_AppliesTheConfiguredRuntimeAndCpuCeiling()
    {
        var p = DockerWorkflowLauncher.BuildBakedImageContainerParameters(
            new WorkflowLaunchRequest(string.Empty, new Dictionary<string, string>(), [], DockerImageUri: "img"),
            new DockerWorkflowLauncherSettings { ContainerRuntime = "runsc", ContainerCpus = 1.5 });

        Assert.That(p.HostConfig.Runtime, Is.EqualTo("runsc"));
        Assert.That(p.HostConfig.NanoCPUs, Is.EqualTo(1_500_000_000));
    }

    [Test]
    public void Confinement_WindowsDaemon_KeepsOnlyTheResourceCeilings()
    {
        var hostConfig = new Docker.DotNet.Models.HostConfig();
        DockerWorkflowLauncher.ApplyConfinement(hostConfig, new DockerWorkflowLauncherSettings(), linux: false);

        Assert.That(hostConfig.CapDrop, Is.Null);
        Assert.That(hostConfig.SecurityOpt, Is.Null);
        Assert.That(hostConfig.Memory, Is.EqualTo(8192L * 1024 * 1024));
    }

    [Test]
    public void Companions_CannotEscalate_AndHaveNoRawSockets()
    {
        var p = DockerPodHost.BuildCompanionParameters(
            new PlannedCompanion("db", "db-0", "postgres@sha256:abc",
                new Dictionary<string, string>(), null, null, null, []),
            Own, "pod-net");

        Assert.That(p.HostConfig.SecurityOpt, Does.Contain("no-new-privileges:true"));
        Assert.That(p.HostConfig.CapDrop, Does.Contain("NET_RAW"));
        Assert.That(p.HostConfig.PidsLimit, Is.GreaterThan(0));
        Assert.That(p.HostConfig.Privileged, Is.False);
    }
}
