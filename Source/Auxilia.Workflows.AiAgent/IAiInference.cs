using Auxilia.AI;

namespace Auxilia.Workflows.AiAgent;

/// <summary>Workflow-facing contract for obtaining a scoped AI inference session by slot name.</summary>
public interface IAiInference
{
    Task<IAgentSession> CreateSessionAsync(string slotName);
}
