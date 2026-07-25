using System.Net.Http.Json;
using Auxilia.Core.Contracts;
using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.SystemTestSuite.CoreApiDispatch;

/// <summary>
/// The per-run repository path end to end on real Docker: a connector holds ONLY the git credential
/// (not the repo); a run supplies the repo URL and references that connector. The Core resolves the
/// credential just-in-time to the runner, which clones the AUTHENTICATED repo — a wrong or missing
/// credential would get a 401 — and bind-mounts it. The no-slot verifier workflow reaches
/// <see cref="WorkflowState.Success"/> only if the repo's README.md is present in the workspace, so
/// Success proves the whole chain: connector-auth resolution, credential injection, and clone+mount.
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
        // 1. Configure git auth: a connector holding ONLY the credential.
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

        // 2. Start a run against exactly that repo, referencing the auth connector for its credential.
        var run = new RunRequest(
            WorkflowType: CoreApiDispatchEnvironment.RepositoryWorkflowType,
            PackageUri: CoreApiDispatchEnvironment.DummyPackageUri,
            Context: new Dictionary<string, string>
            {
                ["WORKFLOW_NAME"] = CoreApiDispatchEnvironment.RepositoryWorkflowType,
                ["EXPECTED_REPO_FILE"] = "repos/main/README.md",
                ["EXPECTED_REPO_CONTENT"] = "auxilia system test repo"
            },
            Repositories:
            [
                // NoCache: clone straight into the run workspace. (The warm-cache copy trips over
                // git's read-only pack .idx on Docker Desktop's bind mount — a host-FS quirk, not a
                // product issue: WorkspaceManagerTests proves the copy path on a normal filesystem.)
                new RepositorySpec("main", CoreApiDispatchEnvironment.RepositoryCloneUrl,
                    AuthConnectorId: connector!.Id, NoCache: true)
            ]);
        var runResp = await Client.PostAsJsonAsync("/api/runs", run, cancellationToken);
        runResp.EnsureSuccessStatusCode();

        // 3. Success requires the authenticated clone to have landed in the mounted workspace.
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
