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

    /// <summary>
    /// Host directory mounted read-write at <c>/workflow-output</c> in the container;
    /// declared outputs written there are persisted to the artifact store after success.
    /// </summary>
    public string? OutputDirectoryBind { get; init; }

    /// <summary>
    /// Effective network policy resolved at dispatch (ARCHITECTURE §10); the launcher uses
    /// it to select the container network. Null means no policy was resolved (legacy callers).
    /// </summary>
    public EffectiveNetworkPolicy? NetworkPolicy { get; init; }

    /// <summary>
    /// Host directory prepared by the Workspace Manager (ARCHITECTURE §9), mounted read-write
    /// at <c>/workspace</c> — workflows commit locally; pushes go through slots.
    /// </summary>
    public string? WorkspaceDirectoryBind { get; init; }
}

