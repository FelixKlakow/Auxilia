using System.Net;
using System.Net.Http.Json;
using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// Component tests for the failover pipeline end-to-end through the real Core.Api DI graph and bus:
/// a run is dispatched (its command stashed), the runner claims it over the bus (owner + command id),
/// the runner's bus heartbeat then goes stale, and the <see cref="FailoverMonitor"/> fails the orphaned
/// run over — cancel + Failed(steering-instance-lost) + one guarded re-dispatch — all from the Core's
/// own store, never a runner database. Time is controlled so the scan is deterministic.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class FailoverMonitorComponentTests : CoreApiComponentTestBase
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private const string DummyType = "simple-git-commit-workflow";
    private const string DummyImage = "docker://auxilia-dummy-workflows:system-test";

    private readonly ManualTimeProvider _time = new();

    protected override void ConfigureHost(IWebHostBuilder builder)
    {
        builder.UseSetting("CoreApi:HeartbeatTimeoutSeconds", "30");
        // A huge scan interval parks the background loop (its timer runs on wall-clock) so the test
        // drives ScanOnceAsync deterministically instead of racing a periodic sweep.
        builder.UseSetting("CoreApi:FailoverScanIntervalSeconds", "3600");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(_time);
        });
    }

    /// <summary>Dispatches a run and returns (dispatch CommandId, the request context used).</summary>
    private async Task<Guid> DispatchRunAsync(HttpClient client, IDictionary<string, string>? context = null)
    {
        var request = new RunRequest(DummyType, DummyImage,
            new Dictionary<string, string>(context ?? new Dictionary<string, string> { ["WORKFLOW_NAME"] = DummyType }));
        var response = await client.PostAsJsonAsync("/api/runs", request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        return MessageBus.PublishedMessages
            .Where(m => m.Topic == "workflow.run-commands")
            .Select(m => m.Message).OfType<RunWorkflowCommand>()
            .Last().CommandId;
    }

    /// <summary>Simulates the runner claiming the run over the bus (stamps owner + command id).</summary>
    private Task ClaimAsync(Guid instanceId, Guid serviceId, Guid commandId) =>
        MessageBus.SimulateReceivedAsync(WorkflowStatusEvent.ExchangeName,
            new WorkflowStatusEvent(instanceId, DummyType, "Received", null, _time.Now,
                OwnerServiceId: serviceId, CommandId: commandId));

    private Task BeatAsync(Guid serviceId) =>
        MessageBus.SimulateReceivedAsync(RunnerHeartbeat.ExchangeName,
            new RunnerHeartbeat(serviceId, "Auxilia.Core.Runner", _time.Now));

    private Task ScanAsync() =>
        Factory.Services.GetRequiredService<FailoverMonitor>().ScanOnceAsync(CancellationToken.None);

    [Test]
    public async Task SilentRunner_PastTimeout_FailsOverItsRun_CancelsAndRedispatchesOnce()
    {
        var client = CreateClient();
        var serviceId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();

        var commandId = await DispatchRunAsync(client);
        await BeatAsync(serviceId);
        await ClaimAsync(instanceId, serviceId, commandId);

        // The runner goes silent: advance past the timeout without another beat, then scan.
        _time.Now += TimeSpan.FromSeconds(31);
        await ScanAsync();

        var status = await client.GetFromJsonAsync<RunStatus>($"/api/runs/{instanceId}");
        Assert.Multiple(() =>
        {
            Assert.That(status!.State, Is.EqualTo("Failed"));
            Assert.That(status.Error, Is.EqualTo("steering-instance-lost"));
            Assert.That(MessageBus.PublishedMessages.Any(p =>
                    p.Topic == $"workflow-cancel-{instanceId}" && p.Message is CancelWorkflowCommand),
                Is.True, "A cancel must be published to the run's cancel queue.");
        });

        var redispatch = MessageBus.PublishedMessages
            .Where(p => p.Topic == "workflow.run-commands")
            .Select(p => p.Message).OfType<RunWorkflowCommand>()
            .Where(c => c.Context.ContainsKey(FailoverMonitor.FailoverContextKey))
            .ToList();
        Assert.That(redispatch, Has.Count.EqualTo(1),
            "The orphaned run must be re-dispatched exactly once, guarded by FAILOVER_REDISPATCH.");
    }

    [Test]
    public async Task LiveRunner_RunIsUntouched()
    {
        var client = CreateClient();
        var serviceId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();

        var commandId = await DispatchRunAsync(client);
        await ClaimAsync(instanceId, serviceId, commandId);

        // The runner is still beating: advance time but refresh the beat within the timeout window.
        _time.Now += TimeSpan.FromSeconds(31);
        await BeatAsync(serviceId);
        await ScanAsync();

        var status = await client.GetFromJsonAsync<RunStatus>($"/api/runs/{instanceId}");
        Assert.That(status!.State, Is.EqualTo("Received"));
        Assert.That(MessageBus.PublishedMessages.Any(p => p.Topic == $"workflow-cancel-{instanceId}"),
            Is.False, "A live runner's run must not be cancelled.");
    }

    [Test]
    public async Task FailoverRedispatch_ThatDiesAgain_IsNotRedispatchedASecondTime()
    {
        var client = CreateClient();
        var serviceId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();

        // This run was itself a failover re-dispatch (carries the guard key) — the guard must stop a loop.
        var commandId = await DispatchRunAsync(client, new Dictionary<string, string>
        {
            ["WORKFLOW_NAME"] = DummyType,
            [FailoverMonitor.FailoverContextKey] = Guid.NewGuid().ToString("D")
        });
        await BeatAsync(serviceId);
        await ClaimAsync(instanceId, serviceId, commandId);

        _time.Now += TimeSpan.FromSeconds(31);
        await ScanAsync();

        var status = await client.GetFromJsonAsync<RunStatus>($"/api/runs/{instanceId}");
        Assert.That(status!.State, Is.EqualTo("Failed"), "It is still failed and cancelled...");

        var loopRedispatch = MessageBus.PublishedMessages
            .Where(p => p.Topic == "workflow.run-commands")
            .Select(p => p.Message).OfType<RunWorkflowCommand>()
            .Count(c => c.CommandId != commandId);
        Assert.That(loopRedispatch, Is.Zero,
            "...but a run that was already a failover re-dispatch must never be re-dispatched again.");
    }
}
