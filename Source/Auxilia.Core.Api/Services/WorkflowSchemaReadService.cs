using System.Text.Json;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Contracts;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Reads the Core's catalog of registered workflow types (mirrored from the runner over the bus by
/// <see cref="WorkflowSchemaTrackingService"/>) and maps stored schemas to the config-editor DTOs.
/// Read-only over the Core's own store — no cross-DB access.
/// </summary>
public sealed class WorkflowSchemaReadService(IDataAccess<CoreWorkflowSchemaRecord> schemas)
{
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    /// <summary>All registered workflow types, ordered by name, filtered and paged.</summary>
    public async Task<PagedResult<WorkflowTypeDto>> QueryTypesAsync(WorkflowTypeQuery query, CancellationToken ct)
    {
        var all = (await schemas.ReadAsync(ct))
            .Select(r => ToTypeDto(Deserialize(r.SchemaJson), r.WorkflowType))
            .OrderBy(t => t.WorkflowType, StringComparer.Ordinal)
            .ToList();
        var take = query.Take <= 0 ? 50 : query.Take;
        var page = all.Skip(query.Skip).Take(take).ToList();
        return new PagedResult<WorkflowTypeDto>(page, all.Count, query.Skip, take);
    }

    /// <summary>The full schema of one workflow type, or null when it is not registered.</summary>
    public async Task<WorkflowSchemaDto?> GetSchemaAsync(string workflowType, CancellationToken ct)
    {
        var record = await schemas.ReadAsync(CoreWorkflowSchemaRecord.IdFor(workflowType), ct);
        return record is null ? null : ToSchemaDto(Deserialize(record.SchemaJson), record.WorkflowType);
    }

    private static WorkflowSchema Deserialize(string json)
        => JsonSerializer.Deserialize<WorkflowSchema>(json)
           ?? throw new InvalidOperationException("Stored workflow schema is not deserializable.");

    private static WorkflowTypeDto ToTypeDto(WorkflowSchema schema, string workflowType)
        => new(workflowType, schema.Version, schema.Lifetime.ToString(), null, schema.Tags);

    internal static WorkflowSchemaDto ToSchemaDto(WorkflowSchema schema, string workflowType)
        => new(
            workflowType,
            schema.Version,
            schema.SchemaVersion,
            schema.Lifetime.ToString(),
            schema.Tags,
            schema.Slots.Select(s => new WorkflowSlotDto(
                s.SlotName,
                s.Contract,
                s.Description,
                s.Optional,
                s.Capabilities is null ? null : JsonSerializer.Serialize(s.Capabilities, JsonOptions))).ToList(),
            schema.Inputs.Select(i => new WorkflowInputDto(i.Name, i.Label, i.Required, i.Description)).ToList(),
            schema.Views.Select(v => new WorkflowViewDto(
                v.Name, v.Rendering.ToString(), v.Lifecycle.ToString(), v.RendererKey, v.ItemSchemaJson)).ToList(),
            schema.Triggers.Select(t => new WorkflowTriggerDto(t.Kind, t.Description)).ToList(),
            schema.ConsumedArtifacts,
            schema.InteractiveTerminalPort,
            JsonSerializer.Serialize(schema.EnvironmentRequirements, JsonOptions));
}
