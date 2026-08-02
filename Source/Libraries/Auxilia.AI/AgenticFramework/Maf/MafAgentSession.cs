using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using System.Reactive.Subjects;

namespace Auxilia.AI.AgenticFramework.Maf;

/// <summary>
/// Microsoft Agent Framework implementation of <see cref="IAgentSession"/>.
/// Wraps a <see cref="ChatClientAgent"/> + <see cref="AgentSession"/> and exposes
/// events as an <see cref="IObservable{T}"/> stream.
/// </summary>
internal sealed class MafAgentSession : IAgentSession
{
    internal readonly ChatClientAgent Agent;
    internal readonly AgentSession AgentSession;

    private readonly Subject<AgentEvent> _events = new();
    private readonly IChatClient _chatClient;
    private readonly IReadOnlyList<McpClient> _mcpClients;
    private bool _disposed;

    public Guid Id { get; } = Guid.NewGuid();
    public Guid? LinkedWorkflowId { get; }
    public string SdkName => "Microsoft.Agents.AI";
    public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;
    public IObservable<AgentEvent> Events => _events;

    internal MafAgentSession(
        ChatClientAgent agent,
        AgentSession agentSession,
        IChatClient chatClient,
        IReadOnlyList<McpClient> mcpClients,
        Guid? linkedWorkflowId)
    {
        Agent = agent;
        AgentSession = agentSession;
        _chatClient = chatClient;
        _mcpClients = mcpClients;
        LinkedWorkflowId = linkedWorkflowId;
    }

    public IAgentRequest PrepareRequest(string prompt)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new MafAgentRequest(prompt, this);
    }

    internal void Publish(AgentEvent evt) => _events.OnNext(evt);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _events.OnCompleted();
        _events.Dispose();

        // ChatClientAgent does not implement IDisposable; dispose the IChatClient we own.
        if (_chatClient is IDisposable disposableChatClient)
            disposableChatClient.Dispose();

        // McpClient implements IAsyncDisposable – block synchronously so callers using
        // the standard `using var session = ...` pattern work correctly.
        foreach (var client in _mcpClients)
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}