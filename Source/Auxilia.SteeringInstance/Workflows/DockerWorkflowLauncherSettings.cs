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
    /// RabbitMQ hostname as seen from inside the Docker network
    /// (e.g. the Testcontainer network alias "rabbitmq").
    /// </summary>
    public string RabbitMqHost { get; set; } = "localhost";

    public int RabbitMqPort { get; set; } = 5672;

    public string RabbitMqUserName { get; set; } = "guest";

    public string RabbitMqPassword { get; set; } = "guest";
}

