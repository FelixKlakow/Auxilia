using System.Collections.Concurrent;
using System.Net.Http.Json;
using Auxilia.Core.Contracts;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.SystemTestSuite.Failover;

/// <summary>
/// End-to-end Core.Runner failover: a long-running workflow is dispatched through the Core Run
/// API; its owning runner is killed; the <b>Core.Api</b> failover monitor detects the stale bus
/// heartbeat, fails the orphaned run over (visible Failed status event, audited, never silent),
/// and re-dispatches it once from the Core's own stored dispatch command — the surviving runner
/// picks the fresh run up from the shared command queue.
/// </summary>
[TestFixture]
[Category("System")]
public sealed class FailoverSystemTests
{
    [Test]
    [CancelAfter(240_000)]
    public async Task WhenOwningRunnerDies_RunIsFailedOverAndRedispatched(
        CancellationToken cancellationToken)
    {
        var bus = FailoverEnvironment.MessageBusClient;
        var statusEvents = new ConcurrentQueue<WorkflowStatusEvent>();

        await bus.DeclareTopicExchangeAsync(WorkflowStatusEvent.ExchangeName, cancellationToken);
        await using var subscription = await bus.SubscribeToTopicExchangeAsync<WorkflowStatusEvent>(
            WorkflowStatusEvent.ExchangeName, ["#"],
            (msg, _) => { statusEvents.Enqueue(msg); return Task.CompletedTask; },
            cancellationToken);

        // 1. Dispatch a workflow that stays Running long enough to kill its owner — via the Core Run
        //    API, so the Core stashes the dispatch command and can re-dispatch it on failover.
        var request = new RunRequest(
            "sleeping-workflow",
            new Dictionary<string, string>
            {
                ["WORKFLOW_NAME"] = "sleeping-workflow",
                ["SLEEP_SECONDS"] = "120"
            });
        var runResp = await FailoverEnvironment.CoreApiClient.PostAsJsonAsync(
            "/api/runs", request, cancellationToken);
        runResp.EnsureSuccessStatusCode();

        // 2. Wait until the run is Running and learn which runner owns it. The owner rides the
        //    claim ("Received") transition and later transitions may omit it — the documented
        //    contract is preserve-the-last-non-null-value, so it is read from the instance's
        //    event history, not from the Running event itself.
        WorkflowStatusEvent? running = null;
        while (running is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(1000, cancellationToken);
            running = statusEvents.FirstOrDefault(e =>
                e.WorkflowType == "sleeping-workflow" &&
                e.State == "Running");
        }
        var runId = running.WorkflowInstanceId;
        var ownerServiceId = statusEvents
            .Where(e => e.WorkflowInstanceId == runId)
            .Select(e => e.OwnerServiceId)
            .FirstOrDefault(id => id is not null);
        Assert.That(ownerServiceId, Is.Not.Null, "The claim transition must record the owning instance.");

        var owner1 = await FailoverEnvironment.ServiceIdOfAsync(FailoverEnvironment.Runner1);
        var ownerContainer = ownerServiceId == owner1
            ? FailoverEnvironment.Runner1
            : FailoverEnvironment.Runner2;

        // 3. Kill the owner.
        await ownerContainer.StopAsync(cancellationToken);

        // 4. The Core.Api monitor must surface the failover as a Failed status event…
        var failedEventSeen = await WaitForAsync(
            () => statusEvents.Any(e =>
                e.WorkflowInstanceId == runId &&
                e.State == "Failed" &&
                e.ErrorMessage == "steering-instance-lost"),
            TimeSpan.FromSeconds(60), cancellationToken);
        Assert.That(failedEventSeen, Is.True,
            "The failover must be published as a Failed status event — never silent.");

        // 5. …and re-dispatch the run: the surviving runner brings a NEW run to Running.
        var redispatchRunning = await WaitForAsync(
            () => statusEvents.Any(e =>
                e.WorkflowInstanceId != runId &&
                e.WorkflowType == "sleeping-workflow" &&
                e.State == "Running"),
            TimeSpan.FromSeconds(90), cancellationToken);
        if (!redispatchRunning)
        {
            var observed = string.Join("\n", statusEvents.Select(e =>
                $"  {e.TimestampUtc:HH:mm:ss} {e.WorkflowInstanceId} {e.WorkflowType} {e.State} {e.ErrorMessage}"));
            var (coreOut, coreErr) = await FailoverEnvironment.CoreApi.GetLogsAsync(ct: cancellationToken);
            var survivor = ReferenceEquals(ownerContainer, FailoverEnvironment.Runner1)
                ? FailoverEnvironment.Runner2
                : FailoverEnvironment.Runner1;
            var (siOut, siErr) = await survivor.GetLogsAsync(ct: cancellationToken);
            Assert.Fail(
                $"The re-dispatched run must reach Running on the surviving runner.\n" +
                $"Observed status events:\n{observed}\n\n" +
                $"--- Core.Api logs (tail) ---\n{Tail(coreOut + coreErr, 4000)}\n\n" +
                $"--- Surviving runner logs (tail) ---\n{Tail(siOut + siErr, 4000)}");
        }

        // Cleanup: cancel the re-dispatched sleeper so teardown can remove the network.
        var redispatched = statusEvents.First(e =>
            e.WorkflowInstanceId != runId && e.WorkflowType == "sleeping-workflow" && e.State == "Running");
        await bus.PublishAsync($"workflow-cancel-{redispatched.WorkflowInstanceId}",
            new CancelWorkflowCommand(redispatched.WorkflowInstanceId), cancellationToken);
        await Task.Delay(2000, cancellationToken);
    }

    private static string Tail(string text, int maxChars)
        => text.Length <= maxChars ? text : text[^maxChars..];

    private static async Task<bool> WaitForAsync(
        Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(500, ct);
        }
        return condition();
    }
}
