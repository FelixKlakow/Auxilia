namespace Auxilia.Workflows.Messaging.Messages;

/// <summary>
/// Structured resource call routed through the Steering Instance's audited Resource Proxy
/// (ARCHITECTURE §8): the platform holds the credentials and executes the call; the
/// workflow only names the resource, the operation, and a JSON payload.
/// </summary>
public sealed record ResourceRequest(
    Guid WorkflowInstanceId,
    string ResourceName,
    string Operation,
    string PayloadJson,
    Guid RequestId,
    string? InstanceToken = null);
