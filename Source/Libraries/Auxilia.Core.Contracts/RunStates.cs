namespace Auxilia.Core.Contracts;

/// <summary>
/// The shared run-lifecycle state vocabulary. States are open strings (runtime-extensible);
/// these sets cover the platform-authored states every client can rely on: the Core stamps
/// <see cref="Dispatched"/> at dispatch, the runner authors the rest.
/// </summary>
public static class RunStates
{
    /// <summary>Core-authored initial state: accepted and published, not yet claimed by a runner.</summary>
    public const string Dispatched = "Dispatched";

    public const string Received = "Received";
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Draining = "Draining";
    public const string Success = "Success";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
    public const string PreFlightFailed = "PreFlightFailed";

    private static readonly string[] Terminal = [Success, Failed, Cancelled, PreFlightFailed];
    private static readonly string[] Active = [Dispatched, Received, Queued, Running, Draining];

    public static bool IsTerminal(string? state)
        => state is not null && Terminal.Contains(state, StringComparer.Ordinal);

    public static bool IsActive(string? state)
        => state is not null && Active.Contains(state, StringComparer.Ordinal);
}
