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

    /// <summary>
    /// Container port of the workflow's declared interactive web terminal (ttyd). The launcher
    /// exposes it and names the container <see cref="TerminalContainerName"/> so the backend —
    /// on the same Docker network — can reach it by name. Null = no terminal (the default).
    /// </summary>
    public int? PublishTerminalPort { get; init; }

    /// <summary>Deterministic container name/alias the terminal is reachable at on the shared network.</summary>
    public string? TerminalContainerName { get; init; }
}

/// <summary>What a launch produced: the container-network "name:port" of the web terminal, when any.</summary>
public sealed record WorkflowLaunchResult(string? TerminalEndpoint = null);

