using System.Text.Json;
using Auxilia.SteeringInstance.Workflows.Storage;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Options;

namespace Auxilia.SteeringInstance.Workflows;

/// <summary>
/// Launches workflow containers via the Docker API over the local (or configured) Docker socket.
/// No Docker CLI is required inside the SteeringInstance container — only socket access.
/// The container is started with <c>AutoRemove = true</c> so it is cleaned up on exit.
/// The extracted workflow package is bind-mounted read-only into the container at <c>/workflow</c>.
/// </summary>
public sealed class DockerWorkflowLauncher(
    IOptions<DockerWorkflowLauncherSettings> settingsOptions,
    ILogger<DockerWorkflowLauncher> logger) : IWorkflowLauncher
{
    private static readonly JsonSerializerOptions ManifestReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task LaunchAsync(WorkflowLaunchRequest request, CancellationToken ct = default)
    {
        var settings = settingsOptions.Value;
        var createParams = BuildCreateContainerParameters(request, settings);

        using var client = new DockerClientConfiguration(new Uri(settings.DockerSocketPath))
            .CreateClient();

        logger.LogInformation(
            "Creating workflow container. RuntimeImage={RuntimeImage} ExtractedDir={ExtractedDir} Network={Network}",
            settings.RuntimeImage, request.ExtractedContentDirectory, settings.NetworkName ?? "<default>");

        var created = await client.Containers.CreateContainerAsync(createParams, ct);
        await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);

        logger.LogInformation(
            "Workflow container started. RuntimeImage={RuntimeImage} ContainerId={ContainerId}",
            settings.RuntimeImage, created.ID[..Math.Min(12, created.ID.Length)]);
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
        var manifestPath = Path.Combine(request.ExtractedContentDirectory, "manifest.json");
        var manifestJson = File.ReadAllText(manifestPath);
        var manifest = JsonSerializer.Deserialize<WorkflowPackageManifest>(manifestJson, ManifestReadOptions)!;
        var executablePath = $"/workflow/{manifest.ExecutableRelativePath}";

        var env = request.EnvironmentVariables
            .Select(kv => $"{kv.Key}={kv.Value}")
            .ToList();

        var parameters = new CreateContainerParameters
        {
            Image = settings.RuntimeImage,
            Cmd = [executablePath],
            Env = env,
            HostConfig = new HostConfig
            {
                AutoRemove = true,
                Binds = [$"{request.ExtractedContentDirectory}:/workflow:ro"]
            }
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
