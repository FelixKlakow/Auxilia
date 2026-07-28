namespace Auxilia.Workflows.AiAgent.CodingAgent;

/// <summary>
/// Provider-specific preparation of an INTERACTIVE console session, run inside the container
/// right before the terminal host starts — e.g. materializing the slot's credential where the
/// CLI's interactive mode actually reads it. Registered by the slot handler; console mode runs
/// without one when the provider needs no preparation.
/// </summary>
public interface IConsoleSessionPreparer
{
    Task PrepareAsync(string workspaceDirectory, CancellationToken cancellationToken);
}
