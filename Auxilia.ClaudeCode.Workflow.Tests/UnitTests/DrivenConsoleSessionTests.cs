using Auxilia.Workflows.AiAgent.CodingAgent;

namespace Auxilia.ClaudeCode.Workflow.Tests.UnitTests;

[TestFixture, Category("Unit")]
public sealed class DrivenConsoleSessionTests
{
    private sealed class ScriptedHost : ISessionHost
    {
        public List<string> Journal { get; } = [];

        public Task StartAsync(TerminalSessionInfo session, CancellationToken ct)
        {
            Journal.Add("host-start");
            return Task.CompletedTask;
        }

        public Task WaitForSessionEndAsync(CancellationToken ct) => Task.CompletedTask;

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
