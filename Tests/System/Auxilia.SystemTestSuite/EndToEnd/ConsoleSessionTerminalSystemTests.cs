using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Auxilia.Core.Contracts;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.SystemTestSuite.EndToEnd;

/// <summary>
/// The console-mode session terminal end to end: a claude-code run dispatched with
/// <c>view-mode=console</c> hosts the stub CLI under tmux+ttyd inside the workflow container,
/// the runner stamps the container-network terminal endpoint, and the Core exposes it ONLY
/// through the ticketed proxy — <c>HasTerminal</c> during the session, a short-lived ticket
/// minting the proxy URL, 401 without a ticket, and no ticket once the run is over. The stub
/// dwells via the <c>STUB_DWELL_SECONDS</c> context variable so the terminal window is stable.
/// </summary>
[TestFixture]
[Category("System")]
public sealed class ConsoleSessionTerminalSystemTests
{
    [Test]
    [CancelAfter(540_000)]
    public async Task ConsoleModeRun_HostsTheTicketedTerminal_ThroughTheCoreProxy(
        CancellationToken cancellationToken)
    {
        var bus = EndToEndEnvironment.MessageBusClient;
        var statusEvents = new ConcurrentQueue<WorkflowStatusEvent>();
        await bus.DeclareTopicExchangeAsync(WorkflowStatusEvent.ExchangeName, cancellationToken);
        await using var statusSubscription = await bus.SubscribeToTopicExchangeAsync<WorkflowStatusEvent>(
            WorkflowStatusEvent.ExchangeName, ["#"],
            (msg, _) => { statusEvents.Enqueue(msg); return Task.CompletedTask; },
            cancellationToken);

        // 0. Warm the runner's schema store: the launch-time terminal decision reads the schema
        //    a claude-code instance self-registers at startup, so the type must have run once
        //    (statically registered types carry no schema of their own). A first-ever console
        //    dispatch on a fresh runner would silently lose its terminal — tracked in the backlog.
        var warmup = await EndToEndEnvironment.CoreApiClient.PostAsJsonAsync(
            "/api/runs",
            new RunRequest(
                EndToEndEnvironment.ClaudeWorkflowType,
                new Dictionary<string, string>
                {
                    ["Title"] = "Schema warm-up",
                    ["Body"] = "Headless run so the type's schema is stored before the console run."
                },
                SlotBindings: new List<SlotBinding>
                {
                    new("coding-agent", "claude-code-cli", Settings: new Dictionary<string, string>
                    {
                        ["ApiKey"] = "e2e-stub-key",
                        ["CliPath"] = EndToEndEnvironment.ClaudeStubCliPath
                    })
                }),
            cancellationToken);
        warmup.EnsureSuccessStatusCode();
        var warmupAccepted = (await warmup.Content.ReadFromJsonAsync<RunAccepted>(cancellationToken))!;
        if (!await WaitForAsync(() => ReachedState(statusEvents, warmupAccepted.CommandId, "Success"),
                TimeSpan.FromSeconds(180), cancellationToken))
            await FailWithDiagnosticsAsync("The schema warm-up run must reach Success.", statusEvents);

        // 1. Dispatch in console mode: the view-mode choice satisfies the workflow's
        //    InteractiveTerminalGate, so the runner resolves and publishes the terminal port.
        var runResp = await EndToEndEnvironment.CoreApiClient.PostAsJsonAsync(
            "/api/runs",
            new RunRequest(
                EndToEndEnvironment.ClaudeWorkflowType,
                new Dictionary<string, string>
                {
                    ["Title"] = "Console session",
                    ["Body"] = "Operator drives the CLI live in the terminal.",
                    ["view-mode"] = "console",
                    ["STUB_DWELL_SECONDS"] = "60"
                },
                SlotBindings: new List<SlotBinding>
                {
                    new("coding-agent", "claude-code-cli", Settings: new Dictionary<string, string>
                    {
                        ["ApiKey"] = "e2e-stub-key",
                        ["CliPath"] = EndToEndEnvironment.ClaudeStubCliPath
                    })
                }),
            cancellationToken);
        runResp.EnsureSuccessStatusCode();
        var accepted = (await runResp.Content.ReadFromJsonAsync<RunAccepted>(cancellationToken))!;

        // 2. HasTerminal must become true while the session lives (endpoint stamped + not terminal).
        var status = await WaitForRunAsync(
            accepted.RunId, s => s.HasTerminal, TimeSpan.FromSeconds(180), cancellationToken);
        if (status is null)
            await FailWithDiagnosticsAsync(
                "The console-mode run must report HasTerminal while the session lives.", statusEvents);

        // 3. The proxy rejects ticketless access. (A separate client: the ticketed request below
        //    plants a path-scoped cookie in the shared handler's jar, which would mask the 401.)
        using (var anonymous = new HttpClient { BaseAddress = EndToEndEnvironment.CoreApiClient.BaseAddress })
        {
            var unticketed = await anonymous.GetAsync(
                $"/api/runs/{accepted.RunId}/terminal/", cancellationToken);
            Assert.That(unticketed.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized),
                "The terminal proxy must reject requests without a valid ticket.");
        }

