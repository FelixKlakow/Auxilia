using System.Collections.Concurrent;
using System.Net.Http.Json;
using Auxilia.Core.Contracts;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.SystemTestSuite.ReAdoption;

/// <summary>
/// Runner restart → container RE-ADOPTION (hardening wave 2026-08-04): a running workflow
/// survives its runner's restart — the workflow container keeps executing on the host daemon,
/// the restarted runner re-claims it from its persisted instance registry (container id +
/// protected instance token) before any failover clock fires, and the SAME instance completes.
/// </summary>
[TestFixture]
[Category("System")]
public sealed class ReAdoptionSystemTests
{
    [Test]
    [CancelAfter(300_000)]
    public async Task RunnerRestart_MidRun_TheSameInstanceIsReAdoptedAndCompletes(
        CancellationToken cancellationToken)
    {
        var bus = ReAdoptionEnvironment.MessageBusClient;
        var statusEvents = new ConcurrentQueue<WorkflowStatusEvent>();
        await bus.DeclareTopicExchangeAsync(WorkflowStatusEvent.ExchangeName, cancellationToken);
        await using var subscription = await bus.SubscribeToTopicExchangeAsync<WorkflowStatusEvent>(
            WorkflowStatusEvent.ExchangeName, ["#"],
            (msg, _) => { statusEvents.Enqueue(msg); return Task.CompletedTask; },
            cancellationToken);

        // 1. A run long enough to span the runner restart.
        var response = await ReAdoptionEnvironment.CoreApiClient.PostAsJsonAsync("/api/runs",
            new RunRequest("sleeping-workflow", new Dictionary<string, string>
            {
                ["WORKFLOW_NAME"] = "sleeping-workflow",
                ["SLEEP_SECONDS"] = "60"
            }), cancellationToken);
        response.EnsureSuccessStatusCode();

        var running = await WaitForAsync(
            () => statusEvents.FirstOrDefault(e =>
                e.WorkflowType == "sleeping-workflow" && e.State == "Running"),
            TimeSpan.FromSeconds(90), cancellationToken);
        Assert.That(running, Is.Not.Null, "the run must reach Running before the restart");
        var instanceId = running!.WorkflowInstanceId;

        // 2. Restart the runner mid-run. The workflow container keeps executing meanwhile.
        await ReAdoptionEnvironment.Runner.StopAsync(cancellationToken);
        await ReAdoptionEnvironment.Runner.StartAsync(cancellationToken);

        // 3. The restarted runner must RE-ADOPT the container (never clean-kill a claimable one).
        var readopted = await WaitForAsync(
            async () =>
            {
                var (stdout, stderr) = await ReAdoptionEnvironment.Runner.GetLogsAsync(ct: cancellationToken);
                var logs = stdout + stderr;
                return logs.Contains("Re-adopted running workflow container", StringComparison.Ordinal)
                       || logs.Contains("ExitsCollected=1", StringComparison.Ordinal);
            },
            TimeSpan.FromSeconds(60), cancellationToken);
        Assert.That(readopted, Is.True,
            "the restarted runner must re-adopt its previous process's container (or collect its exit)");

        // 4. The SAME instance completes — no failover, no orphan, no duplicate run.
        var succeeded = await WaitForAsync(
            () => statusEvents.FirstOrDefault(e =>
                e.WorkflowInstanceId == instanceId && e.State == "Success"),
            TimeSpan.FromSeconds(120), cancellationToken);
        if (succeeded is null)
        {
            var observed = string.Join("\n", statusEvents.Select(e =>
                $"  {e.TimestampUtc:HH:mm:ss} {e.WorkflowInstanceId} {e.WorkflowType} {e.State} {e.ErrorMessage}"));
            var (rOut, rErr) = await ReAdoptionEnvironment.Runner.GetLogsAsync(ct: cancellationToken);
            Assert.Fail(
                $"The re-adopted run must complete as the SAME instance.\n"
                + $"Observed status events:\n{observed}\n\n"
                + $"--- Runner logs (tail) ---\n{Tail(rOut + rErr, 5000)}");
        }

        Assert.Multiple(() =>
        {
            Assert.That(statusEvents.Any(e =>
                    e.WorkflowInstanceId == instanceId && e.State == "Failed"),
                Is.False, "a survivable restart must never fail the run");
            Assert.That(statusEvents
                    .Where(e => e.WorkflowType == "sleeping-workflow" && e.State == "Running")
                    .Select(e => e.WorkflowInstanceId).Distinct().Count(),
                Is.EqualTo(1), "re-adoption must not spawn a second run of the command");
        });
    }

