namespace Auxilia.SteeringInstance.Workflows;

/// <summary>
/// Configuration for <see cref="DockerWorkflowLauncher"/>.
/// All fields that need to differ between CI and local dev live here so the
/// launcher itself stays environment-agnostic.
/// </summary>
public sealed class DockerWorkflowLauncherSettings
{
    /// <summary>
    /// Name of the Docker network workflow containers are attached to.
    /// Must be the same network that RabbitMQ is reachable on.
    /// Leave null to run containers without explicit network assignment (uses Docker default).
    /// </summary>
    public string? NetworkName { get; set; }

    /// <summary>
    /// Name of an operator-created internal Docker network (<c>docker network create --internal</c>)
    /// that still hosts RabbitMQ but has no internet egress. Containers whose effective network
    /// policy is default-deny with no allowed endpoints are attached here instead of
    /// <see cref="NetworkName"/>. Leave null to disable internal-network isolation.
    /// </summary>
    public string? InternalNetworkName { get; set; }

    /// <summary>
    /// RabbitMQ hostname as seen from inside the Docker network
    /// (e.g. the Testcontainer network alias "rabbitmq").
    /// </summary>
    public string RabbitMqHost { get; set; } = "localhost";

    public int RabbitMqPort { get; set; } = 5672;

    public string RabbitMqUserName { get; set; } = "guest";

    public string RabbitMqPassword { get; set; } = "guest";

    /// <summary>
    /// URI of the Docker daemon socket used to launch workflow containers.
    /// Defaults to the standard Unix socket. Override in config or env for remote daemons.
    /// </summary>
    public string DockerSocketPath { get; set; } = "unix:///var/run/docker.sock";

    /// <summary>
    /// Base container image used to run workflow assemblies.
    /// The extracted package is bind-mounted over the container filesystem.
    /// </summary>
    public string RuntimeImage { get; set; } = "mcr.microsoft.com/dotnet/runtime:8.0";

    /// <summary>
    /// Maps ProviderType → absolute path to the *.slothandler.dll on the SteeringInstance filesystem.
    /// The matching *.slothandler.manifest.json is always co-located with the DLL.
    /// </summary>
    public Dictionary<string, string> SlotPackages { get; set; } = new();

    /// <summary>
    /// Additional environment variables injected into every workflow container launch.
    /// For example: <c>{"AUXILIA_DEVELOPER_MODE": "1"}</c>.
    /// </summary>
    public Dictionary<string, string>? ExtraEnvironmentVariables { get; set; }

    /// <summary>
    /// Host name under which published web-terminal ports are reachable FROM THE BACKEND:
    /// "host.docker.internal" when the backend itself runs in a container (the default),
    /// "localhost" for bare-process development.
    /// </summary>
    public string TerminalPublishHost { get; set; } = "host.docker.internal";
}


