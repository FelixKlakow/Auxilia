using Auxilia.Workflows.AiAgent.CodingAgent;

namespace Auxilia.Implementation.Workflow;

/// <summary>
/// The run's driven consoles by stage role. The AUTHOR console is primary: it serves the run
/// terminal, does the refinement, and is the default for every stage. Secondary consoles
/// (a fresh author instance, or one on the reviewer binding) run in their own tmux sessions
/// beside it, started lazily on their first drive. All consoles share one provider event
/// source (the pipeline drives one console at a time).
/// </summary>
public sealed class AgentConsolePool(
    ImplementationContext context,
    CodingAgentCredentials? authorCredentials,
    CodingAgentCredentials? reviewerCredentials,
    Func<ISessionHost> hostFactory,
    Func<IConsoleSessionEventSource?> eventSourceFactory,
    IConsoleSessionPreparer? preparer,
    Func<ConsoleSessionEvent, CancellationToken, Task>? onEvent = null) : IAsyncDisposable
{
    public sealed class AgentConsole
    {
        public required DrivenConsoleSession Session { get; init; }
        public required TerminalSessionInfo Info { get; init; }
        internal string? PendingBase { get; set; }
        internal bool Started { get; set; }
    }

    private readonly Dictionary<string, AgentConsole> _consoles = [];

    public CodingAgentCredentials? AuthorCredentials => authorCredentials;

    /// <summary>The primary console; started explicitly so the terminal is up before drives.</summary>
    public AgentConsole Author => GetOrCreate(
        "author", authorCredentials, context.AuthorBaseInstructions, primary: true);

    /// <summary>A named secondary console — a FRESH instance of the author binding.</summary>
    public AgentConsole FreshAuthor(string name)
        => GetOrCreate(name, authorCredentials, context.AuthorBaseInstructions, primary: false);

    /// <summary>The reviewer binding as a driven console; throws when the slot is unbound.</summary>
    public AgentConsole ReviewerConsole()
        => GetOrCreate("reviewer", reviewerCredentials
                ?? throw new InvalidOperationException(
                    "A stage selects the reviewer agent, but the review-agent slot is unbound."),
            context.ReviewerBaseInstructions, primary: false);

    public async Task StartAuthorAsync(CancellationToken cancellationToken)
    {
        var author = Author;
        await author.Session.StartAsync(author.Info, cancellationToken);
        author.Started = true;
    }

    /// <summary>
    /// One drive: lazy console start, base instructions on the first prompt when the provider
    /// has no system-prompt seam, and the prompt flattened to ONE line — a driven console
    /// receives exactly one input per drive (multi-line text would blur turn boundaries).
    /// </summary>
    public async Task<string> DriveAsync(AgentConsole console, string prompt, CancellationToken ct)
    {
        if (!console.Started)
        {
            await console.Session.StartAsync(console.Info, ct);
            console.Started = true;
        }
        if (console.PendingBase is { } baseInstructions)
        {
            console.PendingBase = null;
            prompt = baseInstructions + " " + prompt;
        }
        return await console.Session.DriveAsync(Flatten(prompt), ct);
    }

    public Task SendCommandAsync(AgentConsole console, string command, TimeSpan settle, CancellationToken ct)
        => console.Started
            ? console.Session.SendCommandAsync(command, settle, ct)
            : Task.CompletedTask;

    private AgentConsole GetOrCreate(
        string name, CodingAgentCredentials? credentials, string? baseInstructions, bool primary)
    {
        if (_consoles.TryGetValue(name, out var existing))
            return existing;

        var cli = credentials?.CliPath is { Length: > 0 } path ? path : "claude";
        var unattended = credentials?.UnattendedCliArguments is { Length: > 0 } arguments
            ? " " + arguments
            : "";
        var environment = new Dictionary<string, string>(
            credentials?.ToEnvironment() ?? new Dictionary<string, string>());
        var command = cli + unattended;
        string? pendingBase = null;
        if (baseInstructions is { Length: > 0 })
        {
            if (credentials?.SystemPromptCliArgument is { Length: > 0 } systemPromptArgument)
            {
                environment[AgentConsoleApplication.BasePromptVariable] = baseInstructions;
                command += $" {systemPromptArgument} \"${AgentConsoleApplication.BasePromptVariable}\"";
            }
            else
            {
                pendingBase = baseInstructions;
            }
        }

        var console = new AgentConsole
        {
            Session = new DrivenConsoleSession(
                hostFactory(), primary ? preparer : null, eventSourceFactory(), onEvent),
            Info = new TerminalSessionInfo(
                context.WorkspaceDirectory, command, AgentConsoleApplication.TerminalPort)
            {
                Environment = environment,
                SessionName = primary ? "agent-session" : $"{name}-console",
                ServeTerminal = primary,
                EndsServerOnExit = primary,
            },
            PendingBase = pendingBase,
        };
        _consoles[name] = console;
        return console;
    }

    public async ValueTask DisposeAsync()
    {
        // Secondary consoles first — the author's disposal takes the tmux server down.
        foreach (var console in _consoles.Where(c => c.Key != "author").Select(c => c.Value))
            if (console.Started)
                await console.Session.DisposeAsync();
        if (_consoles.TryGetValue("author", out var author) && author.Started)
            await author.Session.DisposeAsync();
    }

    private static string Flatten(string prompt)
        => string.Join(" ", prompt
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
}
