using Auxilia.Workflows.Companions;
using System.Text.Json;

namespace Auxilia.Workflows.Tests;

[TestFixture]
[Category("Unit")]
public class WorkflowBuilderCompanionTests
{
    private const string PinnedImage = "postgres@sha256:0000000000000000000000000000000000000000000000000000000000000000";

    [Test]
    public void RequiresCompanion_FullConfiguration_RidesSchemaAndManifest()
    {
        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test")
            .RequiresCompanion("rabbit", PinnedImage, c => c
                .WithEnvironment("RABBITMQ_DEFAULT_PASS", CompanionValue.RunSecret())
                .WithReadinessProbe(tcpPort: 5672))
            .RequiresCompanion("machine-a", PinnedImage, c => c
                .WithScale(0, 8, "machines-a")
                .WithReadinessProbe(tcpPort: 9000)
                .WithResourceCaps(memoryMb: 512, cpus: 1.5))
            .RequiresCompanion("fleet-manager", PinnedImage, c => c
                .WithStartAfter("rabbit", "machine-a")
                .WithEnvironment("BUS_URI", "amqp://rabbit:5672")
                .WithEnvironment("MACHINES", CompanionValue.InstanceEndpoints("machine-a", 9000))
                .WithPodVolume("logs", "/var/log/app")
                .WithReadinessProbe("/health", 8080));

        var schema = builder.BuildSchema();
        var manifest = builder.BuildManifest();

        Assert.Multiple(() =>
        {
            Assert.That(schema.Companions, Has.Count.EqualTo(3));
            Assert.That(manifest.Companions, Has.Count.EqualTo(3));

            var rabbit = schema.Companions[0];
            Assert.That(rabbit.EnvironmentVariables[0].Kind, Is.EqualTo(CompanionEnvironmentVariable.RunSecret));
            Assert.That(rabbit.Readiness, Is.EqualTo(new CompanionReadinessProbe(CompanionReadinessProbe.Tcp, 5672)));

            var machine = schema.Companions[1];
            Assert.That(machine.MinInstances, Is.EqualTo(0));
            Assert.That(machine.MaxInstances, Is.EqualTo(8));
            Assert.That(machine.CountInput, Is.EqualTo("machines-a"));
            Assert.That(machine.MemoryMb, Is.EqualTo(512));
            Assert.That(machine.Cpus, Is.EqualTo(1.5));

            var manager = schema.Companions[2];
            Assert.That(manager.StartAfter, Is.EqualTo(new[] { "rabbit", "machine-a" }));
            Assert.That(manager.EnvironmentVariables[1].SourceCompanion, Is.EqualTo("machine-a"));
            Assert.That(manager.EnvironmentVariables[1].Port, Is.EqualTo(9000));
            Assert.That(manager.PodVolumes[0], Is.EqualTo(new CompanionPodVolume("logs", "/var/log/app")));
            Assert.That(manager.Readiness!.HttpPath, Is.EqualTo("/health"));
        });
    }

    [Test]
    public void RequiresCompanion_DuplicateName_Throws()
    {
        var builder = WorkflowBuilder.Create("test").RequiresCompanion("db", PinnedImage);
        Assert.That(() => builder.RequiresCompanion("db", PinnedImage),
            Throws.InvalidOperationException);
    }

    [Test]
    public void BuildSchema_UnpinnedImage_Throws()
    {
        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test")
            .RequiresCompanion("db", "postgres:17");
        Assert.That(() => builder.BuildSchema(),
            Throws.InvalidOperationException.With.Message.Contains("digest-pinned"));
    }

    [Test]
    public void BuildSchema_StartAfterCycle_Throws()
    {
        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test")
            .RequiresCompanion("a", PinnedImage, c => c.WithStartAfter("b"))
            .RequiresCompanion("b", PinnedImage, c => c.WithStartAfter("a"));
        Assert.That(() => builder.BuildSchema(),
            Throws.InvalidOperationException.With.Message.Contains("cycle"));
    }

    [Test]
    public void RequiresPodControl_RidesSchemaAndManifest_AndCountsIntoTheSpawnCap()
    {
        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test")
            .RequiresCompanion("rabbit", PinnedImage)
            .RequiresPodControl(32, "Simulated machines", "bin", "logs");

        var schema = builder.BuildSchema();
        var manifest = builder.BuildManifest();

        Assert.Multiple(() =>
        {
            Assert.That(schema.PodControl, Is.EqualTo(new PodControlDeclaration(32, "Simulated machines")
            {
                PodVolumes = new[] { "bin", "logs" }
            }) | Is.Not.Null);
            Assert.That(schema.PodControl!.MaxContainers, Is.EqualTo(32));
            Assert.That(schema.PodControl.PodVolumes, Is.EqualTo(new[] { "bin", "logs" }));
            Assert.That(manifest.PodControl, Is.Not.Null);
            Assert.That(CompanionTopologyValidator.MaxContainers(schema.Companions, schema.PodControl),
                Is.EqualTo(33), "spawn cap = declared templates + the runtime envelope");
        });
    }

    [Test]
    public void RequiresPodControl_InvalidEnvelope_ThrowsAtBuild()
    {
        var zero = (WorkflowBuilder)WorkflowBuilder.Create("test").RequiresPodControl(0);
        Assert.That(() => zero.BuildSchema(),
            Throws.InvalidOperationException.With.Message.Contains("at least 1"));

        var badVolume = (WorkflowBuilder)WorkflowBuilder.Create("test")
            .RequiresPodControl(4, podVolumes: "Bad_Volume");
        Assert.That(() => badVolume.BuildSchema(),
            Throws.InvalidOperationException.With.Message.Contains("volume name"));

        var declared = WorkflowBuilder.Create("test").RequiresPodControl(4);
        Assert.That(() => declared.RequiresPodControl(8),
            Throws.InvalidOperationException.With.Message.Contains("already been declared"));
    }

