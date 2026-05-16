using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Auxilia.Workflows;

namespace Auxilia.SteeringInstance.Workflows.Storage;

public sealed class WorkflowSchemaStore
{
    private readonly ConcurrentDictionary<string, WorkflowSchema> _store = new();

    public bool TryGetSchema(string workflowTypeName, [NotNullWhen(true)] out WorkflowSchema? schema)
        => _store.TryGetValue(workflowTypeName, out schema);

    public void SetSchema(string workflowTypeName, WorkflowSchema schema)
        => _store[workflowTypeName] = schema;
}
