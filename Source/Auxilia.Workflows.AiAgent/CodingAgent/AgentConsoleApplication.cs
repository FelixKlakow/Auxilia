using System.Text.Json.Serialization;
using Auxilia.Workflows.Views;

namespace Auxilia.Workflows.AiAgent.CodingAgent;

/// <summary>
/// Console-mode agent run: the CLI runs INTERACTIVELY in tmux+ttyd — the operator drives the
/// real console through the platform's authenticated web terminal — instead of headless
/// stream parsing. The run completes when the CLI exits; the session report stays the
/// durable record, and the conversation view only carries the hand-off note.
/// </summary>
public sealed class AgentConsoleApplication(
    ISessionHost host,
    IViewPublisher? views,
    AgentSessionContext context,
    CodingAgentCredentials? credentials,
    TimeProvider time,
    IConsoleSessionPreparer? sessionPreparer = null,
    IConsoleSessionEventSource? sessionEvents = null)
{
    private readonly ConsoleEventViews _eventViews = new(views!, time);

    /// <summary>The instruction reaches the CLI via the session environment, never the command line.</summary>
    public const string InstructionVariable = "AUXILIA_INSTRUCTION";

    /// <summary>A forgotten console must not hold the container (and the credential) open forever.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(4);

    public const int TerminalPort = 7681;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var started = time.GetUtcNow();
        await PublishProgressAsync("session", "console session starting", cancellationToken);
        if (context.Instruction.Length > 0)
            await PublishChatAsync(
                new AgentChatEntry(AgentChatRole.User, context.Instruction, time.GetUtcNow()),
                cancellationToken);
        await PublishChatAsync(
            new AgentChatEntry(
                AgentChatRole.System,
                "Console session — the conversation happens in the live terminal, not in this view.",
                time.GetUtcNow()),
            cancellationToken);

        // The event source starts FIRST so preparation can wire the CLI to its endpoint
        // (e.g. hook commands pointing at the listener's port).
        if (sessionEvents is not null)
            await sessionEvents.StartAsync(PublishSessionEventAsync, cancellationToken);

        // Provider-specific session preparation (e.g. materializing the credential where the
        // CLI's INTERACTIVE mode reads it — env vars only cover headless use).
        if (sessionPreparer is not null)
            await sessionPreparer.PrepareAsync(context.WorkspaceDirectory, cancellationToken);

        await host.StartAsync(BuildSession(context, credentials), cancellationToken);
        await PublishProgressAsync("session",
            $"console up — terminal on container port {TerminalPort}; the run completes when the CLI exits",
            cancellationToken);

        var timedOut = false;
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        guard.CancelAfter(MaxDuration);
        try
        {
            await host.WaitForSessionEndAsync(guard.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            await PublishProgressAsync("session",
                $"max duration of {MaxDuration.TotalMinutes:0} min reached — ending the session",
                cancellationToken);
        }

        await host.ShutdownAsync();
        if (sessionEvents is not null)
            await sessionEvents.StopAsync();

        var ended = time.GetUtcNow();
        var report = new SessionReport(
            context.Instruction,
            Success: !timedOut,
            Summary: timedOut ? null : "The interactive console session ended.",
            TurnCount: 0,
            TotalCostUsd: null,
            DurationMs: (long)(ended - started).TotalMilliseconds,
            ErrorMessage: timedOut
                ? $"The console session hit its maximum duration of {MaxDuration.TotalMinutes:0} minutes."
                : null,
            CompletedUtc: ended);
        await new SessionReportWriter(context.OutputDirectory).WriteAsync(report, cancellationToken);
        await PublishProgressAsync("session", timedOut ? "timed out" : "completed", cancellationToken);
    }

    /// <summary>
    /// The command stays FREE of run data: a non-empty instruction rides the session
    /// environment and is expanded by the shell inside tmux as the CLI's first prompt.
    /// </summary>
    public static TerminalSessionInfo BuildSession(
        AgentSessionContext context, CodingAgentCredentials? credentials)
    {
        var cli = credentials?.CliPath is { Length: > 0 } path ? path : "claude";
        var environment = new Dictionary<string, string>(
            credentials?.ToEnvironment() ?? new Dictionary<string, string>());
        var command = cli;
        if (context.Instruction.Length > 0)
        {
            environment[InstructionVariable] = context.Instruction;
            command = $"{cli} \"${InstructionVariable}\"";
        }

        return new TerminalSessionInfo(context.WorkspaceDirectory, command, TerminalPort)
        {
            Environment = environment
        };
    }

    /// <summary>CLI-side events land on the run's surfaces (see <see cref="ConsoleEventViews"/>).</summary>
    public Task PublishSessionEventAsync(ConsoleSessionEvent evt, CancellationToken ct)
        => views is null ? Task.CompletedTask : _eventViews.PublishAsync(evt, ct);

    private Task PublishChatAsync(AgentChatEntry entry, CancellationToken ct)
        => views?.PublishAsync(AgentSessionApplication.ChatViewName, entry, ct) ?? Task.CompletedTask;

    private Task PublishProgressAsync(string phase, string message, CancellationToken ct)
        => views?.PublishAsync(
               AgentSessionApplication.ProgressViewName,
               new SessionProgressEntry(phase, message, time.GetUtcNow()), ct)
           ?? Task.CompletedTask;
}
