namespace Auxilia.Workflows.Messaging.Messages;

/// <summary>
/// One runtime pod-control operation, authenticated by the instance token and answered on
/// the instance's exclusive pod response queue (mirrors <see cref="ResourceRequest"/>).
/// </summary>
public sealed record PodControlRequest(
    Guid WorkflowInstanceId,
    string Action,
    string SpecJson,
    Guid RequestId,
    string? InstanceToken = null)
{
    public const string Spawn = "spawn";
    public const string Stop = "stop";
}

public sealed record PodControlResponse(
    Guid RequestId,
    bool Success,
    string? ErrorMessage,
    string? ResultJson);
