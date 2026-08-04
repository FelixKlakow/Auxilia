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

    private static readonly string[] Terminal = ["Success", "Failed", "Cancelled", "PreFlightFailed"];
    private static readonly string[] Active = [Dispatched, "Received", "Queued", "Running", "Draining"];

    public static bool IsTerminal(string? state)
        => state is not null && Terminal.Contains(state, StringComparer.Ordinal);

    public static bool IsActive(string? state)
        => state is not null && Active.Contains(state, StringComparer.Ordinal);
}
