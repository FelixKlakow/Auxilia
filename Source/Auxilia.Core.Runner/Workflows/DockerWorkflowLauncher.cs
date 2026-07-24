using System.Formats.Tar;
using System.Text.Json;
using Auxilia.Workflows.Crypto;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Runner.Workflows;

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

    public async Task<WorkflowLaunchResult> LaunchAsync(WorkflowLaunchRequest request, CancellationToken ct = default)
    {
        var settings = settingsOptions.Value;

        using var client = clientFactory.CreateClient(settings.DockerSocketPath);

        var createParams = request.DockerImageUri is not null
            ? BuildBakedImageContainerParameters(request, settings)
            : BuildCreateContainerParameters(request, settings);

        logger.LogInformation(
            "Creating workflow container. RuntimeImage={RuntimeImage} ExtractedDir={ExtractedDir} Network={Network}",
            settings.RuntimeImage, request.ExtractedContentDirectory, SelectNetworkName(request, settings) ?? "<default>");

        var created = await client.Containers.CreateContainerAsync(createParams, ct);

        if (request.SlotPluginFiles.Count > 0)
        {
            if (request.DockerImageUri is not null)
            {
                // Baked-image path: no bind-mount; inject via Docker tar API.
                var containerPluginDir = request.PluginDirectory ?? "/app";
                using var tarStream = BuildSlotPluginTar(request.SlotPluginFiles);
                await client.Containers.ExtractArchiveToContainerAsync(
                    created.ID,
                    new ContainerPathStatParameters { Path = containerPluginDir },
                    tarStream,
                    ct);
            }
            else
            {
                // ZIP-extracted path: copy files into the bind-mounted extracted directory.
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
        }

        await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);

        logger.LogInformation(
            "Workflow container started. RuntimeImage={RuntimeImage} ContainerId={ContainerId}",
            settings.RuntimeImage, created.ID[..Math.Min(12, created.ID.Length)]);

        // Terminal reachability is container-to-container on the shared network: the backend
        // proxies to "<name>:<port>". No host port is published — the browser only ever talks
        // to the backend, never to the workflow container directly.
        if (request is { PublishTerminalPort: { } terminalPort, TerminalContainerName: { } name })
        {
            logger.LogInformation(
                "Workflow terminal available. Name={Name} ContainerPort={ContainerPort}", name, terminalPort);
            return new WorkflowLaunchResult($"{name}:{terminalPort}");
        }

        return new WorkflowLaunchResult();
    }

    /// <summary>
    /// Selects the Docker network for a launch. A default-deny policy with no allowed
    /// endpoints is attached to <see cref="DockerWorkflowLauncherSettings.InternalNetworkName"/>
    /// (an operator-created <c>--internal</c> network without internet egress) when configured.
    /// Endpoint-granular enforcement requires the future egress proxy; this realizes only the
    /// no-egress case at the Docker level.
    /// </summary>
    internal static string? SelectNetworkName(
        WorkflowLaunchRequest request, DockerWorkflowLauncherSettings settings)
        => request.NetworkPolicy is { Mode: NetworkPolicyMode.DefaultDeny, AllowedEndpoints.Count: 0 }
           && !string.IsNullOrWhiteSpace(settings.InternalNetworkName)
            ? settings.InternalNetworkName
            : settings.NetworkName;

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

        var binds = new List<string> { $"{request.ExtractedContentDirectory}:/workflow:ro" };
        if (request.OutputDirectoryBind is not null)
            binds.Add($"{request.OutputDirectoryBind}:/workflow-output");
        if (request.WorkspaceDirectoryBind is not null)
            binds.Add($"{request.WorkspaceDirectoryBind}:/workspace");

        var parameters = new CreateContainerParameters
        {
            Image = settings.RuntimeImage,
            Cmd = [executablePath],
            Env = env,
            HostConfig = new HostConfig
            {
                AutoRemove = true,
                Binds = binds
            }
        };

        if (SelectNetworkName(request, settings) is { } networkName &&
            !string.IsNullOrWhiteSpace(networkName))
        {
            parameters.NetworkingConfig = new NetworkingConfig
            {
                EndpointsConfig = new Dictionary<string, EndpointSettings>
                {
                    [networkName] = new EndpointSettings()
                }
            };
        }

        ApplyTerminalPort(parameters, request);
        return parameters;
    }

    /// <summary>
    /// Builds <see cref="CreateContainerParameters"/> for a baked-image launch where the
    /// workflow executable is already baked into the Docker image (no bind-mount).
    /// </summary>
    internal static CreateContainerParameters BuildBakedImageContainerParameters(
        WorkflowLaunchRequest request,
        DockerWorkflowLauncherSettings settings)
    {
        ArgumentNullException.ThrowIfNull(request.DockerImageUri, nameof(request.DockerImageUri));

        var env = request.EnvironmentVariables
            .Select(kv => $"{kv.Key}={kv.Value}")
            .ToList();

        var binds = new List<string>();
        if (request.OutputDirectoryBind is not null)
            binds.Add($"{request.OutputDirectoryBind}:/workflow-output");
        if (request.WorkspaceDirectoryBind is not null)
            binds.Add($"{request.WorkspaceDirectoryBind}:/workspace");

        var parameters = new CreateContainerParameters
        {
            Image = request.DockerImageUri,
            Cmd   = null,
            Env   = env,
            HostConfig = new HostConfig
            {
                AutoRemove = true,
                Binds      = binds
            }
        };

        if (SelectNetworkName(request, settings) is { } networkName &&
            !string.IsNullOrWhiteSpace(networkName))
        {
            parameters.NetworkingConfig = new NetworkingConfig
            {
                EndpointsConfig = new Dictionary<string, EndpointSettings>
                {
                    [networkName] = new EndpointSettings()
                }
            };
        }

        ApplyTerminalPort(parameters, request);
        return parameters;
    }

    /// <summary>
    /// Exposes the workflow's declared web-terminal port and names the container so the backend
    /// (on the same Docker network) can reach the terminal at "&lt;name&gt;:&lt;port&gt;". No host
    /// port is published — the dashboard is the only client, and it proxies over the network.
    /// </summary>
    internal static void ApplyTerminalPort(
        CreateContainerParameters parameters, WorkflowLaunchRequest request)
    {
        if (request.PublishTerminalPort is not { } port)
            return;
        parameters.ExposedPorts = new Dictionary<string, EmptyStruct> { [$"{port}/tcp"] = default };
        if (request.TerminalContainerName is { Length: > 0 } name)
            parameters.Name = name;
    }

    /// <summary>
    /// Builds a raw (uncompressed) tar stream containing the DLL and manifest for each
    /// <see cref="SlotPluginFile"/>. Used to inject slot plugins into baked-image containers
    /// via <c>ExtractArchiveToContainerAsync</c>.
    /// </summary>
    private static MemoryStream BuildSlotPluginTar(IReadOnlyList<SlotPluginFile> files)
    {
        var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var dllEntry = new PaxTarEntry(TarEntryType.RegularFile, Path.GetFileName(file.DllPath));
                dllEntry.DataStream = new MemoryStream(File.ReadAllBytes(file.DllPath));
                writer.WriteEntry(dllEntry);

                var manifestEntry = new PaxTarEntry(TarEntryType.RegularFile, Path.GetFileName(file.ManifestPath));
                manifestEntry.DataStream = new MemoryStream(File.ReadAllBytes(file.ManifestPath));
                writer.WriteEntry(manifestEntry);
            }
        }
        stream.Seek(0, SeekOrigin.Begin);
        return stream;
    }
}
