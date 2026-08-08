using Auxilia.Core.Contracts;

namespace Auxilia.AdminConsole.Support;

/// <summary>
/// Maps a run state onto its badge class. States are a runtime-extensible vocabulary — the Core may
/// report one this console has never heard of — so anything unrecognised gets a defined
/// "unknown state" badge instead of silently falling back to neutral page chrome.
/// </summary>
public static class StateBadge
{
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "success", "failed", "cancelled", "preflightfailed",
        "running", "queued", "pending", "dispatched", "claimed"
    };

    public static string ClassFor(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
            return "badge-unknown";
        var slug = state.ToLowerInvariant();
        if (Known.Contains(slug))
            return $"badge-{slug}";
        // An unknown-but-active state still reads as in-flight; anything else is simply unknown.
        return RunStates.IsActive(state) ? "badge-running" : "badge-unknown";
    }
}
