namespace Auxilia.Workflows.AiAgent.CodingAgent;

/// <summary>
/// One event observed from an INTERACTIVE console session (e.g. via the CLI's hook system):
/// what happened, a human-readable line, and — for tool events — which tool. Kinds are an open
/// vocabulary; consumers ignore kinds they do not know.
/// </summary>
public sealed record ConsoleSessionEvent(string Kind, string Message)
{
    /// <summary>The session is waiting for the operator (a permission prompt, a question).</summary>
    public const string Attention = "attention";

    public const string ToolStarted = "tool-started";
    public const string ToolFinished = "tool-finished";

    /// <summary>The agent finished responding and is idle at the prompt.</summary>
    public const string TurnEnded = "turn-ended";

    public string? ToolName { get; init; }
}

/// <summary>
/// Provider-specific source of <see cref="ConsoleSessionEvent"/>s, registered by the slot
/// handler. Started BEFORE <see cref="IConsoleSessionPreparer"/> runs so preparation can wire
/// the CLI to the source's endpoint (e.g. hook commands pointing at a local listener).
/// </summary>
public interface IConsoleSessionEventSource
{
    Task StartAsync(Func<ConsoleSessionEvent, CancellationToken, Task> onEvent, CancellationToken cancellationToken);

    ValueTask StopAsync();
}
