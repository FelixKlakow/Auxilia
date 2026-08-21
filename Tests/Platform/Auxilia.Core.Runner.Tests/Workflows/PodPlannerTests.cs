using Auxilia.Core.Runner.Workflows;
using Auxilia.Core.Runner.Workflows.Pods;
using Auxilia.Workflows.Companions;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Auxilia.Core.Runner.Tests.Workflows;

[TestFixture]
[Category("Unit")]
public class PodPlannerTests
{
    private const string Image = "img@sha256:2222222222222222222222222222222222222222222222222222222222222222";
    private static readonly Guid Instance = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static CompanionDeclaration Companion(string name) => new(name, Image);

    [Test]
    public void Plan_EmptyTopology_IsNull()
        => Assert.That(PodPlanner.Plan([], new Dictionary<string, string>(), Instance), Is.Null);

    [Test]
    public void Plan_CountInput_ClampsToSignedBounds()
    {
        var machine = Companion("machine") with { MinInstances = 1, MaxInstances = 4, CountInput = "n" };

        int CountFor(string? raw)
        {
            var context = raw is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["n"] = raw };
            return PodPlanner.Plan([machine], context, Instance)!.Companions.Count;
        }

        Assert.Multiple(() =>
        {
            Assert.That(CountFor("3"), Is.EqualTo(3));
            Assert.That(CountFor("99"), Is.EqualTo(4), "above the bound clamps down");
            Assert.That(CountFor("0"), Is.EqualTo(1), "below the bound clamps up");
            Assert.That(CountFor("junk"), Is.EqualTo(1), "unparseable falls to the minimum");
            Assert.That(CountFor(null), Is.EqualTo(1), "missing input falls to the minimum");
        });
    }

    [Test]
    public void Plan_ScaledTemplate_GetsIndexedInstanceNames_SingleStaysBare()
    {
        var plan = PodPlanner.Plan(
            [
                Companion("rabbit"),
                Companion("machine") with { MinInstances = 0, MaxInstances = 8, CountInput = "n" }
            ],
            new Dictionary<string, string> { ["n"] = "2" }, Instance)!;

        Assert.That(plan.Companions.Select(c => c.InstanceName),
            Is.EquivalentTo(new[] { "rabbit", "machine-1", "machine-2" }));
    }

    [Test]
    public void Plan_StartAfter_OrdersDependentsLast()
    {
        var plan = PodPlanner.Plan(
            [
                Companion("manager") with { StartAfter = ["rabbit", "machine"] },
                Companion("machine") with { StartAfter = ["rabbit"] },
                Companion("rabbit")
            ],
            new Dictionary<string, string>(), Instance)!;

        Assert.That(plan.Companions.Select(c => c.TemplateName),
            Is.EqualTo(new[] { "rabbit", "machine", "manager" }));
    }

    [Test]
    public void Plan_ResolvesEveryEnvironmentKind_AndAnnouncesToTheWorkflow()
    {
        var secrets = new Queue<string>(["secret-1"]);
        var plan = PodPlanner.Plan(
            [
                Companion("machine") with
                {
                    MinInstances = 0, MaxInstances = 4, CountInput = "n",
                    Readiness = new CompanionReadinessProbe(CompanionReadinessProbe.Tcp, 9000)
                },
                Companion("rabbit") with
                {
                    EnvironmentVariables =
                    [
                        new CompanionEnvironmentVariable("RABBITMQ_DEFAULT_PASS", CompanionEnvironmentVariable.RunSecret)
                    ],
                    Readiness = new CompanionReadinessProbe(CompanionReadinessProbe.Tcp, 5672)
                },
                Companion("manager") with
                {
                    StartAfter = ["rabbit", "machine"],
                    EnvironmentVariables =
                    [
                        new CompanionEnvironmentVariable("BUS", CompanionEnvironmentVariable.Literal) { Value = "amqp://rabbit:5672" },
                        new CompanionEnvironmentVariable("PASS", CompanionEnvironmentVariable.RunSecret),
                        new CompanionEnvironmentVariable("MACHINES", CompanionEnvironmentVariable.InstanceEndpoints)
                        {
                            SourceCompanion = "machine", Port = 9000
                        },
                        new CompanionEnvironmentVariable("MACHINE_COUNT", CompanionEnvironmentVariable.InstanceCount)
                        {
                            SourceCompanion = "machine"
                        }
                    ]
                }
            ],
            new Dictionary<string, string> { ["n"] = "2" }, Instance,
            () => secrets.Count > 0 ? secrets.Dequeue() : "secret-2")!;

        var manager = plan.Companions.Single(c => c.TemplateName == "manager");
        Assert.Multiple(() =>
        {
            Assert.That(manager.EnvironmentVariables["BUS"], Is.EqualTo("amqp://rabbit:5672"));
            Assert.That(manager.EnvironmentVariables["MACHINES"], Is.EqualTo("machine-1:9000,machine-2:9000"));
            Assert.That(manager.EnvironmentVariables["MACHINE_COUNT"], Is.EqualTo("2"));
            Assert.That(manager.EnvironmentVariables["PASS"], Is.Not.Empty);

            // The workflow learns the resolved topology and the minted secrets.
            var a = plan.WorkflowAnnouncements;
            Assert.That(a["Workflow__Companion__MACHINE__COUNT"], Is.EqualTo("2"));
            Assert.That(a["Workflow__Companion__MACHINE__ENDPOINTS"], Is.EqualTo("machine-1:9000,machine-2:9000"));
            Assert.That(a["Workflow__Companion__RABBIT__ENDPOINTS"], Is.EqualTo("rabbit:5672"));
            Assert.That(a["Workflow__Companion__RABBIT__SECRET__RABBITMQ_DEFAULT_PASS"],
                Is.EqualTo(plan.Companions.Single(c => c.TemplateName == "rabbit")
                    .EnvironmentVariables["RABBITMQ_DEFAULT_PASS"]),
                "the workflow gets the same secret the companion was started with");
        });
    }

    [Test]
    public void Plan_PodVolumes_AreRunScopedAndSharedByName()
    {
        var plan = PodPlanner.Plan(
            [
                Companion("a") with { PodVolumes = [new CompanionPodVolume("logs", "/var/log/app")] },
                Companion("b") with { PodVolumes = [new CompanionPodVolume("logs", "/logs")] }
            ],
            new Dictionary<string, string>(), Instance)!;

        var expectedVolume = $"auxilia-pod-{Instance:N}-logs";
        Assert.Multiple(() =>
        {
            Assert.That(plan.Volumes, Is.EqualTo(new[] { new PodVolumeSpec("logs", expectedVolume) }));
            Assert.That(plan.Companions.Single(c => c.TemplateName == "a").VolumeBinds,
                Is.EqualTo(new[] { $"{expectedVolume}:/var/log/app" }));
            Assert.That(plan.Companions.Single(c => c.TemplateName == "b").VolumeBinds,
                Is.EqualTo(new[] { $"{expectedVolume}:/logs" }));
        });
    }

    [Test]
    public void Plan_NetworkName_IsPerRun()
    {
        var plan = PodPlanner.Plan([Companion("db")], new Dictionary<string, string>(), Instance)!;
        Assert.That(plan.NetworkName, Is.EqualTo($"auxilia-pod-{Instance:N}"));
    }

    [Test]
    public void Plan_PodControlWithoutCompanions_StillMaterializesNetworkAndVolumes()
    {
        var plan = PodPlanner.Plan(
            [], new PodControlDeclaration(16) { PodVolumes = ["bin"] },
            new Dictionary<string, string>(), Instance)!;

        Assert.Multiple(() =>
        {
            Assert.That(plan, Is.Not.Null,
                "an envelope needs the pod network and its delivery volumes at launch");
            Assert.That(plan.Companions, Is.Empty);
            Assert.That(plan.NetworkName, Is.EqualTo($"auxilia-pod-{Instance:N}"));
            Assert.That(plan.Volumes,
                Is.EqualTo(new[] { new PodVolumeSpec("bin", $"auxilia-pod-{Instance:N}-bin") }));
        });
    }
}