        // 4. Mint a ticket and open the terminal page through the Core's proxy — the browser
        //    path: Core reaches ttyd over the container network, the client only ever sees the Core.
        var ticketResp = await EndToEndEnvironment.CoreApiClient.PostAsync(
            $"/api/runs/{accepted.RunId}/terminal-ticket", null, cancellationToken);
        Assert.That(ticketResp.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            $"Ticket minting failed: {await ticketResp.Content.ReadAsStringAsync(cancellationToken)}");
        var ticket = (await ticketResp.Content.ReadFromJsonAsync<TerminalTicket>(cancellationToken))!;

        // HasTerminal is stamped at container start; ttyd inside only listens once the SDK app
        // reaches the console session — poll through that warm-up window (the proxy surfaces a
        // not-yet-listening upstream as 500).
        var page = await EndToEndEnvironment.CoreApiClient.GetAsync(ticket.Url, cancellationToken);
        var pageDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (page.StatusCode != HttpStatusCode.OK && DateTime.UtcNow < pageDeadline)
        {
            await Task.Delay(1000, cancellationToken);
            page = await EndToEndEnvironment.CoreApiClient.GetAsync(ticket.Url, cancellationToken);
        }
        Assert.Multiple(async () =>
        {
            Assert.That(page.StatusCode, Is.EqualTo(HttpStatusCode.OK),
                "The ticketed proxy request must reach ttyd's page.");
            Assert.That(await page.Content.ReadAsStringAsync(cancellationToken), Is.Not.Empty);
            Assert.That(page.Headers.TryGetValues("Set-Cookie", out var cookies)
                        && cookies.Any(c => c.Contains("auxilia-terminal-ticket")),
                Is.True, "The first page response must plant the path-scoped ticket cookie.");
        });

        // 5. The run completes when the stub's dwell elapses and the CLI exits. The claim
        //    transition (command id + a real instance id) pins the instance; terminal events
        //    are then matched by instance, never by type alone.
        var completed = await WaitForAsync(
            () => ReachedState(statusEvents, accepted.CommandId, "Success"),
            TimeSpan.FromSeconds(180), cancellationToken);
        if (!completed)
            await FailWithDiagnosticsAsync("The console-mode run must reach Success.", statusEvents);

        // 6. A terminal run mints no tickets: the session is gone.
        var lateTicket = await EndToEndEnvironment.CoreApiClient.PostAsync(
            $"/api/runs/{accepted.RunId}/terminal-ticket", null, cancellationToken);
        Assert.That(lateTicket.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest),
            "A completed run must not mint terminal tickets.");
    }

    /// <summary>Claim-pinned terminal-state match: the claim transition (command id + a real
    /// instance id) pins the instance; states are then matched by instance, never type alone.</summary>
    private static bool ReachedState(
        ConcurrentQueue<WorkflowStatusEvent> statusEvents, Guid commandId, string state)
    {
        var instanceId = statusEvents
            .FirstOrDefault(e => e.CommandId == commandId && e.WorkflowInstanceId != commandId)
            ?.WorkflowInstanceId;
        return instanceId is { } id && statusEvents.Any(e =>
            e.WorkflowInstanceId == id && e.State == state);
    }

    private static async Task<RunStatus?> WaitForRunAsync(
        Guid runId, Func<RunStatus, bool> predicate, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var status = await EndToEndEnvironment.CoreApiClient
                .GetFromJsonAsync<RunStatus>($"/api/runs/{runId}", ct);
            if (status is not null && predicate(status))
                return status;
            await Task.Delay(500, ct);
        }
        return null;
    }

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

    private static async Task FailWithDiagnosticsAsync(
        string message, ConcurrentQueue<WorkflowStatusEvent> statusEvents)
    {
        var observed = statusEvents.IsEmpty
            ? "  <none>"
            : string.Join("\n", statusEvents.Select(e =>
                $"  {e.TimestampUtc:HH:mm:ss} {e.WorkflowInstanceId} {e.WorkflowType} {e.State} {e.ErrorMessage}"));
        var runnerLogs = await EndToEndEnvironment.LogTailAsync(EndToEndEnvironment.Runner);
        Assert.Fail(
            $"{message}\n" +
            $"Observed status events:\n{observed}\n\n" +
            $"--- Runner logs (tail) ---\n{runnerLogs}");
    }
}
