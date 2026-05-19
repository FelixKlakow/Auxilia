using System.Collections.Concurrent;

namespace Auxilia.SteeringInstance.Workflows;

public sealed class WorkflowInstanceRegistry
{
    private readonly ConcurrentDictionary<Guid, string> _map = new();

    public void Register(Guid instanceId, string workflowTypeName)
        => _map[instanceId] = workflowTypeName;

    public bool TryGetWorkflowType(Guid instanceId, out string? workflowTypeName)
        => _map.TryGetValue(instanceId, out workflowTypeName);
}