[TestFixture]
[Category("Unit")]
public class DockerPodHostParameterTests
{
    [Test]
    public void BuildCompanionParameters_CarryLabelsAliasCapsAndVolumeBinds()
    {
        var instance = Guid.NewGuid();
        var plan = new PodPlan(instance, $"auxilia-pod-{instance:N}",
            [], [], new Dictionary<string, string>());
        var companion = new PlannedCompanion(
            "machine", "machine-2", "img@sha256:aa",
            new Dictionary<string, string> { ["A"] = "1" },
            null, MemoryMb: 512, Cpus: 1.5,
            VolumeBinds: [$"auxilia-pod-{instance:N}-logs:/var/log"]);

        var parameters = DockerPodHost.BuildCompanionParameters(
            companion, plan.InstanceId, plan.NetworkName);

        Assert.Multiple(() =>
        {
            Assert.That(parameters.Image, Is.EqualTo("img@sha256:aa"));
            Assert.That(parameters.Name, Is.EqualTo($"auxilia-pod-{instance:N}-machine-2"));
            Assert.That(parameters.Env, Is.EqualTo(new[] { "A=1" }));
            Assert.That(parameters.Labels[DockerPodHost.CompanionLabel], Is.EqualTo("1"));
            Assert.That(parameters.Labels[DockerWorkflowLauncher.InstanceIdLabel],
                Is.EqualTo(instance.ToString("D")));
            Assert.That(parameters.Labels[DockerPodHost.CompanionNameLabel], Is.EqualTo("machine-2"));
            Assert.That(parameters.Labels, Does.Not.ContainKey(DockerPodHost.RuntimeSpawnLabel),
                "declared templates must not carry the runtime-spawn label — it drives the envelope clamp");
            Assert.That(parameters.HostConfig.Memory, Is.EqualTo(512L * 1024 * 1024));
            Assert.That(parameters.HostConfig.NanoCPUs, Is.EqualTo(1_500_000_000));
            Assert.That(parameters.HostConfig.Binds, Has.Count.EqualTo(1));
            Assert.That(parameters.NetworkingConfig.EndpointsConfig[plan.NetworkName].Aliases,
                Is.EqualTo(new[] { "machine-2" }));
        });
    }

