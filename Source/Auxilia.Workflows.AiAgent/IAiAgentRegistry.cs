using Auxilia.AI;

namespace Auxilia.Workflows.AiAgent;

public interface IAiAgentRegistry
{
    void RegisterPrototype(string slotName, IAgentSessionBuilder prototype);
    Task<IAgentSession> CreateSessionAsync(string slotName);
}
