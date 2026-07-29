using Auxilia.Workflows.AiAgent.CodingAgent;

namespace Auxilia.ClaudeCode.Workflow.Tests.UnitTests;

[TestFixture, Category("Unit")]
public sealed class DrivenConsoleSessionTests
{
    private sealed class ScriptedHost : ISessionHost
    {
        private readonly TaskCompletionSource _sessionEnd = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Journal { get; } = [];

        /// <summary>Simulates the CLI dying — the tmux session (and its server) is gone.</summary>
        public void EndSession() => _sessionEnd.TrySetResult();

        public Task StartAsync(TerminalSessionInfo session, CancellationToken ct)
        {
            Journal.Add("host-start");
            return Task.CompletedTask;
        }

        public async Task WaitForSessionEndAsync(CancellationToken ct)
        {
            await using var cancel = ct.Register(() => _sessionEnd.TrySetCanceled(ct));
            await _sessionEnd.Task;
        }

        public Task SendTextAsync(string text, CancellationToken ct)
        {
            Journal.Add($"send:{text}");
            return Task.CompletedTask;
        }

        public Task ShutdownAsync()
        {
            Journal.Add("shutdown");
            return Task.CompletedTask;
        }
    }

    private sealed class ManualEvents : IConsoleSessionEventSource
    {
        public Func<ConsoleSessionEvent, CancellationToken, Task>? Handler { get; private set; }
        public List<string> Journal { get; } = [];

        public Task StartAsync(Func<ConsoleSessionEvent, CancellationToken, Task> onEvent, CancellationToken ct)
        {
            Journal.Add("events-start");
            Handler = onEvent;
            return Task.CompletedTask;
        }

        public ValueTask StopAsync()
        {
            Journal.Add("events-stop");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingPreparer : IConsoleSessionPreparer
    {
        public List<string> Journal { get; }

        public RecordingPreparer(List<string> journal) => Journal = journal;

        public Task PrepareAsync(string workspaceDirectory, CancellationToken ct)
        {
            Journal.Add("prepare");
            return Task.CompletedTask;
        }
    }

    private static TerminalSessionInfo Session()
        => new(Path.GetTempPath(), "claude", 7681);

    [Test]
    public async Task Start_RunsEventsThenPreparerThenHost()
    {
        var host = new ScriptedHost();
        var events = new ManualEvents();
        // A shared journal proves the ORDER: the preparer needs the event source's endpoint.
        var journal = host.Journal;
        events.Journal.Clear();
        await using var session = new DrivenConsoleSession(
            host, new RecordingPreparer(journal), events);

        await session.StartAsync(Session(), CancellationToken.None);

        Assert.That(journal, Is.EqualTo(new[] { "prepare", "host-start" }));
        Assert.That(events.Handler, Is.Not.Null, "the event source was started first");
    }

    [Test]
    public async Task Drive_SendsThePrompt_AndCompletesOnTurnEnded()
    {
        var host = new ScriptedHost();
        var events = new ManualEvents();
        var forwarded = new List<ConsoleSessionEvent>();
        await using var session = new DrivenConsoleSession(host, null, events,
            (evt, _) =>
            {
                forwarded.Add(evt);
                return Task.CompletedTask;
            });
        await session.StartAsync(Session(), CancellationToken.None);

        var drive = session.DriveAsync("write the plan", CancellationToken.None);
        Assert.That(drive.IsCompleted, Is.False, "the turn is awaited, not fire-and-forget");

        await events.Handler!(
            new ConsoleSessionEvent(ConsoleSessionEvent.ToolStarted, "Bash running"), CancellationToken.None);
        Assert.That(drive.IsCompleted, Is.False, "tool activity is not turn completion");

        await events.Handler!(
            new ConsoleSessionEvent(ConsoleSessionEvent.TurnEnded, "Plan written."), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(drive.Result, Is.EqualTo("Plan written."));
            Assert.That(host.Journal, Does.Contain("send:write the plan"));
            Assert.That(forwarded.Select(e => e.Kind),
                Is.EqualTo(new[] { ConsoleSessionEvent.ToolStarted, ConsoleSessionEvent.TurnEnded }),
                "every event still reaches the view-publishing callback");
        });
    }

    [Test]
    public async Task Drive_SessionDiesMidTurn_ThrowsInsteadOfHangingForever()
    {
        var host = new ScriptedHost();
        var events = new ManualEvents();
        await using var session = new DrivenConsoleSession(host, null, events);
        await session.StartAsync(Session(), CancellationToken.None);

        var drive = session.DriveAsync("write the plan", CancellationToken.None);
        Assert.That(drive.IsCompleted, Is.False);

        // The CLI crashes — tmux kills the server, the Stop event never comes.
        host.EndSession();

        var error = Assert.ThrowsAsync<InvalidOperationException>(() => drive)!;
        Assert.That(error.Message, Does.Contain("console session ended"));
    }

    [Test]
    public async Task Drive_WithoutAnEventSource_ThrowsInsteadOfHangingForever()
    {
        var session = new DrivenConsoleSession(new ScriptedHost());
        await session.StartAsync(Session(), CancellationToken.None);

        Assert.ThrowsAsync<InvalidOperationException>(
            () => session.DriveAsync("prompt", CancellationToken.None));
        await session.DisposeAsync();
    }

    [Test]
    public async Task SendCommand_DeliversWithoutAwaitingATurn()
    {
        var host = new ScriptedHost();
        var events = new ManualEvents();
        await using var session = new DrivenConsoleSession(host, null, events);
        await session.StartAsync(Session(), CancellationToken.None);

        await session.SendCommandAsync("/compact", TimeSpan.Zero, CancellationToken.None);

        Assert.That(host.Journal, Does.Contain("send:/compact"));
    }
}