    [Test]
    public void BuildCompanionParameters_ExposeNothingOnTheHost()
    {
        var companion = new PlannedCompanion(
            "machine", "machine", "img@sha256:aa",
            new Dictionary<string, string>(), null, null, null, []);

        var parameters = DockerPodHost.BuildCompanionParameters(companion, Guid.NewGuid(), "net");

        Assert.Multiple(() =>
        {
            Assert.That(parameters.ExposedPorts, Is.Null.Or.Empty,
                "a companion is reachable on the pod network only — never via host ports");
            Assert.That(parameters.HostConfig.PortBindings, Is.Null.Or.Empty);
            Assert.That(parameters.HostConfig.PublishAllPorts, Is.False);
            Assert.That(parameters.HostConfig.NetworkMode, Is.Null.Or.Empty,
                "no host/bridge override — membership comes from the pod NetworkingConfig");
        });
    }

    [Test]
    public async Task Materialize_CreatesThePodNetworkInternal()
    {
        var instance = Guid.NewGuid();
        var networkOps = new Mock<INetworkOperations>();
        NetworksCreateParameters? created = null;
        networkOps
            .Setup(n => n.CreateNetworkAsync(
                It.IsAny<NetworksCreateParameters>(), It.IsAny<CancellationToken>()))
            .Callback<NetworksCreateParameters, CancellationToken>((p, _) => created = p)
            .ReturnsAsync(new NetworksCreateResponse { ID = "net-id" });
        var client = new Mock<IDockerClient>();
        client.SetupGet(c => c.Networks).Returns(networkOps.Object);

        var host = new DockerPodHost(
            Options.Create(new DockerWorkflowLauncherSettings()),
            new Mock<IDockerClientFactory>().Object,
            new Mock<ICompanionReadinessChecker>().Object,
            NullLogger<DockerPodHost>.Instance);
        await host.MaterializeAsync(client.Object,
            new PodPlan(instance, $"auxilia-pod-{instance:N}", [], [], new Dictionary<string, string>()),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(created, Is.Not.Null);
            Assert.That(created!.Internal, Is.True,
                "the pod network must be --internal — the zero-egress guarantee for companions");
            Assert.That(created.Labels[DockerPodHost.CompanionLabel], Is.EqualTo("1"));
        });
    }

    [Test]
    public void BuildCompanionParameters_RuntimeSpawnCarriesTheRuntimeLabel()
    {
        var instance = Guid.NewGuid();
        var companion = new PlannedCompanion(
            "helper", "helper", "img@sha256:bb",
            new Dictionary<string, string>(), null, null, null, []);

        var parameters = DockerPodHost.BuildCompanionParameters(
            companion, instance, "net", runtimeSpawned: true);

        Assert.That(parameters.Labels[DockerPodHost.RuntimeSpawnLabel], Is.EqualTo("1"));
    }

    [Test]
    public void AddPodVolumeBinds_MountsEveryPodVolumeUnderWorkspacePod()
    {
        var instance = Guid.NewGuid();
        var request = new WorkflowLaunchRequest("dir", new Dictionary<string, string>())
        {
            Pod = new PodPlan(instance, "net",
                [], [new PodVolumeSpec("logs", "vol-logs"), new PodVolumeSpec("bin", "vol-bin")],
                new Dictionary<string, string>())
        };
        var binds = new List<string>();

        DockerWorkflowLauncher.AddPodVolumeBinds(binds, request);

        Assert.That(binds, Is.EqualTo(new[]
        {
            "vol-logs:/workspace/pod/logs",
            "vol-bin:/workspace/pod/bin"
        }));
    }
}
