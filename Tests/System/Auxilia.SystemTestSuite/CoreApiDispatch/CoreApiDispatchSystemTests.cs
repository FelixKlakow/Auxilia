using System.Net.Http.Json;
using Auxilia.Core.Contracts;
using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.SystemTestSuite.CoreApiDispatch;

/// <summary>
/// Acceptance tests for the Core: a dummy workflow is run to completion through Core.Api both
/// configured-on-the-fly (dynamic) and from a startup-seeded (static) configuration.
///
/// End-to-end path per test:
/// <c>Core.Api REST → RunWorkflowCommand → Runner → DockerWorkflowLauncher → dummy
/// container → WorkflowStateMessage(Success) → WorkflowStatusEvent → Core.Api run view</c>.
/// </summary>
[TestFixture]
[Category("System")]
public class CoreApiDispatchSystemTests
{
    private static HttpClient Client => CoreApiDispatchEnvironment.CoreApiClient;
    private static IMessageBusClient Bus => CoreApiDispatchEnvironment.MessageBusClient;

    [Test]
    [CancelAfter(150_000)]
    public async Task DynamicConfiguration_RunsDummyWorkflowToSuccess(CancellationToken cancellationToken)
    {
        await using var success = await SubscribeSuccessAsync(cancellationToken);

        // Configure on the fly through the Core API.
        var create = new CreateRunConfiguration(
            Name: "dyn-" + Guid.NewGuid().ToString("N"),
            WorkflowType: CoreApiDispatchEnvironment.DummyWorkflowType,
            Context: new Dictionary<string, string>
            {
                ["WORKFLOW_NAME"] = CoreApiDispatchEnvironment.DummyWorkflowType
            });
        var createResp = await Client.PostAsJsonAsync("/api/configurations", create, cancellationToken);
        createResp.EnsureSuccessStatusCode();
        var config = await createResp.Content.ReadFromJsonAsync<RunConfiguration>(cancellationToken);
        Assert.That(config, Is.Not.Null);

        var runResp = await Client.PostAsync($"/api/configurations/{config!.Id}/run", null, cancellationToken);
        runResp.EnsureSuccessStatusCode();

        var state = await success.Task.WaitAsync(TimeSpan.FromSeconds(120), cancellationToken);
        Assert.That(state.State, Is.EqualTo(WorkflowState.Success),
            $"Dynamic dummy run ended {state.State}. Error: {state.ErrorMessage}");

        await AssertCoreTracksSuccessAsync(cancellationToken);
    }

    [Test]
    [CancelAfter(150_000)]
    public async Task StaticConfiguration_RunsDummyWorkflowToSuccess(CancellationToken cancellationToken)
    {
        await using var success = await SubscribeSuccessAsync(cancellationToken);

        // The static configuration was seeded into the Core at startup from host settings.
        var page = await Client.GetFromJsonAsync<PagedResult<RunConfiguration>>(
            "/api/configurations", cancellationToken);
        var config = page!.Items.SingleOrDefault(c => c.Name == CoreApiDispatchEnvironment.StaticConfigurationName);
        Assert.That(config, Is.Not.Null, "Static configuration must be seeded at startup.");

        var runResp = await Client.PostAsync($"/api/configurations/{config!.Id}/run", null, cancellationToken);
        runResp.EnsureSuccessStatusCode();

        var state = await success.Task.WaitAsync(TimeSpan.FromSeconds(120), cancellationToken);
        Assert.That(state.State, Is.EqualTo(WorkflowState.Success),
            $"Static dummy run ended {state.State}. Error: {state.ErrorMessage}");

        await AssertCoreTracksSuccessAsync(cancellationToken);
    }

    /// <summary>Subscribes fresh to the workflow.state exchange; completes on the next terminal message.</summary>
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

    /// <summary>Polls the Core run view until it records a Success run of the dummy type.</summary>
    private static async Task AssertCoreTracksSuccessAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var page = await Client.GetFromJsonAsync<PagedResult<RunStatus>>(
                $"/api/runs?state=Success&workflowType={CoreApiDispatchEnvironment.DummyWorkflowType}", ct);
            if (page!.Items.Count > 0)
                return;
            await Task.Delay(1000, ct);
        }

        Assert.Fail("Core.Api did not record a Success run in its run view within 30s.");
    }

    private sealed record SuccessWaiter(
        Task<WorkflowStateMessage> Task, IAsyncDisposable Subscription) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Subscription.DisposeAsync();
    }
}
