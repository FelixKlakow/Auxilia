using System.Text.Json;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows;

namespace Auxilia.Core.Runner.Workflows.Storage;

/// <summary>Durable workflow schema repository; one record per workflow type.</summary>
public sealed class WorkflowSchemaStore(IDataAccess<WorkflowSchemaRecord> dataAccess)
{
    public async Task<WorkflowSchema?> GetSchemaAsync(string workflowTypeName, CancellationToken ct = default)
    {
        var record = await dataAccess.ReadAsync(WorkflowSchemaRecord.IdFor(workflowTypeName), ct);
        return record is null ? null : JsonSerializer.Deserialize<WorkflowSchema>(record.SchemaJson);
    }

    public Task SetSchemaAsync(string workflowTypeName, WorkflowSchema schema, CancellationToken ct = default)
        => dataAccess.SaveAsync(new WorkflowSchemaRecord
        {
            Id = WorkflowSchemaRecord.IdFor(workflowTypeName),
            WorkflowType = workflowTypeName,
            SchemaJson = JsonSerializer.Serialize(schema)
        }, ct);
}