    [Test]
    [CancelAfter(300_000)]
    public async Task ContainerDiesWhileRunnerIsDown_TheRestartCollectsTheRealExit(
        CancellationToken cancellationToken)
    {
        var bus = ReAdoptionEnvironment.MessageBusClient;
        var statusEvents = new ConcurrentQueue<WorkflowStatusEvent>();
        await bus.DeclareTopicExchangeAsync(WorkflowStatusEvent.ExchangeName, cancellationToken);
        await using var subscription = await bus.SubscribeToTopicExchangeAsync<WorkflowStatusEvent>(
            WorkflowStatusEvent.ExchangeName, ["#"],
            (msg, _) => { statusEvents.Enqueue(msg); return Task.CompletedTask; },
            cancellationToken);

        var response = await ReAdoptionEnvironment.CoreApiClient.PostAsJsonAsync("/api/runs",
            new RunRequest("sleeping-workflow", new Dictionary<string, string>
            {
                ["WORKFLOW_NAME"] = "sleeping-workflow",
                ["SLEEP_SECONDS"] = "300"
            }), cancellationToken);
        response.EnsureSuccessStatusCode();

        var running = await WaitForAsync(
            () => statusEvents.FirstOrDefault(e =>
                e.WorkflowType == "sleeping-workflow" && e.State == "Running"),
            TimeSpan.FromSeconds(90), cancellationToken);
        Assert.That(running, Is.Not.Null);
        var instanceId = running!.WorkflowInstanceId;

        // Runner down; the workflow container dies while nobody is watching (SIGKILL → 137).
        await ReAdoptionEnvironment.Runner.StopAsync(cancellationToken);
        var containerId = (await DockerAsync(
            $"ps -q --filter label=auxilia.instance-id={instanceId:D}", cancellationToken)).Trim();
        Assert.That(containerId, Is.Not.Empty, "the labeled workflow container must exist on the host");
        await DockerAsync($"kill {containerId}", cancellationToken);

        await ReAdoptionEnvironment.Runner.StartAsync(cancellationToken);

        // The restarted runner collects the REAL exit: the run fails with the container's
        // exit code — never a silent orphan, never a generic failover reason.
        var failed = await WaitForAsync(
            () => statusEvents.FirstOrDefault(e =>
                e.WorkflowInstanceId == instanceId && e.State == "Failed"),
            TimeSpan.FromSeconds(120), cancellationToken);
        if (failed is null)
        {
            var (rOut, rErr) = await ReAdoptionEnvironment.Runner.GetLogsAsync(ct: cancellationToken);
            Assert.Fail("The run must fail with the collected exit.\n--- Runner logs (tail) ---\n"
                        + Tail(rOut + rErr, 5000));
        }
        Assert.That(failed!.ErrorMessage, Does.Contain("container exited (code 137)"),
            $"the REAL exit code must surface; got: {failed.ErrorMessage}");
    }

    [Test]
    [CancelAfter(240_000)]
    public async Task UnmatchedWorkflowContainer_IsCleanKilledOnRestart(CancellationToken cancellationToken)
    {
        // A labeled workflow container NO runner record knows (e.g. its run was cleared while
        // the runner was down) must be force-removed at startup — never left running blind.
        var bogusInstanceId = Guid.NewGuid();
        await ReAdoptionEnvironment.Runner.StopAsync(cancellationToken);
        var containerId = (await DockerAsync(
            "run -d --label auxilia.workflow=1 "
            + $"--label auxilia.instance-id={bogusInstanceId:D} "
            + "--entrypoint /bin/sleep "
            + $"{WorkflowDispatch.WorkflowDispatchEnvironment.DummyWorkflowsImageName} 300",
            cancellationToken)).Trim();

        await ReAdoptionEnvironment.Runner.StartAsync(cancellationToken);

        var removed = await WaitForAsync(
            async () => string.IsNullOrWhiteSpace(await DockerAsync(
                $"ps -a -q --filter label=auxilia.instance-id={bogusInstanceId:D}", cancellationToken)),
            TimeSpan.FromSeconds(60), cancellationToken);
        if (!removed)
        {
            // Never leave the orphan behind, whatever the verdict.
            try { await DockerAsync($"rm -f {containerId}", cancellationToken); } catch { /* best effort */ }
            var (rOut, rErr) = await ReAdoptionEnvironment.Runner.GetLogsAsync(ct: cancellationToken);
            Assert.Fail("The unmatched container must be clean-killed on restart.\n"
                        + "--- Runner logs (tail) ---\n" + Tail(rOut + rErr, 5000));
        }
    }

    private static async Task<string> DockerAsync(string arguments, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("docker", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = System.Diagnostics.Process.Start(psi)
                            ?? throw new InvalidOperationException("Failed to start docker.");
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"docker {arguments} failed: {stderr}");
        return stdout;
    }

    private static string Tail(string text, int maxChars)
        => text.Length <= maxChars ? text : text[^maxChars..];

    private static async Task<T?> WaitForAsync<T>(
        Func<T?> probe, TimeSpan timeout, CancellationToken ct) where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is { } hit)
                return hit;
            await Task.Delay(500, ct);
        }
        return probe();
    }

    private static async Task<bool> WaitForAsync(
        Func<Task<bool>> probe, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe())
                return true;
            await Task.Delay(1000, ct);
        }
        return await probe();
    }
}
