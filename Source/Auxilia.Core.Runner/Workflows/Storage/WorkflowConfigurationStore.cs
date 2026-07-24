using System.Text.Json;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Runner.Workflows.Storage;

/// <summary>
/// Durable named-workflow-configuration repository (#18); one record per configuration name.
/// Binding settings are run through the <see cref="ISettingsProtector"/> per binding before
/// persisting, so secrets are encrypted at rest whenever a protection key is configured.
/// Bindings may alternatively reference a reusable <see cref="SlotInstanceRecord"/>; those are
/// dereferenced at read time so instance updates apply to every configuration using them.
/// Every mutation is audited — without settings values.
/// </summary>
public sealed class WorkflowConfigurationStore(
    IDataAccess<WorkflowConfigurationRecord> dataAccess,
    SlotInstanceStore slotInstances,
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

        var bindings = new List<WorkflowConfigurationSlotBinding>();
        foreach (var binding in configuration.SlotBindings)
        {
            if (binding.SlotInstanceId is { } instanceId)
            {
                // Instance-backed binding: no settings of its own; the provider type is a
                // denormalized copy taken from the instance so lists render without a join.
                var instance = await slotInstances.GetAsync(instanceId, ct)
                               ?? throw new ArgumentException(
                                   $"Slot binding '{binding.SlotName}' references unknown slot instance '{instanceId}'.",
                                   nameof(configuration));
                bindings.Add(new WorkflowConfigurationSlotBinding
                {
                    SlotName = binding.SlotName,
                    ProviderType = instance.ProviderType,
                    ProtectedSettingsJson = protector.Protect("{}"),
                    SlotInstanceId = instanceId
                });
            }
            else
            {
                bindings.Add(new WorkflowConfigurationSlotBinding
                {
                    SlotName = binding.SlotName,
                    ProviderType = binding.ProviderType,
                    ProtectedSettingsJson = protector.Protect(JsonSerializer.Serialize(binding.Settings))
                });
            }
        }

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
            SlotBindingsJson = JsonSerializer.Serialize(bindings)
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
                slotBindings = bindings.Select(b => new { b.SlotName, b.ProviderType, b.SlotInstanceId })
            }), ct);

        return (await GetAsync(id, ct))!;
    }

    public async Task<StoredWorkflowConfiguration?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var record = await dataAccess.ReadAsync(id, ct);
        return record is null ? null : await FromRecordAsync(record, ct);
    }

    public Task<StoredWorkflowConfiguration?> GetByNameAsync(string name, CancellationToken ct = default)
        => GetAsync(WorkflowConfigurationRecord.IdFor(name), ct);

    public async Task<IReadOnlyList<StoredWorkflowConfiguration>> GetAllAsync(CancellationToken ct = default)
    {
        var query = await dataAccess.ReadAsync(ct);
        var configurations = new List<StoredWorkflowConfiguration>();
        foreach (var record in query.ToList())
            configurations.Add(await FromRecordAsync(record, ct));
        return configurations.AsReadOnly();
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
            if (binding.SlotInstanceId is null && string.IsNullOrWhiteSpace(binding.ProviderType))
                throw new ArgumentException(
                    $"Slot binding '{binding.SlotName}' must declare a provider type or reference a slot instance.",
                    nameof(configuration));
            if (!slotNames.Add(binding.SlotName))
                throw new ArgumentException(
                    $"Duplicate slot binding '{binding.SlotName}'.", nameof(configuration));
        }
    }

    private async Task<StoredWorkflowConfiguration> FromRecordAsync(
        WorkflowConfigurationRecord record, CancellationToken ct)
    {
        var bindings = new List<StoredSlotBinding>();
        foreach (var binding in JsonSerializer
                     .Deserialize<List<WorkflowConfigurationSlotBinding>>(record.SlotBindingsJson)!)
        {
            if (binding.SlotInstanceId is { } instanceId)
            {
                var instance = await slotInstances.GetAsync(instanceId, ct);
                bindings.Add(instance is null
                    ? new StoredSlotBinding(
                        binding.SlotName, binding.ProviderType,
                        new Dictionary<string, string>(), instanceId)
                    {
                        SlotInstanceResolved = false
                    }
                    : new StoredSlotBinding(
                        binding.SlotName, instance.ProviderType, instance.Settings, instanceId));
            }
            else
            {
                bindings.Add(new StoredSlotBinding(
                    binding.SlotName,
                    binding.ProviderType,
                    JsonSerializer.Deserialize<Dictionary<string, string>>(
                        protector.Unprotect(binding.ProtectedSettingsJson))!));
            }
        }

        return new StoredWorkflowConfiguration(
            record.Name, record.DisplayName, record.WorkflowType, record.PackageUri,
            record.Enabled, bindings, record.OwnerPrincipalId)
        {
            CreatedUtc = record.CreatedUtc,
            UpdatedUtc = record.UpdatedUtc
        };
    }
}
