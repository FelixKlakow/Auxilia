namespace Auxilia.Workflows;

/// <summary>
/// Manifest-declared lifetime (ARCHITECTURE.md §6). One-shot is the security default;
/// long-living deployment additionally requires explicit operator approval on the
/// Core.Runner.
/// </summary>
public enum WorkflowLifetime
{
    /// <summary>Runs exactly once for a single trigger and always shuts down afterwards.</summary>
    OneShot,

    /// <summary>
    /// Service-style workflow that stays running and processes many work items/events.
    /// Drained and replaced on configuration changes or upgrades; still not resumable.
    /// </summary>
    LongLiving
}
