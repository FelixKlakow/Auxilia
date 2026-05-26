using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Options;

namespace Auxilia.SteeringInstance.Workflows;

/// <summary>
/// Launches workflow containers via the Docker API over the local (or configured) Docker socket.
/// No Docker CLI is required inside the SteeringInstance container — only socket access.
/// The container is started with <c>AutoRemove = true</c> so it is cleaned up on exit.
/// </summary>
public sealed class DockerWorkflowLauncher(
    IOptions<DockerWorkflowLauncherSettings> settingsOptions,
    ILogger<DockerWorkflowLauncher> logger) : IWorkflowLauncher
{
    public async Task LaunchAsync(WorkflowLaunchRequest request, CancellationToken ct = default)
    {
        var settings = settingsOptions.Value;
        var createParams = BuildCreateContainerParameters(request, settings);

        using var client = new DockerClientConfiguration(new Uri(settings.DockerSocketPath))
            .CreateClient();

        logger.LogInformation(
            "Creating workflow container. Image={Image} Network={Network}",
            request.Image, settings.NetworkName ?? "<default>");

        var created = await client.Containers.CreateContainerAsync(createParams, ct);
        await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);

        logger.LogInformation(
            "Workflow container started. Image={Image} ContainerId={ContainerId}",
            request.Image, created.ID[..Math.Min(12, created.ID.Length)]);
    }

    /// <summary>
    /// Builds the Docker <see cref="CreateContainerParameters"/> for the given request and settings.
    /// Extracted as <c>internal static</c> so unit tests can verify parameter construction
    /// without requiring a live Docker daemon.
    /// </summary>
    internal static CreateContainerParameters BuildCreateContainerParameters(
        WorkflowLaunchRequest request,
        DockerWorkflowLauncherSettings settings)
    {
        var env = request.EnvironmentVariables
            .Select(kv => $"{kv.Key}={kv.Value}")
            .ToList();

        var parameters = new CreateContainerParameters
        {
            Image = request.Image,
            Env = env,
            HostConfig = new HostConfig { AutoRemove = true }
        };

        if (!string.IsNullOrWhiteSpace(settings.NetworkName))
        {
            parameters.NetworkingConfig = new NetworkingConfig
            {
                EndpointsConfig = new Dictionary<string, EndpointSettings>
                {
                    [settings.NetworkName] = new EndpointSettings()
                }
            };
        }

        return parameters;
    }
}
