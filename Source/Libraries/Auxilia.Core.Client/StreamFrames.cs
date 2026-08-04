namespace Auxilia.Core.Client;

/// <summary>
/// One frame of a resilient client stream: a payload event (<see cref="StreamEventFrame{TEvent}"/>)
/// or a connection-state transition (<see cref="StreamConnectionFrame{TEvent}"/>). The client
/// reconnects internally — raw transport errors never escape the enumeration; consumers switch on
/// the frame type to drive "reconnecting" UX and refresh-on-reconnect.
/// </summary>
public abstract record ClientStreamFrame<TEvent>;

/// <summary>A payload event received from the Core.</summary>
public sealed record StreamEventFrame<TEvent>(TEvent Event) : ClientStreamFrame<TEvent>;

/// <summary>
/// A connection-state transition. <see cref="StreamConnectionState.Connected"/> fires on every
/// (re)subscribe — <see cref="Attempt"/> 1 is the first connect, anything above is a reconnect
/// (refresh caught-up state then). <see cref="StreamConnectionState.Reconnecting"/> carries the
/// wrapped cause and the delay before the next attempt.
/// </summary>
public sealed record StreamConnectionFrame<TEvent>(
    StreamConnectionState State,
    int Attempt,
    TimeSpan? RetryDelay = null,
    Exception? Cause = null) : ClientStreamFrame<TEvent>;

public enum StreamConnectionState
{
    Connected,
    Reconnecting
}
