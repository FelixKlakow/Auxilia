using System.Net;
using System.Net.Http.Json;
using Auxilia.Core.Contracts;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// Component tests for the Core run pipeline: configuration create-and-run (the "configured on
/// the fly" path), inline run, connector secret redaction, and the bus-driven run view.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class CoreApiRunTests : CoreApiComponentTestBase
{
    private const string DummyType = "simple-git-commit-workflow";
    private const string DummyImage = "docker://auxilia-dummy-workflows:system-test";

    [Test]
    public async Task CreateConfiguration_ThenRun_PublishesResolvedRunCommand()
    {
        var client = CreateClient();

        var create = new CreateRunConfiguration(
            Name: "dyn-" + Guid.NewGuid().ToString("N"),
            WorkflowType: DummyType,
            PackageUri: DummyImage,
            Context: new Dictionary<string, string> { ["WORKFLOW_NAME"] = DummyType });
        var createResp = await client.PostAsJsonAsync("/api/configurations", create);
        Assert.That(createResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var config = await createResp.Content.ReadFromJsonAsync<RunConfiguration>();
        Assert.That(config, Is.Not.Null);

        var runResp = await client.PostAsync($"/api/configurations/{config!.Id}/run", null);
        Assert.That(runResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var command = MessageBus.PublishedMessages
            .Where(m => m.Topic == "workflow.run-commands")
            .Select(m => m.Message).OfType<RunWorkflowCommand>()
            .Single();
        Assert.That(command.WorkflowType, Is.EqualTo(DummyType));
        Assert.That(command.WorkflowPackageUri, Is.EqualTo(DummyImage));
        Assert.That(command.Context["WORKFLOW_NAME"], Is.EqualTo(DummyType));
    }

    [Test]
    public async Task RunInline_PublishesRunCommand()
    {
        var client = CreateClient();

        var request = new RunRequest(DummyType, DummyImage,
            new Dictionary<string, string> { ["WORKFLOW_NAME"] = DummyType });
        var response = await client.PostAsJsonAsync("/api/runs", request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var accepted = await response.Content.ReadFromJsonAsync<RunAccepted>();
        Assert.That(accepted, Is.Not.Null);

        var command = MessageBus.PublishedMessages
            .Select(m => m.Message).OfType<RunWorkflowCommand>().Single();
        Assert.That(command.WorkflowType, Is.EqualTo(DummyType));
    }

    [Test]
    public async Task StatusEvent_UpdatesRunView()
    {
        var client = CreateClient();
        var instanceId = Guid.NewGuid();

        await MessageBus.SimulateReceivedAsync(
            WorkflowStatusEvent.ExchangeName,
            new WorkflowStatusEvent(instanceId, DummyType, "Success", null, DateTimeOffset.UtcNow));

        var status = await client.GetFromJsonAsync<RunStatus>($"/api/runs/{instanceId}");
        Assert.That(status, Is.Not.Null);
        Assert.That(status!.State, Is.EqualTo("Success"));
        Assert.That(status.WorkflowType, Is.EqualTo(DummyType));

        var page = await client.GetFromJsonAsync<PagedResult<RunStatus>>(
            $"/api/runs?state=Success&workflowType={DummyType}");
        Assert.That(page!.Items.Any(r => r.RunId == instanceId), Is.True);
    }

    [Test]
    public async Task RunUnknownConfiguration_Returns404()
    {
        var client = CreateClient();
        var response = await client.PostAsync($"/api/configurations/{Guid.NewGuid()}/run", null);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task CreateConnector_ReadReturnsKeysNotSecretValues()
    {
        var client = CreateClient();

        var create = new CreateConnector("gh", "github",
            new Dictionary<string, string> { ["token"] = "super-secret-value" });
        var createResp = await client.PostAsJsonAsync("/api/connectors", create);
        Assert.That(createResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var connector = await createResp.Content.ReadFromJsonAsync<Connector>();
        Assert.That(connector!.SettingKeys, Does.Contain("token"));

        var raw = await (await client.GetAsync($"/api/connectors/{connector.Id}"))
            .Content.ReadAsStringAsync();
        Assert.That(raw, Does.Not.Contain("super-secret-value"),
            "Connector read endpoints must never expose secret values.");
    }
}
