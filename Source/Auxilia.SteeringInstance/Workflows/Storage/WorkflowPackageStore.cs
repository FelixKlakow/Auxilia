using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;

namespace Auxilia.SteeringInstance.Workflows.Storage;

/// <summary>
/// Durable registry of workflow packages known to the platform; one record per workflow type.
/// Seeded registrations own the display metadata — dispatch-learned entries never overwrite them.
/// </summary>
public sealed class WorkflowPackageStore(
    IDataAccess<WorkflowPackageRecord> dataAccess, TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<WorkflowPackageRecord>> GetAllAsync(CancellationToken ct = default)
        => (await dataAccess.ReadAsync(ct)).ToList();

    public Task<WorkflowPackageRecord?> GetAsync(string workflowType, CancellationToken ct = default)
        => dataAccess.ReadAsync(WorkflowPackageRecord.IdFor(workflowType), ct);

    /// <summary>Explicit deployment registration: always wins over learned entries.</summary>
    public Task RegisterAsync(
        string workflowType, string packageUri, string? displayName, string? version,
        CancellationToken ct = default)
        => dataAccess.SaveAsync(new WorkflowPackageRecord
        {
            Id = WorkflowPackageRecord.IdFor(workflowType),
            WorkflowType = workflowType,
            PackageUri = packageUri,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? workflowType : displayName.Trim(),
            Version = version?.Trim() ?? "",
            Source = "seed",
            RegisteredUtc = timeProvider.GetUtcNow()
        }, ct);

    /// <summary>
    /// Dispatch-learned registration: creates a missing entry so every workflow that ever ran is
    /// offered in the editor, but never overwrites a seeded record's coordinates or metadata.
    /// </summary>
    public async Task LearnAsync(string workflowType, string packageUri, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workflowType) || string.IsNullOrWhiteSpace(packageUri))
            return;

        var existing = await dataAccess.ReadAsync(WorkflowPackageRecord.IdFor(workflowType), ct);
        if (existing is not null && (existing.Source == "seed" || existing.PackageUri == packageUri))
            return;

        await dataAccess.SaveAsync(new WorkflowPackageRecord
        {
            Id = WorkflowPackageRecord.IdFor(workflowType),
            WorkflowType = workflowType,
            PackageUri = packageUri,
            DisplayName = existing?.DisplayName is { Length: > 0 } name ? name : workflowType,
            Version = existing?.Version ?? "",
            Source = "run",
            RegisteredUtc = timeProvider.GetUtcNow()
        }, ct);
    }

    public Task<bool> RemoveAsync(string workflowType, CancellationToken ct = default)
        => dataAccess.RemoveAsync(WorkflowPackageRecord.IdFor(workflowType), ct);
}
