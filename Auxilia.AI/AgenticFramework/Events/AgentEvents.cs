namespace Auxilia.AI;

/// <summary>Base record for all events emitted by an <see cref="IAgentSession"/>.</summary>
public abstract record AgentEvent(Guid SessionId, DateTime TimestampUtc);

/// <summary>Fired when a new request is submitted to the agent.</summary>
public record AgentRequestStartedEvent(Guid SessionId, DateTime TimestampUtc, string Prompt)
    : AgentEvent(SessionId, TimestampUtc);

/// <summary>Fired for each incremental text chunk during streaming.</summary>
public record AgentResponseChunkEvent(Guid SessionId, DateTime TimestampUtc, string Chunk)
    : AgentEvent(SessionId, TimestampUtc);

/// <summary>Fired when the agent has produced its complete response for a request.</summary>
public record AgentResponseCompleteEvent(Guid SessionId, DateTime TimestampUtc, string FullText)
    : AgentEvent(SessionId, TimestampUtc);

/// <summary>Fired when the agent invokes a tool.</summary>
public record AgentToolCallEvent(Guid SessionId, DateTime TimestampUtc, string ToolName, string? ArgumentsJson)
    : AgentEvent(SessionId, TimestampUtc);

/// <summary>Fired when a tool invocation completes.</summary>
public record AgentToolResultEvent(Guid SessionId, DateTime TimestampUtc, string ToolName, string? ResultJson)
    : AgentEvent(SessionId, TimestampUtc);

/// <summary>Fired when an error occurs during an agent run.</summary>
public record AgentErrorEvent(Guid SessionId, DateTime TimestampUtc, Exception Exception)
    : AgentEvent(SessionId, TimestampUtc);

