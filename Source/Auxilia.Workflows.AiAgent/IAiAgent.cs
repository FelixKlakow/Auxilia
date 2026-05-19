namespace Auxilia.Workflows.AiAgent;

/// <summary>
/// AI-agent interface injected into workflows.
/// Provider packages implement this interface. Workflow code depends on it.
/// The slot contract is intentionally independent of any specific AI SDK; a provider package bridges them.
/// </summary>
public interface IAiAgent
{
    /// <summary>Creates a new agent session, optionally setting a system prompt that frames the session.</summary>
    IAiAgentSession CreateSession(string? systemPrompt = null);
}
