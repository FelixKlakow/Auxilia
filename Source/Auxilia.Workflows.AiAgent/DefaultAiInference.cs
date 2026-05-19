using Auxilia.AI;

namespace Auxilia.Workflows.AiAgent;

public sealed class DefaultAiInference(IAiAgentRegistry registry) : IAiInference
{
    public Task<IAgentSession> CreateSessionAsync(string slotName)
        => registry.CreateSessionAsync(slotName);
}
