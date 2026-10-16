using System.Net.Http.Json;
using Auxilia.Core.Contracts;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.SystemTestSuite.CoreApiDispatch;

/// <summary>
/// The central, tamper-evident audit trail across real services: a dispatched run's runner-side
/// containment records (broker identity issued/revoked, egress policy) arrive in the Core's
/// audit with their source stamped, and the Core's integrity check verifies the whole chain —
/// anchored by a signed checkpoint outside the store.
/// </summary>
[TestFixture]
[Category("System")]
public sealed class AuditTrailSystemTests
{
    private static HttpClient Client => CoreApiDispatchEnvironment.CoreApiClient;

    [Test]
    [CancelAfter(180_000)]
    public async Task RunnerContainmentRecords_ReachTheCoresChainedTrail(CancellationToken cancellationToken)
    {
        // A run the runner contains (identity, egress) — its records are forwarded to the Core.
        var state = new TaskCompletionSource<WorkflowStateMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await CoreApiDispatchEnvironment.MessageBusClient.DeclareExchangeAsync("workflow.state", cancellationToken);
        await using var _ = await CoreApiDispatchEnvironment.MessageBusClient.SubscribeToExchangeAsync<WorkflowStateMessage>(
            "workflow.state", (m, _) => { state.TrySetResult(m); return Task.CompletedTask; }, cancellationToken);
        var create = new CreateRunConfiguration(
            Name: "audit-" + Guid.NewGuid().ToString("N"),
            WorkflowType: CoreApiDispatchEnvironment.DummyWorkflowType,
            Context: new Dictionary<string, string> { ["WORKFLOW_NAME"] = CoreApiDispatchEnvironment.DummyWorkflowType });
        var config = await (await Client.PostAsJsonAsync("/api/configurations", create, cancellationToken))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<RunConfiguration>(cancellationToken);
        (await Client.PostAsync($"/api/configurations/{config!.Id}/run", null, cancellationToken)).EnsureSuccessStatusCode();
        var ended = await state.Task.WaitAsync(TimeSpan.FromSeconds(120), cancellationToken);
        Assert.That(ended.State, Is.EqualTo(WorkflowState.Success));

        // The runner's identity records for THIS run show up centrally, attributed to the runner.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        PagedResult<AuditEntry>? issued = null;
        while (DateTime.UtcNow < deadline)
        {
            issued = await Client.GetFromJsonAsync<PagedResult<AuditEntry>>(
                $"/api/audit?action=workflow.bus-identity.issued&subject={ended.WorkflowInstanceId}", cancellationToken);
            if (issued!.Items.Count > 0)
                break;
            await Task.Delay(500, cancellationToken);
        }
        Assert.That(issued!.Items, Has.Count.EqualTo(1), "the runner's audit record must reach the Core's trail");
        Assert.That(issued.Items[0].Source, Does.StartWith("core-runner:"));

        // The chain is anchored outside the store (signed checkpoint) within a few intervals.
        AuditIntegrityReport? integrity = null;
        var anchorDeadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < anchorDeadline)
        {
            integrity = await Client.GetFromJsonAsync<AuditIntegrityReport>("/api/audit/integrity", cancellationToken);
            if (integrity!.AnchoredChains > 0)
                break;
            await Task.Delay(1000, cancellationToken);
        }
        Assert.That(integrity!.Intact, Is.True, string.Join("; ", integrity.Breaks.Select(b => b.Problem)));
        Assert.That(integrity.Records, Is.GreaterThan(0));
        Assert.That(integrity.AnchoredChains, Is.GreaterThan(0), "the Core's chain must be anchored by a checkpoint");
    }
}
