using System.Text.Json;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess;

namespace Auxilia.SteeringInstance.Workflows.Storage;

/// <summary>
/// Durable named-workflow-configuration repository (#18); one record per configuration name.
/// Binding settings are run through the <see cref="ISettingsProtector"/> per binding before
/// persisting, so secrets are encrypted at rest whenever a protection key is configured.
/// Every mutation is audited — without settings values.
/// </summary>
public sealed class WorkflowConfigurationStore(
    IDataAccess<WorkflowConfigurationRecord> dataAccess,
    ISettingsProtector protector,
    AuditLog auditLog,
    TimeProvider timeProvider)
{
    public async Task<StoredWorkflowConfiguration> UpsertAsync(
        StoredWorkflowConfiguration configuration, CancellationToken ct = default)
    {
        Validate(configuration);

        var id = WorkflowConfigurationRecord.IdFor(configuration.Name);
        var existing = await dataAccess.ReadAsync(id, ct);
        var now = timeProvider.GetUtcNow();

        var record = new WorkflowConfigurationRecord
        {
            Id = id,
            Name = configuration.Name,
            DisplayName = string.IsNullOrWhiteSpace(configuration.DisplayName)
                ? configuration.Name
                : configuration.DisplayName,
            WorkflowType = configuration.WorkflowType,
            PackageUri = configuration.PackageUri,
            Enabled = configuration.Enabled,
            OwnerPrincipalId = configuration.OwnerPrincipalId ?? existing?.OwnerPrincipalId,
            CreatedUtc = existing?.CreatedUtc ?? now,
            UpdatedUtc = now,
            SlotBindingsJson = JsonSerializer.Serialize(configuration.SlotBindings
                .Select(b => new WorkflowConfigurationSlotBinding
                {
                    SlotName = b.SlotName,
                    ProviderType = b.ProviderType,
                    ProtectedSettingsJson = protector.Protect(JsonSerializer.Serialize(b.Settings))
                })
                .ToList())
        };
        await dataAccess.SaveAsync(record, ct);

        // Audited without settings values: only the binding topology is recorded.
        await auditLog.AppendAsync(
            "steering-instance", "workflow-configuration.upserted",
            configuration.Name, existing is null ? "created" : "updated",
            JsonSerializer.Serialize(new
            {
                workflowType = record.WorkflowType,
                packageUri = record.PackageUri,
                enabled = record.Enabled,
                slotBindings = configuration.SlotBindings
                    .Select(b => new { b.SlotName, b.ProviderType })
            }), ct);

        return FromRecord(record);
    }

    public async Task<StoredWorkflowConfiguration?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var record = await dataAccess.ReadAsync(id, ct);
        return record is null ? null : FromRecord(record);
    }

    public Task<StoredWorkflowConfiguration?> GetByNameAsync(string name, CancellationToken ct = default)
        => GetAsync(WorkflowConfigurationRecord.IdFor(name), ct);

    public async Task<IReadOnlyList<StoredWorkflowConfiguration>> GetAllAsync(CancellationToken ct = default)
    {
        var query = await dataAccess.ReadAsync(ct);
        return query.ToList().Select(FromRecord).ToList().AsReadOnly();
    }

    public async Task<bool> RemoveAsync(string name, CancellationToken ct = default)
    {
        var removed = await dataAccess.RemoveAsync(WorkflowConfigurationRecord.IdFor(name), ct);
        if (removed)
            await auditLog.AppendAsync(
                "steering-instance", "workflow-configuration.removed", name, "removed", ct: ct);
        return removed;
    }

    private static void Validate(StoredWorkflowConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.Name))
            throw new ArgumentException("Configuration name must not be empty.", nameof(configuration));
        if (string.IsNullOrWhiteSpace(configuration.WorkflowType))
            throw new ArgumentException("Workflow type must not be empty.", nameof(configuration));
        if (string.IsNullOrWhiteSpace(configuration.PackageUri))
            throw new ArgumentException("Package URI must not be empty.", nameof(configuration));

        var slotNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in configuration.SlotBindings)
        {
            if (string.IsNullOrWhiteSpace(binding.SlotName))
                throw new ArgumentException("Slot binding name must not be empty.", nameof(configuration));
            if (string.IsNullOrWhiteSpace(binding.ProviderType))
                throw new ArgumentException(
                    $"Slot binding '{binding.SlotName}' must declare a provider type.", nameof(configuration));
            if (!slotNames.Add(binding.SlotName))
                throw new ArgumentException(
                    $"Duplicate slot binding '{binding.SlotName}'.", nameof(configuration));
        }
    }

    private StoredWorkflowConfiguration FromRecord(WorkflowConfigurationRecord record)
    {
        var bindings = JsonSerializer
            .Deserialize<List<WorkflowConfigurationSlotBinding>>(record.SlotBindingsJson)!
            .Select(b => new StoredSlotBinding(
                b.SlotName,
                b.ProviderType,
                JsonSerializer.Deserialize<Dictionary<string, string>>(
                    protector.Unprotect(b.ProtectedSettingsJson))!))
            .ToList();

        return new StoredWorkflowConfiguration(
            record.Name, record.DisplayName, record.WorkflowType, record.PackageUri,
            record.Enabled, bindings, record.OwnerPrincipalId)
        {
            CreatedUtc = record.CreatedUtc,
            UpdatedUtc = record.UpdatedUtc
        };
    }
}
