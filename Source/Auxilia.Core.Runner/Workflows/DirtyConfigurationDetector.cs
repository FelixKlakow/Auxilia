using System.Text.Json;
using Auxilia.Core.Runner.Workflows.Storage;
using Auxilia.Workflows;

namespace Auxilia.Core.Runner.Workflows;

/// <summary>
///     Compares a stored <see cref="WorkflowSchema"/> against an incoming one to detect new required
///     capability fields and marks affected slot configurations dirty.
///     Called when a new schema version becomes available — not during registration.
/// </summary>
public sealed class DirtyConfigurationDetector(
    WorkflowSchemaStore schemaStore,
    SlotConfigurationStore configStore)
{
    public async Task<SchemaDiffResult> DetectAsync(
        string workflowTypeName, WorkflowSchema updatedSchema, CancellationToken ct = default)
    {
        var storedSchema = await schemaStore.GetSchemaAsync(workflowTypeName, ct);
        if (storedSchema is null)
        {
            await schemaStore.SetSchemaAsync(workflowTypeName, updatedSchema, ct);
            return new SchemaDiffResult(0, 0);
        }

        var storedJson = JsonSerializer.SerializeToElement(storedSchema);
        var updatedJson = JsonSerializer.SerializeToElement(updatedSchema);

        int addedRequiredFieldsCount = ComputeAddedRequiredFields(storedJson, updatedJson);

        if (addedRequiredFieldsCount > 0)
            await configStore.MarkDirtyAsync(workflowTypeName, ct);

        await schemaStore.SetSchemaAsync(workflowTypeName, updatedSchema, ct);

        int dirtyConfigurationCount = (await configStore.GetConfigurationsAsync(workflowTypeName, ct))
            .Count(c => c.Status == ConfigurationStatus.Dirty);

        return new SchemaDiffResult(addedRequiredFieldsCount, dirtyConfigurationCount);
    }

    private static int ComputeAddedRequiredFields(JsonElement storedJson, JsonElement updatedJson)
    {
        if (!storedJson.TryGetProperty("Slots", out var storedSlots) ||
            storedSlots.ValueKind != JsonValueKind.Array)
            return 0;

        if (!updatedJson.TryGetProperty("Slots", out var updatedSlots) ||
            updatedSlots.ValueKind != JsonValueKind.Array)
            return 0;

        var storedByName = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var slot in storedSlots.EnumerateArray())
        {
            if (slot.TryGetProperty("SlotName", out var nameElem) &&
                nameElem.ValueKind == JsonValueKind.String)
            {
                storedByName[nameElem.GetString()!] = slot;
            }
        }

        int count = 0;
        foreach (var updatedSlot in updatedSlots.EnumerateArray())
        {
            if (!updatedSlot.TryGetProperty("SlotName", out var nameElem) ||
                nameElem.ValueKind != JsonValueKind.String)
                continue;

            var slotName = nameElem.GetString()!;
            if (!storedByName.TryGetValue(slotName, out var storedSlot))
                continue;

            if (!updatedSlot.TryGetProperty("Capabilities", out var newCaps) ||
                newCaps.ValueKind != JsonValueKind.Object)
                continue;

            if (!storedSlot.TryGetProperty("Capabilities", out var oldCaps) ||
                oldCaps.ValueKind != JsonValueKind.Object)
                continue;

            var oldProperties = new HashSet<string>(StringComparer.Ordinal);
            foreach (var prop in oldCaps.EnumerateObject())
                oldProperties.Add(prop.Name);

            foreach (var prop in newCaps.EnumerateObject())
            {
                if (!oldProperties.Contains(prop.Name) &&
                    prop.Value.ValueKind != JsonValueKind.Null)
                {
                    count++;
                }
            }
        }

        return count;
    }
}
