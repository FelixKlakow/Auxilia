using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData.Entities;

/// <summary>
/// One workflow package known to the platform — the registry behind "pick a workflow"
/// dropdowns. Registered explicitly at deployment time or learned from dispatches; the
/// package itself stays signed and verified on every launch regardless of this record.
/// </summary>
public sealed record WorkflowPackageRecord : IEntity
{
    public Guid Id { get; init; }
    public required string WorkflowType { get; init; }
    public required string PackageUri { get; init; }
    public string DisplayName { get; init; } = "";
    public string Version { get; init; } = "";
    /// <summary>"seed" = explicit deployment registration; "run" = learned from a dispatch.</summary>
    public string Source { get; init; } = "seed";
    public DateTimeOffset RegisteredUtc { get; init; }

    public static Guid IdFor(string workflowType) => DeterministicGuid.For("workflow-package", workflowType);
}
