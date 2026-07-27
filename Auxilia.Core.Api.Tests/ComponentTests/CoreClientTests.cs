using System.Net;
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

    [SetUp]
    public Task RegisterDummyTypeAsync() => RegisterActiveTypeAsync(CreateClient(), DummyType, DummyImage);


    [Test]
    public async Task Client_CreateConfigurationAndRun_DispatchesThroughCore()
    {
        ICoreClient core = new CoreClient(CreateClient());

        var config = await core.CreateConfigurationAsync(new CreateRunConfiguration(
            "client-" + Guid.NewGuid().ToString("N"), DummyType,
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

    [Test]
    public async Task Client_Connector_GetAndSetGrants_RoundTrips()
    {
        ICoreClient core = new CoreClient(CreateClient());
        var connector = await core.CreateConnectorAsync(new CreateConnector(
            "conn-" + Guid.NewGuid().ToString("N"), "github",
            new Dictionary<string, string> { ["token"] = "x" }, ConnectorScope.Personal));

        var fetched = await core.GetConnectorAsync(connector.Id);
        Assert.That(fetched!.Scope, Is.EqualTo(ConnectorScope.Personal));

        await core.SetConnectorGrantsAsync(connector.Id,
            new SetConnectorGrants([new ConnectorGrant(ConnectorGrantKind.DirectoryGroup, "group-devs")]));

        var afterGrant = await core.GetConnectorAsync(connector.Id);
        Assert.That(afterGrant!.Grants, Has.One.Matches<ConnectorGrant>(g => g.Id == "group-devs"));
    }

    [Test]
    public async Task Client_Groups_CreateListMemberRole_RoundTrips()
    {
        ICoreClient core = new CoreClient(CreateClient());
        var me = await core.GetCurrentPrincipalAsync();

        var group = await core.CreateGroupAsync(new CreateGroupRequest($"g-{Guid.NewGuid():N}"));
        await core.AddGroupMemberAsync(group.Id, new AddGroupMemberRequest(me.PrincipalId));
        await core.AssignGroupRoleAsync(group.Id, new AssignGroupRoleRequest("Operator"));

        var stored = (await core.ListGroupsAsync()).Single(g => g.Id == group.Id);
        Assert.Multiple(() =>
        {
            Assert.That(stored.Members, Does.Contain(me.PrincipalId));
            Assert.That(stored.Roles, Does.Contain("Operator"));
        });
    }

    [Test]
    public async Task Client_GroupMappings_CreateListRemove_RoundTrips()
    {
        ICoreClient core = new CoreClient(CreateClient());
        var mapping = await core.CreateGroupMappingAsync(
            new CreateGroupMappingRequest("entra", $"grp-{Guid.NewGuid():N}", "Operator"));

        Assert.That((await core.ListGroupMappingsAsync()).Any(m => m.Id == mapping.Id), Is.True);

        await core.RemoveGroupMappingAsync(mapping.Id);
        Assert.That((await core.ListGroupMappingsAsync()).Any(m => m.Id == mapping.Id), Is.False);
    }

    [Test]
    public async Task Client_CurrentPrincipal_IsTheAuthenticatedAdministrator()
    {
        var me = await new CoreClient(CreateClient()).GetCurrentPrincipalAsync();
        Assert.Multiple(() =>
        {
            Assert.That(me.PrincipalId, Is.Not.EqualTo(Guid.Empty));
            Assert.That(me.Roles, Does.Contain("Administrator"));
        });
    }

    [Test]
    public async Task Client_CheckHealth_ReturnsTrue()
        => Assert.That(await new CoreClient(CreateClient()).CheckHealthAsync(), Is.True);

    [Test]
    public void Client_NonSuccess_ThrowsCoreApiException_WithStatusAndDetail()
    {
        ICoreClient core = new CoreClient(CreateClient());
        var ex = Assert.CatchAsync<CoreApiException>(() =>
            core.CreateGroupMappingAsync(new CreateGroupMappingRequest("entra", "g", "Wizard")));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(ex.ErrorDetail, Does.Contain("Wizard"), "The server's error detail is surfaced.");
        });
    }

    [Test]
    public void Client_Unauthenticated_ThrowsCoreApiException_Unauthorized()
    {
        ICoreClient core = new CoreClient(CreateAnonymousClient());
        var ex = Assert.CatchAsync<CoreApiException>(() => core.GetCurrentPrincipalAsync());
        Assert.That(ex!.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }
}
