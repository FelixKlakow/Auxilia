namespace Auxilia.SteeringInstance.Workflows;

/// <summary>
/// All information the launcher needs to start one workflow container.
/// </summary>
public sealed record WorkflowLaunchRequest(
    string Image,
    IReadOnlyDictionary<string, string> EnvironmentVariables);

