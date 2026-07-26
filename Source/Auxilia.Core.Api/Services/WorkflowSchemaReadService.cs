using System.Text.Json;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Contracts;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Reads the Core's workflow-type registry for clients: the type catalog (with package coordinate
/// and trust status) and each type's schema. Schemas originate from the registered package and are
/// refreshed from runner announcements (<see cref="WorkflowSchemaTrackingService"/>) — for
/// registered types only. Read-only over the Core's own store — no cross-DB access.
/// </summary>
public sealed class WorkflowSchemaReadService(IDataAccess<CoreWorkflowTypeRecord> types)
{
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    /// <summary>All registered workflow types, ordered by name, filtered by status and paged.</summary>
    public async Task<PagedResult<WorkflowTypeDto>> QueryTypesAsync(WorkflowTypeQuery query, CancellationToken ct)
    {
        var all = (await types.ReadAsync(ct)).AsEnumerable();
        if (!string.IsNullOrWhiteSpace(query.Status))
            all = all.Where(r => string.Equals(r.Status, query.Status, StringComparison.OrdinalIgnoreCase));
        var ordered = all
            .Select(ToTypeDto)
            .OrderBy(t => t.WorkflowType, StringComparer.Ordinal)
            .ToList();
        var take = query.Take <= 0 ? 50 : query.Take;
        var page = ordered.Skip(query.Skip).Take(take).ToList();
        return new PagedResult<WorkflowTypeDto>(page, ordered.Count, query.Skip, take);
    }

    /// <summary>The full schema of one registered workflow type, or null when unregistered or schema-less.</summary>
    public async Task<WorkflowSchemaDto?> GetSchemaAsync(string workflowType, CancellationToken ct)
    {
        var record = await types.ReadAsync(CoreWorkflowTypeRecord.IdFor(workflowType), ct);
        if (record?.SchemaJson is not { Length: > 0 } schemaJson)
            return null;
        return ToSchemaDto(Deserialize(schemaJson), record.WorkflowType, record.PackageUri, record.Status);
    }

    private static WorkflowSchema Deserialize(string json)
        => JsonSerializer.Deserialize<WorkflowSchema>(json)
           ?? throw new InvalidOperationException("Stored workflow schema is not deserializable.");

    private static WorkflowTypeDto ToTypeDto(CoreWorkflowTypeRecord record)
    {
        var schema = record.SchemaJson is { Length: > 0 } json ? Deserialize(json) : null;
        return new WorkflowTypeDto(
            record.WorkflowType,
            schema?.Version ?? "",
            schema?.Lifetime.ToString() ?? "",
            record.StatusReason,
            schema?.Tags ?? [],
            record.PackageUri,
            record.Status);
    }

    internal static WorkflowSchemaDto ToSchemaDto(
        WorkflowSchema schema, string workflowType, string? packageUri, string status)
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
            JsonSerializer.Serialize(schema.EnvironmentRequirements, JsonOptions),
            packageUri,
            status);
}