    [Test]
    public void Schema_WithPodControl_SurvivesJsonRoundtrip()
    {
        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test")
            .RequiresPodControl(16, "fleet", "bin");
        var schema = builder.BuildSchema();

        var camel = JsonSerializer.Serialize(schema,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var read = JsonSerializer.Deserialize<WorkflowSchema>(camel,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.Multiple(() =>
        {
            Assert.That(read!.PodControl!.MaxContainers, Is.EqualTo(16));
            Assert.That(read.PodControl.Description, Is.EqualTo("fleet"));
            Assert.That(read.PodControl.PodVolumes, Is.EqualTo(new[] { "bin" }));
        });
    }

    [Test]
    public void Schema_WithCompanions_SurvivesJsonRoundtrip_CaseInsensitive()
    {
        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test")
            .RequiresCompanion("rabbit", PinnedImage, c => c
                .WithEnvironment("PASS", CompanionValue.RunSecret())
                .WithScale(1, 4, "rabbits")
                .WithReadinessProbe(tcpPort: 5672)
                .WithPodVolume("logs", "/var/log"));
        var schema = builder.BuildSchema();

        // Both casings occur in the wild: the packer emits camelCase, the runner PascalCase.
        var camel = JsonSerializer.Serialize(schema,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var read = JsonSerializer.Deserialize<WorkflowSchema>(camel,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.Multiple(() =>
        {
            Assert.That(read!.Companions, Has.Count.EqualTo(1));
            Assert.That(read.Companions[0], Is.EqualTo(schema.Companions[0]) | Is.Not.Null);
            Assert.That(read.Companions[0].Name, Is.EqualTo("rabbit"));
            Assert.That(read.Companions[0].Image, Is.EqualTo(PinnedImage));
            Assert.That(read.Companions[0].CountInput, Is.EqualTo("rabbits"));
            Assert.That(read.Companions[0].Readiness!.Port, Is.EqualTo(5672));
            Assert.That(read.Companions[0].EnvironmentVariables[0].Kind,
                Is.EqualTo(CompanionEnvironmentVariable.RunSecret));
            Assert.That(read.Companions[0].PodVolumes[0].MountPath, Is.EqualTo("/var/log"));
        });
    }
}

[TestFixture]
[Category("Unit")]
public class CompanionTopologyValidatorTests
{
    private const string PinnedImage = "img@sha256:1111111111111111111111111111111111111111111111111111111111111111";

    private static CompanionDeclaration Valid(string name) => new(name, PinnedImage);

    [Test]
    public void Validate_WellFormedTopology_ReturnsNoErrors()
    {
        var companions = new[]
        {
            Valid("rabbit"),
            Valid("machine") with { MinInstances = 0, MaxInstances = 8, CountInput = "n" },
            Valid("manager") with { StartAfter = ["rabbit", "machine"] }
        };
        Assert.That(CompanionTopologyValidator.Validate(companions), Is.Empty);
    }

    [TestCase("Postgres")]
    [TestCase("1db")]
    [TestCase("db_x")]
    [TestCase("")]
    public void Validate_BadName_IsReported(string name)
    {
        var errors = CompanionTopologyValidator.Validate([Valid(name)]);
        Assert.That(errors, Has.Some.Contains("must match"));
    }

    [Test]
    public void Validate_OpenBoundsWithoutCountInput_IsReported()
    {
        var errors = CompanionTopologyValidator.Validate(
            [Valid("db") with { MinInstances = 1, MaxInstances = 3 }]);
        Assert.That(errors, Has.Some.Contains("count input"));
    }

    [Test]
    public void Validate_UnknownStartAfterAndSource_AreReported()
    {
        var errors = CompanionTopologyValidator.Validate(
        [
            Valid("a") with
            {
                StartAfter = ["ghost"],
                EnvironmentVariables =
                [
                    new CompanionEnvironmentVariable("N", CompanionEnvironmentVariable.InstanceCount)
                    {
                        SourceCompanion = "phantom"
                    }
                ]
            }
        ]);
        Assert.Multiple(() =>
        {
            Assert.That(errors, Has.Some.Contains("unknown companion 'ghost'"));
            Assert.That(errors, Has.Some.Contains("unknown companion 'phantom'"));
        });
    }

    [Test]
    public void Validate_ThreeNodeCycle_IsReported()
    {
        var errors = CompanionTopologyValidator.Validate(
        [
            Valid("a") with { StartAfter = ["c"] },
            Valid("b") with { StartAfter = ["a"] },
            Valid("c") with { StartAfter = ["b"] }
        ]);
        Assert.That(errors, Has.Some.Contains("cycle"));
    }

    [Test]
    public void Validate_EndpointsVariableWithoutPort_IsReported()
    {
        var errors = CompanionTopologyValidator.Validate(
        [
            Valid("a"),
            Valid("b") with
            {
                EnvironmentVariables =
                [
                    new CompanionEnvironmentVariable("E", CompanionEnvironmentVariable.InstanceEndpoints)
                    {
                        SourceCompanion = "a"
                    }
                ]
            }
        ]);
        Assert.That(errors, Has.Some.Contains("endpoint port"));
    }

    [Test]
    public void MaxContainers_SumsUpperBounds()
    {
        var companions = new[]
        {
            Valid("a") with { MinInstances = 0, MaxInstances = 8, CountInput = "n" },
            Valid("b")
        };
        Assert.That(CompanionTopologyValidator.MaxContainers(companions), Is.EqualTo(9));
    }
}
