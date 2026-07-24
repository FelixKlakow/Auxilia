using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// Proves the typed <see cref="ICoreClient"/> (the product/CLI consumer boundary) drives the
/// Core API end-to-end over HTTP: create configuration, dispatch, observe.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class CoreClientTests : CoreApiComponentTestBase
{
    private const string DummyType = "simple-git-commit-workflow";
    private const string DummyImage = "docker://auxilia-dummy-workflows:system-test";

    [Test]
    public async Task Client_CreateConfigurationAndRun_DispatchesThroughCore()
    {
        ICoreClient core = new CoreClient(CreateClient());

        var config = await core.CreateConfigurationAsync(new CreateRunConfiguration(
            "client-" + Guid.NewGuid().ToString("N"), DummyType, DummyImage,
            new Dictionary<string, string> { ["WORKFLOW_NAME"] = DummyType }));

        var accepted = await core.RunConfigurationAsync(config.Id);
        Assert.That(accepted.RunId, Is.Not.EqualTo(Guid.Empty));

        var command = MessageBus.PublishedMessages
            .Select(m => m.Message).OfType<RunWorkflowCommand>().Single();
        Assert.That(command.WorkflowType, Is.EqualTo(DummyType));
        Assert.That(command.WorkflowPackageUri, Is.EqualTo(DummyImage));
    }

    [Test]
    public async Task Client_GetRun_ReflectsStatusEvents()
    {
        ICoreClient core = new CoreClient(CreateClient());
        var instanceId = Guid.NewGuid();

        await MessageBus.SimulateReceivedAsync(WorkflowStatusEvent.ExchangeName,
            new WorkflowStatusEvent(instanceId, DummyType, "Success", null, DateTimeOffset.UtcNow));

        var run = await core.GetRunAsync(instanceId);
        Assert.That(run, Is.Not.Null);
        Assert.That(run!.State, Is.EqualTo("Success"));

        var page = await core.QueryRunsAsync(new RunQuery(State: "Success"));
        Assert.That(page.Items.Any(r => r.RunId == instanceId), Is.True);
    }

    [Test]
    public async Task Client_GetUnknownRun_ReturnsNull()
    {
        ICoreClient core = new CoreClient(CreateClient());
        var run = await core.GetRunAsync(Guid.NewGuid());
        Assert.That(run, Is.Null);
    }
}
