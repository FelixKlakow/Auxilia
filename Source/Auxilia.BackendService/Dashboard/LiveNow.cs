using Auxilia.PlatformData.Entities;

namespace Auxilia.BackendService.Dashboard;

/// <summary>
/// Shared definition of "currently active" runs for the live-now surfaces (#21):
/// the dashboard's Live now section and the sidebar count badge.
/// </summary>
public static class LiveNow
{
    private static readonly string[] ActiveStates = ["Queued", "Running", "Draining"];

    public static bool IsActive(string state)
        => ActiveStates.Contains(state, StringComparer.Ordinal);

    /// <summary>Active runs only, newest first.</summary>
    public static IReadOnlyList<WorkflowInstanceRecord> ActiveOf(IEnumerable<WorkflowInstanceRecord> runs)
        => runs.Where(r => IsActive(r.State))
            .OrderByDescending(r => r.CreatedUtc)
            .ToList();
}
