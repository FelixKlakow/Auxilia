using System.Text.Json;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.SystemTestSuite.CoreClientSurface;

/// <summary>
/// Drives the FULL <c>ICoreClient</c> surface against the real Dockerized Core — runs actually
/// execute in containers, streams ride real SSE sockets, artifacts ride real RabbitMQ topic
/// routing. Complements the component tests (in-proc, fake bus) with the network truth; the
/// stream-resilience scenarios live in <see cref="CoreClientStreamSystemTests"/>.
/// </summary>
[TestFixture]
[Category("System")]
public sealed class CoreClientSurfaceSystemTests
{
    private static ICoreClient Admin => _admin ??= CoreClientEnvironment.CreateClient();
    private static ICoreClient? _admin;

    [Test]
    [CancelAfter(60_000)]
    public async Task Identity_Health_Roles_And_Subjects(CancellationToken ct)
    {
        Assert.That(await Admin.CheckHealthAsync(ct), Is.True);

        var me = await Admin.GetCurrentPrincipalAsync(ct);
        Assert.Multiple(() =>
        {
            Assert.That(me.Roles, Does.Contain("Administrator"));
            Assert.That(me.Permissions, Is.Not.Empty);
        });

        var roles = await Admin.ListRolesAsync(ct);
        Assert.That(roles.Select(r => r.Name),
            Is.SupersetOf(new[] { "Administrator", "Operator", "User", "Auditor" }));

        var subjects = await Admin.GetSharingSubjectsAsync(ct);
        Assert.That(subjects, Is.Not.Null);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Principals_KeyAuth_Roles_Tags_StepUp_Disable(CancellationToken ct)
    {
        var created = await Admin.CreateApiKeyPrincipalAsync(
            new CreateApiKeyPrincipalRequest("client-surface probe"), ct);
        Assert.That(created.ApiKey, Is.Not.Empty);

        // The one-shot key authenticates a fresh typed client as that principal.
        var probe = CoreClientEnvironment.CreateClient(created.ApiKey);
        var probeMe = await probe.GetCurrentPrincipalAsync(ct);
        Assert.That(probeMe.PrincipalId, Is.EqualTo(created.Principal.Id));

        await Admin.AssignPrincipalRoleAsync(created.Principal.Id, new AssignRoleRequest("Operator"), ct);
        await Admin.SetPrincipalTagsAsync(created.Principal.Id,
            new SetPrincipalTagsRequest(["system-test", "probe"]), ct);
        var read = await Admin.GetPrincipalAsync(created.Principal.Id, ct);
        Assert.Multiple(() =>
        {
            Assert.That(read!.Roles.Select(r => r.RoleName), Does.Contain("Operator"));
            Assert.That(read.Tags, Is.EquivalentTo(new[] { "system-test", "probe" }));
        });

        var queried = await Admin.QueryPrincipalsAsync(new PrincipalQuery(Search: "client-surface probe"), ct);
        Assert.That(queried.Items.Select(p => p.Id), Does.Contain(created.Principal.Id));

        // Disabling is security-sensitive: step-up with the caller's OWN credential first, then
        // the disabled key must be rejected across the surface.
        await Admin.StepUpAsync(new StepUpRequest(CoreClientEnvironment.BootstrapApiKey), ct);
        await Admin.SetPrincipalEnabledAsync(created.Principal.Id, new SetPrincipalEnabledRequest(false), ct);
        Assert.ThrowsAsync<CoreApiException>(() => probe.GetCurrentPrincipalAsync(ct));

        await Admin.SetPrincipalEnabledAsync(created.Principal.Id, new SetPrincipalEnabledRequest(true), ct);
        Assert.That((await probe.GetCurrentPrincipalAsync(ct)).PrincipalId, Is.EqualTo(created.Principal.Id));

        await Admin.RevokePrincipalRoleAsync(created.Principal.Id, "Operator", ct);
        var afterRevoke = await Admin.GetPrincipalAsync(created.Principal.Id, ct);
        Assert.That(afterRevoke!.Roles.Select(r => r.RoleName), Does.Not.Contain("Operator"));
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Groups_And_DirectoryMappings(CancellationToken ct)
    {
        var member = await Admin.CreateApiKeyPrincipalAsync(
            new CreateApiKeyPrincipalRequest("group member probe"), ct);
        var group = await Admin.CreateGroupAsync(new CreateGroupRequest("client-surface-group"), ct);
        await Admin.AddGroupMemberAsync(group.Id, new AddGroupMemberRequest(member.Principal.Id), ct);
        await Admin.AssignGroupRoleAsync(group.Id, new AssignGroupRoleRequest("User"), ct);

        var groups = await Admin.ListGroupsAsync(ct);
        Assert.That(groups.Select(g => g.Id), Does.Contain(group.Id));

        // Group-assigned roles surface on the member with the Group source.
        var read = await Admin.GetPrincipalAsync(member.Principal.Id, ct);
        Assert.That(read!.Roles.Any(r => r is { RoleName: "User", Source: "Group" }),
            "the group's role must cascade to the member");

        var mapping = await Admin.CreateGroupMappingAsync(
            new CreateGroupMappingRequest("test-idp", "grp-claim-42", "User"), ct);
        Assert.That((await Admin.ListGroupMappingsAsync(ct)).Select(m => m.Id), Does.Contain(mapping.Id));
        await Admin.RemoveGroupMappingAsync(mapping.Id, ct);
        Assert.That((await Admin.ListGroupMappingsAsync(ct)).Select(m => m.Id), Does.Not.Contain(mapping.Id));

        Assert.DoesNotThrowAsync(() => Admin.ListIdentityConnectorsAsync(ct));
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task HumanPrincipal_PasswordLogin_MintsUserBearer(CancellationToken ct)
    {
        var human = await Admin.CreateHumanPrincipalAsync(
            new CreateHumanPrincipalRequest("Test Human", "test.human", "a-strong-test-pw-42!"), ct);

        var bearer = await Admin.LoginAsync(new PasswordLoginRequest("test.human", "a-strong-test-pw-42!"), ct);
        Assert.That(bearer.ExpiresUtc, Is.GreaterThan(DateTimeOffset.UtcNow));

        var asHuman = CoreClientEnvironment.CreateClient(bearer.Token);
        var me = await asHuman.GetCurrentPrincipalAsync(ct);
        Assert.That(me.PrincipalId, Is.EqualTo(human.Id));
    }

    [Test]
    [CancelAfter(120_000)]
    public async Task Configurations_Crud_Grants_And_ConfiguredRun(CancellationToken ct)
    {
        var created = await Admin.CreateConfigurationAsync(new CreateRunConfiguration(
            "client-surface echo", CoreClientEnvironment.EchoWorkflowType,
            Context: CoreClientEnvironment.ContextFor(CoreClientEnvironment.EchoWorkflowType, ("MESSAGE", "from the typed client")),
            Tags: ["#test"]), ct);

        Assert.That((await Admin.GetConfigurationAsync(created.Id, ct))!.Name, Is.EqualTo("client-surface echo"));

        var renamed = await Admin.UpdateConfigurationAsync(created.Id,
            new UpdateRunConfiguration(Name: "client-surface echo v2"), ct);
        Assert.That(renamed.Name, Is.EqualTo("client-surface echo v2"));

        var queried = await Admin.QueryConfigurationsAsync(
            new ConfigurationQuery(WorkflowType: CoreClientEnvironment.EchoWorkflowType), ct);
        Assert.That(queried.Items.Select(c => c.Id), Does.Contain(created.Id));

        var grantee = await Admin.CreateApiKeyPrincipalAsync(new CreateApiKeyPrincipalRequest("config grantee"), ct);
        var granted = await Admin.SetConfigurationGrantsAsync(created.Id,
            new SetConfigurationGrants([new AccessGrant("Principal", grantee.Principal.Id.ToString())]), ct);
        Assert.That(granted.Grants, Is.Not.Null.And.Not.Empty);

        var accepted = await Admin.RunConfigurationAsync(created.Id, ct: ct);
        var (state, frames) = await ClientStreamProbe.AwaitTerminalAsync(Admin, accepted.RunId, ct);
        Assert.Multiple(() =>
        {
            Assert.That(state, Is.EqualTo("Success"));
            Assert.That(frames.ViewPayloads, Has.Some.Contains("from the typed client"));
        });

        await Admin.DeleteConfigurationAsync(created.Id, ct);
        Assert.That(await Admin.GetConfigurationAsync(created.Id, ct), Is.Null);
    }

    [Test]
    [CancelAfter(180_000)]
    public async Task Runs_Dispatch_Observe_Views_Stats_Pins_Rerun_Clear(CancellationToken ct)
    {
        var accepted = await Admin.RunAsync(new RunRequest(
            CoreClientEnvironment.EchoWorkflowType,
            CoreClientEnvironment.ContextFor(CoreClientEnvironment.EchoWorkflowType, ("MESSAGE", "surface run"))), ct);

        var (state, frames) = await ClientStreamProbe.AwaitTerminalAsync(Admin, accepted.RunId, ct);
        Assert.Multiple(() =>
        {
            Assert.That(state, Is.EqualTo("Success"));
            Assert.That(frames.ConnectAttempts.First(), Is.EqualTo(1), "first frame is the initial Connected");
            Assert.That(frames.FirstEventKind, Is.EqualTo(RunStreamEvent.StatusKind),
                "the first event after subscribe is the server's status snapshot");
            Assert.That(frames.ViewSequences, Is.Unique, "view frames are deduped by sequence");
            // Live view frames published in the claim→alias-binding instant may ride only the
            // persisted store (asserted complete below) — liveness means SOME arrived, not all.
            Assert.That(frames.ViewPayloads.Count(p => p.Contains("surface run")), Is.GreaterThanOrEqualTo(1));
        });

        var status = await ClientStreamProbe.GetRunResolvedAsync(Admin, accepted, ct);
        Assert.Multiple(() =>
        {
            Assert.That(status.State, Is.EqualTo("Success"));
            Assert.That(status.CompletedUtc, Is.Not.Null);
        });

        var page = await Admin.QueryRunsAsync(
            new RunQuery(WorkflowType: CoreClientEnvironment.EchoWorkflowType), ct);
        Assert.That(page.Items, Is.Not.Empty);
        var runId = status.RunId;

        var views = await Admin.GetRunViewsAsync(runId, ct: ct);
        Assert.That(views.Items.Count(v => v.PayloadJson.Contains("surface run")), Is.EqualTo(3));

        var stats = await Admin.GetRunStatsAsync(ct);
        Assert.That(stats.ByState.GetValueOrDefault("Success"), Is.GreaterThan(0));

        var pin = await Admin.PinDashboardViewAsync(new CreateDashboardPin(runId, "output"), ct);
        Assert.That((await Admin.ListDashboardPinsAsync(ct)).Select(p => p.Id), Does.Contain(pin.Id));
        await Admin.UnpinDashboardViewAsync(pin.Id, ct);
        Assert.That((await Admin.ListDashboardPinsAsync(ct)).Select(p => p.Id), Does.Not.Contain(pin.Id));

        // A finished non-terminal-capable run has no web terminal to open.
        Assert.ThrowsAsync<CoreApiException>(() => Admin.OpenTerminalAsync(runId, ct));

        var rerun = await Admin.RerunAsync(runId, ct);
        var (rerunState, _) = await ClientStreamProbe.AwaitTerminalAsync(Admin, rerun.RunId, ct);
        Assert.That(rerunState, Is.EqualTo("Success"));

        Assert.That(await Admin.ClearFinishedRunsAsync(ct), Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    [CancelAfter(120_000)]
    public async Task Runs_CancelInterruptsASleepingRun(CancellationToken ct)
    {
        var accepted = await Admin.RunAsync(new RunRequest(
            CoreClientEnvironment.SleepingWorkflowType,
            CoreClientEnvironment.ContextFor(CoreClientEnvironment.SleepingWorkflowType, ("SLEEP_SECONDS", "300"))), ct);

        await ClientStreamProbe.AwaitStateAsync(Admin, accepted.RunId, "Running", ct);
        await Admin.CancelRunAsync(accepted.RunId, ct);

        var (state, _) = await ClientStreamProbe.AwaitTerminalAsync(Admin, accepted.RunId, ct);
        Assert.That(state, Is.AnyOf("Cancelled", "Failed"),
            "a hard cancel must end the run loudly, never leave it Running");
    }

    [Test]
    [CancelAfter(120_000)]
    public async Task Steering_ProvideInput_DecidesTheProposedAction(CancellationToken ct)
    {
        var accepted = await Admin.RunAsync(new RunRequest(CoreClientEnvironment.EchoDecisionWorkflowType,
            CoreClientEnvironment.ContextFor(CoreClientEnvironment.EchoDecisionWorkflowType)), ct);

        // Live view frames raced into the claim window can be lost BY DESIGN — the persisted
        // views are the loss-free record, and real clients backfill from them. Drive the
        // interaction the same way: read the proposal from the store, decide over the wire.
        string? actionId = null;
        await WaitUntilAsync(async () =>
        {
            var views = await Admin.GetRunViewsAsync(accepted.RunId, ct: ct);
            if (views.Items.FirstOrDefault(v => v.PayloadJson.Contains("action-proposed")) is not { } proposal)
                return false;
            using var doc = JsonDocument.Parse(proposal.PayloadJson);
            actionId = doc.RootElement.GetProperty("actionId").GetString();
            return true;
        }, TimeSpan.FromSeconds(60), ct);
        Assert.That(actionId, Is.Not.Null, "the workflow must have proposed an action");

        await Admin.ProvideInputAsync(accepted.RunId,
            $$"""{"$type":"action-decision","actionId":"{{actionId}}","outcome":0,"rationale":"system test approves"}""",
            ct);

        // The decision's consequences land loss-free in the persisted views and the run record.
        await WaitUntilAsync(async () =>
        {
            var views = await Admin.GetRunViewsAsync(accepted.RunId, ct: ct);
            return views.Items.Any(v => v.PayloadJson.Contains("session-ended"));
        }, TimeSpan.FromSeconds(60), ct);

        var persisted = (await Admin.GetRunViewsAsync(accepted.RunId, ct: ct)).Items;
        Assert.Multiple(() =>
        {
            Assert.That(persisted.Select(v => v.PayloadJson), Has.Some.Contains("action-resolved"),
                "the decision must reach the workflow and consume the proposal");
            Assert.That(persisted.Select(v => v.PayloadJson), Has.Some.Contains("APPROVED"),
                "the approval must be echoed by the workflow");
        });

        var (state, _) = await ClientStreamProbe.AwaitTerminalAsync(Admin, accepted.RunId, ct);
        Assert.That(state, Is.EqualTo("Success"));
    }

    [Test]
    [CancelAfter(120_000)]
    public async Task Registry_Lifecycle_GovernsDispatch(CancellationToken ct)
    {
        const string probeType = "client-surface-probe-type";

        // A docker:// package carries no verifiable signature — always Pending until signed.
        var registered = await Admin.RegisterWorkflowTypeAsync(
            new RegisterWorkflowTypeRequest(probeType, PackageUri: CoreClientEnvironment.DummyPackageUri), ct);
        Assert.That(registered.Status, Is.EqualTo("Pending"));

        var approved = await Admin.ApproveWorkflowTypeAsync(probeType, ct);
        Assert.That(approved.Status, Is.EqualTo("Active"));
        Assert.That((await Admin.ListWorkflowTypesAsync(new WorkflowTypeQuery(Status: "Active"), ct))
            .Items.Select(t => t.WorkflowType), Does.Contain(probeType));

        // Operational off-switch: a disabled type must refuse to dispatch.
        await Admin.SetWorkflowTypeEnabledAsync(probeType, false, ct);
        Assert.ThrowsAsync<CoreApiException>(() => Admin.RunAsync(new RunRequest(probeType), ct));
        await Admin.SetWorkflowTypeEnabledAsync(probeType, true, ct);

        await Admin.UnregisterWorkflowTypeAsync(probeType, ct);
        Assert.That(await Admin.GetWorkflowTypeRegistrationAsync(probeType, ct), Is.Null);

        // The deny path records the signing authority's reason.
        const string deniedType = "client-surface-denied-type";
        await Admin.RegisterWorkflowTypeAsync(
            new RegisterWorkflowTypeRequest(deniedType, PackageUri: CoreClientEnvironment.DummyPackageUri), ct);
        var denied = await Admin.DenyWorkflowTypeAsync(deniedType, "not trusted by the system test", ct);
        Assert.Multiple(() =>
        {
            Assert.That(denied.Status, Is.EqualTo("Denied"));
            Assert.That(denied.StatusReason, Is.EqualTo("not trusted by the system test"));
        });
        await Admin.UnregisterWorkflowTypeAsync(deniedType, ct);

        // The statically registered echo type exposes its schema (populated by its declaration).
        Assert.DoesNotThrowAsync(() => Admin.GetWorkflowSchemaAsync(CoreClientEnvironment.EchoWorkflowType, ct));
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task ProviderCatalog_Layers_And_Bases(CancellationToken ct)
    {
        const string providerType = "client-surface-probe-provider";
        var entry = await Admin.RegisterProviderAsync(new RegisterSlotProvider(
            providerType, "test", "system-test probe provider",
            Contracts: ["test-contract"],
            Settings: [new RegisterProviderSetting("token", "Token", "secret", Required: true)]), ct);
        Assert.That(entry.ProviderType, Is.EqualTo(providerType));

        var available = await Admin.SetProviderAvailabilityAsync(providerType, true, ct);
        Assert.That(available, Is.Not.Null);
        Assert.DoesNotThrowAsync(() => Admin.SetProviderSettingDisabledAsync(providerType, "token", true, ct));
        Assert.That((await Admin.QueryProviderCatalogAsync(new ProviderCatalogQuery(), ct))
            .Items.Select(p => p.ProviderType), Does.Contain(providerType));
        await Admin.DeleteProviderAsync(providerType, ct);

        var upserted = await Admin.UpsertEnvironmentBaseAsync(
            new UpsertEnvironmentBase("linux", "client-surface-24.04", "probe base"), ct);
        Assert.That(upserted.Version, Is.EqualTo("client-surface-24.04"));
        Assert.That((await Admin.ListEnvironmentBasesAsync(ct)).Select(b => b.Version),
            Does.Contain("client-surface-24.04"));
        await Admin.DeleteEnvironmentBaseAsync("linux", "client-surface-24.04", ct);

        const string layerType = "client-surface-probe-env";
        var layer = await Admin.UpsertEnvironmentLayerAsync(
            new UpsertEnvironmentLayer(layerType, "probe env", "echo layer-setup"), ct);
        Assert.That(layer.SetupScript, Is.EqualTo("echo layer-setup"));
        Assert.That((await Admin.GetEnvironmentLayerAsync(layerType, ct))!.BaseEnvironment, Is.EqualTo("linux"));
        Assert.That((await Admin.ListEnvironmentLayersAsync(ct)).Select(l => l.ProviderType), Does.Contain(layerType));
        await Admin.DeleteEnvironmentLayerAsync(layerType, ct);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Connectors_And_Workspaces_Crud_WithGrants(CancellationToken ct)
    {
        var connector = await Admin.CreateConnectorAsync(new CreateConnector(
            "client-surface connector", "credential-probe",
            new Dictionary<string, string> { ["token"] = "secret-material" }), ct);

        // Secrets never come back — only which keys are configured.
        var readBack = await Admin.GetConnectorAsync(connector.Id, ct);
        Assert.That(readBack!.SettingKeys, Does.Contain("token"));

        var updated = await Admin.UpdateConnectorAsync(connector.Id,
            new UpdateConnector(Settings: new Dictionary<string, string> { ["extra"] = "value" }), ct);
        Assert.That(updated.SettingKeys, Is.SupersetOf(new[] { "token", "extra" }));

        Assert.That((await Admin.QueryConnectorsAsync(new ConnectorQuery(ProviderType: "credential-probe"), ct))
            .Items.Select(c => c.Id), Does.Contain(connector.Id));

        var grantee = await Admin.CreateApiKeyPrincipalAsync(new CreateApiKeyPrincipalRequest("connector grantee"), ct);
        await Admin.SetConnectorGrantsAsync(connector.Id,
            new SetConnectorGrants([new AccessGrant("Principal", grantee.Principal.Id.ToString())]), ct);

        var workspace = await Admin.CreateWorkspaceAsync(new CreateWorkspaceResource(
            "client-surface repo", "git-repository",
            new Dictionary<string, string> { ["clone-url"] = "http://example.invalid/repo.git" },
            ConnectorId: connector.Id), ct);
        Assert.That((await Admin.GetWorkspaceAsync(workspace.Id, ct))!.ConnectorId, Is.EqualTo(connector.Id));

        var renamed = await Admin.UpdateWorkspaceAsync(workspace.Id, new UpdateWorkspaceResource(
            "client-surface repo v2", workspace.Settings, connector.Id), ct);
        Assert.That(renamed.Name, Is.EqualTo("client-surface repo v2"));
        Assert.That((await Admin.ListWorkspacesAsync(ct)).Select(w => w.Id), Does.Contain(workspace.Id));
        await Admin.SetWorkspaceGrantsAsync(workspace.Id,
            new SetWorkspaceGrants([new AccessGrant("Principal", grantee.Principal.Id.ToString())]), ct);

        await Admin.DeleteWorkspaceAsync(workspace.Id, ct);
        Assert.That(await Admin.GetWorkspaceAsync(workspace.Id, ct), Is.Null);
        await Admin.DeleteConnectorAsync(connector.Id, ct);
        Assert.That(await Admin.GetConnectorAsync(connector.Id, ct), Is.Null);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task PlatformSettings_ListAndSet(CancellationToken ct)
    {
        var settings = await Admin.ListPlatformSettingsAsync(ct);
        Assert.That(settings, Is.Not.Null);
        if (settings.Count == 0)
            Assert.Ignore("no platform settings are exposed by this Core build");

        await Admin.StepUpAsync(new StepUpRequest(CoreClientEnvironment.BootstrapApiKey), ct);
        var key = settings[0].Key;
        var written = await Admin.SetPlatformSettingAsync(key, new SetPlatformSetting(settings[0].Value), ct);
        Assert.That(written.Key, Is.EqualTo(key));
    }

    [Test]
    [CancelAfter(120_000)]
    public async Task Artifacts_TopicFilteredStream_Query_And_Catchup(CancellationToken ct)
    {
        var bus = CoreClientEnvironment.MessageBusClient;
        await bus.DeclareTopicExchangeAsync(ArtifactPersistedEvent.ExchangeName, ct);

        // Subscribe FILTERED first (real topic routing — the fake bus cannot verify this),
        // then produce artifacts of two types over the real broker.
        const string wanted = "client-surface-report";
        const string unwanted = "client-surface-noise";
        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var received = new List<ArtifactStreamEvent>();
        var streamTask = Task.Run(async () =>
        {
            await foreach (var frame in Admin.StreamArtifactEventsAsync(wanted, ct: streamCts.Token))
                if (frame is StreamEventFrame<ArtifactStreamEvent> evt)
                    received.Add(evt.Event);
        }, CancellationToken.None);
        await Task.Delay(2000, ct); // let the SSE subscription and its bus binding settle

        var t0 = DateTimeOffset.UtcNow;
        var older = NewArtifact(wanted, "WI-1", t0.AddSeconds(-2));
        var newer = NewArtifact(wanted, "WI-2", t0.AddSeconds(2));
        var noise = NewArtifact(unwanted, "WI-3", t0);
        foreach (var evt in new[] { older, newer, noise })
            await bus.PublishToTopicExchangeAsync(ArtifactPersistedEvent.ExchangeName,
                ArtifactPersistedEvent.RoutingKeyFor(evt.ArtifactType), evt, ct);

        // The filtered stream must deliver both wanted artifacts and NEITHER noise artifact.
        await WaitUntilAsync(() => received.Count >= 2, TimeSpan.FromSeconds(20), ct);
        streamCts.Cancel();
        try { await streamTask; } catch (OperationCanceledException) { }
        Assert.Multiple(() =>
        {
            Assert.That(received.Select(e => e.Artifact.ArtifactType), Is.All.EqualTo(wanted));
            Assert.That(received.Select(e => e.Artifact.Id),
                Is.EquivalentTo(new[] { older.ArtifactId, newer.ArtifactId }));
        });

        // Query: newest-first by default, oldest-first in the createdAfterUtc catch-up shape.
        var byType = await Admin.QueryArtifactsAsync(new ArtifactQuery(ArtifactType: wanted), ct);
        Assert.That(byType.Items.Select(a => a.Id), Is.SupersetOf(new[] { older.ArtifactId, newer.ArtifactId }));

        var catchup = await Admin.QueryArtifactsAsync(
            new ArtifactQuery(ArtifactType: wanted, CreatedAfterUtc: t0.AddSeconds(-10)), ct);
        Assert.That(catchup.Items.Select(a => a.CreatedUtc), Is.Ordered.Ascending,
            "catch-up pages oldest-first so a gap drains deterministically");

        Assert.That((await Admin.GetArtifactAsync(older.ArtifactId, ct))!.WorkItemId, Is.EqualTo("WI-1"));
        // Bus-mirrored metadata has no payload behind it — the content read reports absence, not an error.
        Assert.That(await Admin.OpenArtifactContentAsync(older.ArtifactId, ct), Is.Null);

        static ArtifactPersistedEvent NewArtifact(string type, string workItem, DateTimeOffset at)
            => new(Guid.NewGuid(), type, "client-surface-producer", workItem, Guid.NewGuid(), 1, "HASH", 42, at);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Audit_RecordsTheAdministration(CancellationToken ct)
    {
        var page = await Admin.QueryAuditAsync(new AuditQuery(Take: 50), ct);
        Assert.That(page.Items, Is.Not.Empty, "administration above must have produced audit entries");

        var filtered = await Admin.QueryAuditAsync(new AuditQuery(Action: page.Items[0].Action, Take: 10), ct);
        Assert.That(filtered.Items.Select(e => e.Action), Is.All.EqualTo(page.Items[0].Action));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
                return; // let the caller's assertion report the actual state
            await Task.Delay(250, ct);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
                return; // let the caller's assertion report the actual state
            await Task.Delay(500, ct);
        }
    }
}
