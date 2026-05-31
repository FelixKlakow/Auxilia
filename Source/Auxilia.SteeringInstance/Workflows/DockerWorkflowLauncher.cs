using System.Text.Json;
using Auxilia.Workflows.Crypto;
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
    IDockerClientFactory clientFactory,
    ILogger<DockerWorkflowLauncher> logger) : IWorkflowLauncher
{

    public async Task LaunchAsync(WorkflowLaunchRequest request, CancellationToken ct = default)
    {
        var settings = settingsOptions.Value;
        var createParams = BuildCreateContainerParameters(request, settings);

        using var client = clientFactory.CreateClient(settings.DockerSocketPath);

        logger.LogInformation(
            "Creating workflow container. RuntimeImage={RuntimeImage} ExtractedDir={ExtractedDir} Network={Network}",
            settings.RuntimeImage, request.ExtractedContentDirectory, settings.NetworkName ?? "<default>");

        var created = await client.Containers.CreateContainerAsync(createParams, ct);

        if (request.SlotPluginFiles.Count > 0)
        {
            var pkgManifest = JsonSerializer.Deserialize<WorkflowPackageManifest>(
                File.ReadAllText(Path.Combine(request.ExtractedContentDirectory, "package-manifest.json")),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            var execRelDir = Path.GetDirectoryName(pkgManifest.ExecutableRelativePath)?.Replace('\\', '/');
            var targetDir = string.IsNullOrEmpty(execRelDir)
                ? request.ExtractedContentDirectory
                : Path.Combine(request.ExtractedContentDirectory,
                               execRelDir.Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(targetDir);
            foreach (var file in request.SlotPluginFiles)
            {
                File.Copy(file.DllPath,
                          Path.Combine(targetDir, Path.GetFileName(file.DllPath)),
                          overwrite: true);
                File.Copy(file.ManifestPath,
                          Path.Combine(targetDir, Path.GetFileName(file.ManifestPath)),
                          overwrite: true);
            }
        }

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
        var manifestPath = Path.Combine(request.ExtractedContentDirectory, "package-manifest.json");
        var manifestJson = File.ReadAllText(manifestPath);
        var manifest = JsonSerializer.Deserialize<WorkflowPackageManifest>(
            manifestJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
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
