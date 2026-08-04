using Auxilia.Core.Contracts;

namespace Auxilia.AdminConsole.Support;

/// <summary>
/// Shared definition of "currently active" runs for the live-now surfaces: the dashboard's Live-now
/// section and the sidebar count badge. Adapted for the console to operate on the Core's
/// <see cref="RunStatus"/> (from <c>ICoreClient.QueryRunsAsync</c>) rather than a platform entity.
/// </summary>
public static class LiveNow
{
    public static bool IsActive(string state) => RunStates.IsActive(state);

    /// <summary>Active runs only, newest first.</summary>
    public static IReadOnlyList<RunStatus> ActiveOf(IEnumerable<RunStatus> runs)
        => runs.Where(r => IsActive(r.State))
            .OrderByDescending(r => r.CreatedUtc)
            .ToList();
}
