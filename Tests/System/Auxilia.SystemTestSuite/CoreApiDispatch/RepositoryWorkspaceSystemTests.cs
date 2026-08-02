using System.Net.Http.Json;
using Auxilia.Core.Contracts;
using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.SystemTestSuite.CoreApiDispatch;

/// <summary>
/// The generic workspace-mount path end to end on real Docker: a connector holds ONLY the git
/// credential (not the repo); the run binds a slot to a MOUNT provider (registered in the catalog
/// with role-tagged settings) that references that connector. The Core translates the binding to a
/// workspace mount as pure data — key→role — and the runner resolves the credential just-in-time,
/// clones the AUTHENTICATED repo (a wrong or missing credential would get a 401), and bind-mounts
/// it. The no-slot verifier workflow reaches <see cref="WorkflowState.Success"/> only if the repo's
/// README.md is present in the workspace, so Success proves the whole chain: catalog role mapping,
/// connector-auth resolution, credential injection, and clone+mount.
/// </summary>
[TestFixture]
[Category("System")]
public sealed class RepositoryWorkspaceSystemTests
{
    private static HttpClient Client => CoreApiDispatchEnvironment.CoreApiClient;
    private static IMessageBusClient Bus => CoreApiDispatchEnvironment.MessageBusClient;

    [Test]
    [CancelAfter(180_000)]
    public async Task AuthenticatedRepository_ResolvedAndClonedThroughCore_ToSuccess(CancellationToken cancellationToken)
    {
        // 1. Register the mount provider: its settings carry the ROLES the runner's git
        //    materializer understands — the Core only maps keys to roles, it interprets nothing.
        var registerProvider = new RegisterSlotProvider(
            ProviderType: "git-repository",
            Category: "workspace",
            Description: "A git repository materialized into the run's workspace.",
            Contracts: ["Auxilia.Workflows.SourceControl.ISourceControlAccess"],
            Settings:
            [
                new RegisterProviderSetting("CloneUrl", "Repository", "Text", Required: true, Role: "clone-url"),
                new RegisterProviderSetting("Branch", "Branch", "Text", Role: "branch"),
                new RegisterProviderSetting("NoCache", "Fresh clone per run", "Boolean", Role: "no-cache"),
            ],
            RequiredCredentialContract: "git-credential",
            MountsIntoWorkspace: true);
        var providerResp = await Client.PostAsJsonAsync("/api/provider-catalog", registerProvider, cancellationToken);
        providerResp.EnsureSuccessStatusCode();

        // 2. Configure git auth: a connector holding ONLY the credential.
        var createConnector = new CreateConnector(
            Name: "git-auth-" + Guid.NewGuid().ToString("N"),
            ProviderType: "azure-devops",
            Settings: new Dictionary<string, string>
            {
                ["username"] = CoreApiDispatchEnvironment.GitUsername,
                ["token"] = CoreApiDispatchEnvironment.GitPassword
            });
        var connectorResp = await Client.PostAsJsonAsync("/api/connectors", createConnector, cancellationToken);
        connectorResp.EnsureSuccessStatusCode();
        var connector = await connectorResp.Content.ReadFromJsonAsync<Connector>(cancellationToken);
        Assert.That(connector, Is.Not.Null);

        await using var success = await SubscribeSuccessAsync(cancellationToken);

        // 3. Start a run binding the slot to that repo through the mount provider — inline
        //    non-secret settings plus the credential connector, the ONE generic binding shape.
        //    (NoCache: clone straight into the run workspace. The warm-cache copy trips over
        //    git's read-only pack .idx on Docker Desktop's bind mount — a host-FS quirk, not a
        //    product issue: WorkspaceManagerTests proves the copy path on a normal filesystem.)
        var run = new RunRequest(
            WorkflowType: CoreApiDispatchEnvironment.RepositoryWorkflowType,
            Context: new Dictionary<string, string>
            {
                ["WORKFLOW_NAME"] = CoreApiDispatchEnvironment.RepositoryWorkflowType,
                ["EXPECTED_REPO_FILE"] = "repos/main/README.md",
                ["EXPECTED_REPO_CONTENT"] = "auxilia system test repo"
            },
            SlotBindings:
            [
                new SlotBinding("main", ProviderType: "git-repository", ConnectorId: connector!.Id,
                    Settings: new Dictionary<string, string>
                    {
                        ["CloneUrl"] = CoreApiDispatchEnvironment.RepositoryCloneUrl,
                        ["NoCache"] = "true"
                    })
            ]);
        var runResp = await Client.PostAsJsonAsync("/api/runs", run, cancellationToken);
        runResp.EnsureSuccessStatusCode();

        // 4. Success requires the authenticated clone to have landed in the mounted workspace.
        var state = await success.Task.WaitAsync(TimeSpan.FromSeconds(150), cancellationToken);
        Assert.That(state.State, Is.EqualTo(WorkflowState.Success),
            $"Repository run ended {state.State}. Error: {state.ErrorMessage}");
    }

    private static async Task<SuccessWaiter> SubscribeSuccessAsync(CancellationToken ct)
    {
        const string stateExchange = "workflow.state";
        await Bus.DeclareExchangeAsync(stateExchange, ct);
        var tcs = new TaskCompletionSource<WorkflowStateMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = await Bus.SubscribeToExchangeAsync<WorkflowStateMessage>(
            stateExchange, (msg, _) => { tcs.TrySetResult(msg); return Task.CompletedTask; }, ct);
        return new SuccessWaiter(tcs.Task, subscription);
    }

    private sealed record SuccessWaiter(
        Task<WorkflowStateMessage> Task, IAsyncDisposable Subscription) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Subscription.DisposeAsync();
    }
}
