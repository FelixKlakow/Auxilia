namespace Auxilia.AI;

public interface IAgentSession : IDisposable
{
    Guid Id { get; }
    Guid? LinkedWorkflowId { get; }
    string SdkName { get; }
    DateTime CreatedAtUtc { get; }
    IObservable<AgentEvent> Events { get; }
    IAgentRequest PrepareRequest(string prompt);
}