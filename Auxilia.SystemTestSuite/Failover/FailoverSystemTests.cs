using System.Collections.Concurrent;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.UniversalDataAccess.Settings;
using Auxilia.Workflows.Messaging.Messages;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.SystemTestSuite.Failover;

/// <summary>
/// End-to-end Steering Instance failover: a long-running workflow's owning instance is
/// killed; the Backend Service detects the stale heartbeat, fails the orphaned run over
/// (visible status event, audited, never silent), and re-dispatches it once — the surviving
/// instance picks the fresh run up from the shared command queue.
/// </summary>
[TestFixture]
[Category("System")]
public sealed class FailoverSystemTests
{
    [Test]
    [CancelAfter(240_000)]
    public async Task WhenOwningSteeringInstanceDies_RunIsFailedOverAndRedispatched(
        CancellationToken cancellationToken)
    {
        var bus = FailoverEnvironment.MessageBusClient;
        var statusEvents = new ConcurrentQueue<WorkflowStatusEvent>();

        await bus.DeclareExchangeAsync(WorkflowStatusEvent.ExchangeName, cancellationToken);
        await using var subscription = await bus.SubscribeToExchangeAsync<WorkflowStatusEvent>(
            WorkflowStatusEvent.ExchangeName,
            (msg, _) => { statusEvents.Enqueue(msg); return Task.CompletedTask; },
            cancellationToken);

        // Direct read access to the shared platform state (same Mongo the services use).
        var services = new ServiceCollection().AddMongoDbStorage(
            new MongoDbSettings<WorkflowInstanceRecord>
            {
                ConnectionString = FailoverEnvironment.MongoConnectionString,
                DatabaseName = "Auxilia"
            });
        await using var provider = services.BuildServiceProvider();
        var instances = provider.GetRequiredService<IDataAccess<WorkflowInstanceRecord>>();

        // 1. Dispatch a workflow that stays Running long enough to kill its owner.
        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "sleeping-workflow",
            $"docker://{WorkflowDispatch.WorkflowDispatchEnvironment.DummyWorkflowsImageName}",
            new Dictionary<string, string>
            {
                ["WORKFLOW_NAME"] = "sleeping-workflow",
                ["SLEEP_SECONDS"] = "120"
            });
        await bus.PublishAsync(FailoverEnvironment.CommandQueue, command, cancellationToken);

        // 2. Wait until the run is Running and find out which instance owns it.
        WorkflowInstanceRecord? run = null;
        while (run is null || run.State != "Running")
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(1000, cancellationToken);
            var query = await instances.ReadAsync(cancellationToken);
            run = query.FirstOrDefault(r => r.WorkflowType == "sleeping-workflow");
        }
        Assert.That(run.OwnerServiceId, Is.Not.Null, "The dispatcher must record the owning instance.");

        var owner1 = await FailoverEnvironment.ServiceIdOfAsync(FailoverEnvironment.SteeringInstance1);
        var ownerContainer = run.OwnerServiceId == owner1
            ? FailoverEnvironment.SteeringInstance1
            : FailoverEnvironment.SteeringInstance2;

        // 3. Kill the owner.
        await ownerContainer.StopAsync(cancellationToken);

        // 4. The Backend Service must surface the failover as a status event…
        var failedEventSeen = await WaitForAsync(
            () => statusEvents.Any(e =>
                e.WorkflowInstanceId == run.Id &&
                e.State == "Failed" &&
                e.ErrorMessage == "steering-instance-lost"),
            TimeSpan.FromSeconds(60), cancellationToken);
        Assert.That(failedEventSeen, Is.True,
            "The failover must be published as a Failed status event — never silent.");

        // 5. …and re-dispatch the run: the surviving instance brings a NEW run to Running.
        var redispatchRunning = await WaitForAsync(
            () => statusEvents.Any(e =>
                e.WorkflowInstanceId != run.Id &&
                e.WorkflowType == "sleeping-workflow" &&
                e.State == "Running"),
            TimeSpan.FromSeconds(90), cancellationToken);
        if (!redispatchRunning)
        {
            var observed = string.Join("\n", statusEvents.Select(e =>
                $"  {e.TimestampUtc:HH:mm:ss} {e.WorkflowInstanceId} {e.WorkflowType} {e.State} {e.ErrorMessage}"));
            var (beOut, beErr) = await FailoverEnvironment.Backend.GetLogsAsync(ct: cancellationToken);
            var survivor = ReferenceEquals(ownerContainer, FailoverEnvironment.SteeringInstance1)
                ? FailoverEnvironment.SteeringInstance2
                : FailoverEnvironment.SteeringInstance1;
            var (siOut, siErr) = await survivor.GetLogsAsync(ct: cancellationToken);
            Assert.Fail(
                $"The re-dispatched run must reach Running on the surviving instance.\n" +
                $"Observed status events:\n{observed}\n\n" +
                $"--- Backend logs (tail) ---\n{Tail(beOut + beErr, 4000)}\n\n" +
                $"--- Surviving SI logs (tail) ---\n{Tail(siOut + siErr, 4000)}");
        }

        // Cleanup: cancel the re-dispatched sleeper so teardown can remove the network.
        var redispatched = statusEvents.First(e =>
            e.WorkflowInstanceId != run.Id && e.WorkflowType == "sleeping-workflow" && e.State == "Running");
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
