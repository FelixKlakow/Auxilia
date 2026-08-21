using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Auxilia.Core.Contracts;
using Auxilia.Workflows;
using Auxilia.Workflows.Companions;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// Companion topologies at the registry gate (run-pod design §A): a structurally invalid
/// topology — unpinned image, start-order cycle — must never become registrable, and a valid
/// one is served back through the schema endpoint as the approval's spawn summary.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class WorkflowTypeCompanionTests : CoreApiComponentTestBase
{
    private const string PinnedImage =
        "postgres@sha256:0000000000000000000000000000000000000000000000000000000000000000";

    private static string SchemaJsonWith(params CompanionDeclaration[] companions)
        => JsonSerializer.Serialize(new WorkflowSchema("pod-test", [], [])
        {
            Version = "1.0.0",
            Companions = companions
        });

    [Test]
    public async Task Register_UnpinnedCompanionImage_IsRefused()
    {
        var response = await CreateClient().PostAsJsonAsync("/api/workflow-types",
            new RegisterWorkflowTypeRequest("pod-test", "docker://pod-test:1",
                SchemaJson: SchemaJsonWith(new CompanionDeclaration("db", "postgres:17"))));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest),
            "An unpinned companion image must be refused at the spawn-grant gate.");
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("digest-pinned"));
    }

    [Test]
    public async Task Register_CompanionStartOrderCycle_IsRefused()
    {
        var response = await CreateClient().PostAsJsonAsync("/api/workflow-types",
            new RegisterWorkflowTypeRequest("pod-test", "docker://pod-test:1",
                SchemaJson: SchemaJsonWith(
                    new CompanionDeclaration("a", PinnedImage) { StartAfter = ["b"] },
                    new CompanionDeclaration("b", PinnedImage) { StartAfter = ["a"] })));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("cycle"));
    }

    [Test]
    public async Task EnvironmentBase_ImageReference_MustBeDigestPinned()
    {
        var client = CreateClient();

        var unpinned = await client.PostAsJsonAsync("/api/environment-bases",
            new UpsertEnvironmentBase("sim-base", "24.04", ImageReference: "ubuntu:24.04"));
        Assert.That(unpinned.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest),
            "the runtime-spawnable vocabulary follows the digest discipline");

        var pinned = await client.PostAsJsonAsync("/api/environment-bases",
            new UpsertEnvironmentBase("sim-base", "24.04", ImageReference: PinnedImage));
        Assert.That(pinned.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var listed = await client.GetFromJsonAsync<List<EnvironmentBaseDto>>("/api/environment-bases");
        Assert.That(listed!.Single(b => b.Name == "sim-base").ImageReference, Is.EqualTo(PinnedImage));
    }

    [Test]
    public async Task Dispatch_StampsOnlyTheConfigurationPinnedBases_IntoTheCommand()
    {
        var client = CreateClient();
        // Two image-carrying bases in the catalog; the run's configuration pins ONE.
        await client.PostAsJsonAsync("/api/environment-bases",
            new UpsertEnvironmentBase("sim-base", "24.04", ImageReference: PinnedImage));
        await client.PostAsJsonAsync("/api/environment-bases",
            new UpsertEnvironmentBase("other-base", "1", ImageReference: PinnedImage));

        await client.PostAsJsonAsync("/api/workflow-types",
            new RegisterWorkflowTypeRequest("pod-ctl", "docker://pod-ctl:1",
                SchemaJson: JsonSerializer.Serialize(new WorkflowSchema("pod-ctl", [], [])
                {
                    PodControl = new PodControlDeclaration(8)
                })));
        await client.PostAsync("/api/workflow-types/pod-ctl/approve", null);

        var run = await client.PostAsJsonAsync("/api/runs", new RunRequest("pod-ctl",
            new Dictionary<string, string> { ["pod-bases"] = "sim-base" }));
        Assert.That(run.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var command = MessageBus.PublishedMessages
            .Select(m => m.Message).OfType<RunWorkflowCommand>().Single();
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(command.PodBaseImagesJson!);
        Assert.Multiple(() =>
        {
            Assert.That(map!.Keys, Is.EquivalentTo(new[] { "sim-base" }),
                "exactly the configuration-pinned refs ride the command — never the whole catalog");
            Assert.That(map["sim-base"], Is.EqualTo(PinnedImage));
        });
    }

    [Test]
    public async Task Dispatch_WithoutPodBasesContext_StampsNoBaseMap()
    {
        var client = CreateClient();
        await client.PostAsJsonAsync("/api/environment-bases",
            new UpsertEnvironmentBase("sim-base", "24.04", ImageReference: PinnedImage));
        await client.PostAsJsonAsync("/api/workflow-types",
            new RegisterWorkflowTypeRequest("pod-ctl", "docker://pod-ctl:1",
                SchemaJson: JsonSerializer.Serialize(new WorkflowSchema("pod-ctl", [], [])
                {
                    PodControl = new PodControlDeclaration(8)
                })));
        await client.PostAsync("/api/workflow-types/pod-ctl/approve", null);

        await client.PostAsJsonAsync("/api/runs", new RunRequest("pod-ctl"));

        var command = MessageBus.PublishedMessages
            .Select(m => m.Message).OfType<RunWorkflowCommand>().Single();
        Assert.That(command.PodBaseImagesJson, Is.Null,
            "no pod-bases selection = default-deny; the runner refuses every spawn");
    }

    [Test]
    public async Task Register_PodControlEnvelope_RidesTheSpawnSummary()
    {
        var client = CreateClient();
        await client.PostAsJsonAsync("/api/workflow-types",
            new RegisterWorkflowTypeRequest("pod-ctl", "docker://pod-ctl:1",
                SchemaJson: JsonSerializer.Serialize(new WorkflowSchema("pod-ctl", [], [])
                {
                    Companions = [new CompanionDeclaration("rabbit", PinnedImage)],
                    PodControl = new PodControlDeclaration(32, "machines") { PodVolumes = ["bin"] }
                })));

        var schema = await client.GetFromJsonAsync<WorkflowSchemaDto>("/api/workflow-types/pod-ctl/schema");

        Assert.Multiple(() =>
        {
            Assert.That(schema!.PodControl!.MaxContainers, Is.EqualTo(32));
            Assert.That(schema.PodControl.PodVolumes, Is.EqualTo(new[] { "bin" }));
            Assert.That(schema.MaxPodContainers, Is.EqualTo(33),
                "the approval block shows templates + envelope as one cap");
        });
    }

    [Test]
    public async Task Register_ValidTopology_ServesTheSpawnSummaryOnTheSchema()
    {
        var client = CreateClient();
        var register = await client.PostAsJsonAsync("/api/workflow-types",
            new RegisterWorkflowTypeRequest("pod-test", "docker://pod-test:1",
                SchemaJson: SchemaJsonWith(
                    new CompanionDeclaration("rabbit", PinnedImage),
                    new CompanionDeclaration("machine", PinnedImage)
                    {
                        MinInstances = 0, MaxInstances = 8, CountInput = "machines",
                        StartAfter = ["rabbit"], MemoryMb = 512
                    })));
        Assert.That(register.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var schema = await client.GetFromJsonAsync<WorkflowSchemaDto>(
            "/api/workflow-types/pod-test/schema");

        Assert.Multiple(() =>
        {
            Assert.That(schema!.Companions, Has.Count.EqualTo(2));
            Assert.That(schema.MaxPodContainers, Is.EqualTo(9));
            var machine = schema.Companions.Single(c => c.Name == "machine");
            Assert.That(machine.Image, Is.EqualTo(PinnedImage));
            Assert.That(machine.CountInput, Is.EqualTo("machines"));
            Assert.That(machine.StartAfter, Is.EqualTo(new[] { "rabbit" }));
            Assert.That(machine.MemoryMb, Is.EqualTo(512));
        });
    }
}
