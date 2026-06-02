namespace Auxilia.SteeringInstance.Workflows;

/// <summary>
/// All information the launcher needs to start one workflow container.
/// </summary>
public sealed record WorkflowLaunchRequest(
    /// <summary>Path to the directory containing the extracted workflow package contents.</summary>
    string ExtractedContentDirectory,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    IReadOnlyList<SlotPluginFile> SlotPluginFiles,
    string? DockerImageUri = null,
    string? PluginDirectory = null)
{
    public WorkflowLaunchRequest(
        string extractedContentDirectory,
        IReadOnlyDictionary<string, string> environmentVariables)
        : this(extractedContentDirectory, environmentVariables, []) { }
}

