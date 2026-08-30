using System.Formats.Tar;
using System.Text.Json;
using Auxilia.Workflows.Crypto;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Runner.Workflows;

/// <summary>
/// Launches workflow containers via the Docker API over the local (or configured) Docker socket.
/// No Docker CLI is required inside the Core.Runner container — only socket access.
/// A background watcher waits for each container's exit, captures its exit code and log tail
/// (so a crash is never silent), removes the container, and reports the exit to the dispatcher
/// via <see cref="WorkflowLaunchRequest.OnExited"/>.
/// The extracted workflow package is bind-mounted read-only into the container at <c>/workflow</c>.
/// </summary>
public sealed class DockerWorkflowLauncher(
    IOptions<DockerWorkflowLauncherSettings> settingsOptions,
    IDockerClientFactory clientFactory,
    Pods.IPodHost podHost,
    ILogger<DockerWorkflowLauncher> logger) : IWorkflowLauncher, IWorkflowContainerHost
{

    public async Task<WorkflowLaunchResult> LaunchAsync(WorkflowLaunchRequest request, CancellationToken ct = default)
    {
        var settings = settingsOptions.Value;

        using var client = clientFactory.CreateClient(settings.DockerSocketPath);

        // Environment capabilities layer ON TOP of the workflow image: the composed image is
        // content-addressed (base + fragments), so every distinct combination builds once and
        // every later run with the same selection starts instantly from the cache.
        if (request is { DockerImageUri: { } baseImage, EnvironmentLayers.Count: > 0 })
        {
            var composed = await EnsureComposedImageAsync(
                client, baseImage, request.EnvironmentLayers, ct);
            request = request with { DockerImageUri = composed };
        }

        // The run's pod comes up FIRST (private network, companions in start order, each
        // readiness-gated); any failure from here to the workflow's start tears it down —
        // a failed launch must never leave companions running. Materialization itself is
        // inside the teardown scope: a companion failing readiness mid-pod must not leave the
        // earlier companions (or the network/volumes) behind. Teardown removes by labels, so
        // it is tolerant of arbitrary partial materialization.
        try
        {
            if (request.Pod is { } pod)
                await podHost.MaterializeAsync(client, pod, ct);
            return await LaunchWorkflowContainerAsync(client, request, settings, ct);
        }
        catch when (request.Pod is { } failedPod)
        {
            await podHost.TeardownAsync(failedPod.InstanceId, CancellationToken.None);
            throw;
        }
    }

    private async Task<WorkflowLaunchResult> LaunchWorkflowContainerAsync(
        IDockerClient client, WorkflowLaunchRequest request,
        DockerWorkflowLauncherSettings settings, CancellationToken ct)
    {
        var createParams = request.DockerImageUri is not null
            ? BuildBakedImageContainerParameters(request, settings)
            : BuildCreateContainerParameters(request, settings);

        logger.LogInformation(
            "Creating workflow container. RuntimeImage={RuntimeImage} ExtractedDir={ExtractedDir} Network={Network}",
            settings.RuntimeImage, request.ExtractedContentDirectory, SelectNetworkName(request, settings) ?? "<default>");

        var created = await client.Containers.CreateContainerAsync(createParams, ct);
        logger.LogInformation(
            "Workflow container created. ContainerId={ContainerId}",
            created.ID[..Math.Min(12, created.ID.Length)]);

        // Everything between creation and a successful start must remove the created container
        // on failure — it would otherwise linger (with pod volumes bound) until the next runner
        // restart. Nothing watches an unstarted container, so removal here races nobody.
        try
        {
            // Persist the container↔run mapping BEFORE start: a crash from here on must leave a
            // record the re-adoption pass can match, never an anonymous container.
            if (request.OnContainerCreated is { } onCreated)
                await onCreated(created.ID);

            // The workflow container joins the pod network ADDITIONALLY (alias "workflow"): it
            // keeps its governed egress network, staying the pod's only path to the outside.
            if (request.Pod is { } pod)
                await client.Networks.ConnectNetworkAsync(pod.NetworkName, new NetworkConnectParameters
                {
                    Container = created.ID,
                    EndpointConfig = new EndpointSettings { Aliases = ["workflow"] }
                }, ct);

            // Pre-flight linker check (baked images): every member a plugin references on a shared
            // assembly must exist in the image's copy — build skew fails HERE with a clear message,
            // not mid-session with a MissingMethodException.
            if (request is { DockerImageUri: not null, SlotPluginFiles.Count: > 0 })
                await VerifyPluginCompatibilityAsync(client, created.ID, request.SlotPluginFiles, ct);

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
        }
        catch
        {
            await TryRemoveContainerAsync(client, created.ID);
            throw;
        }

        logger.LogInformation(
            "Workflow container started. RuntimeImage={RuntimeImage} ContainerId={ContainerId}",
            settings.RuntimeImage, created.ID[..Math.Min(12, created.ID.Length)]);

        // Watch the container to its end on a background task: capture the exit code + log tail,
        // remove the container (we own cleanup — no AutoRemove, or the evidence would vanish),
        // and hand the exit to the dispatcher so a crashed workflow fails its run.
        _ = Task.Run(() => WatchContainerAsync(settings, created.ID, request.OnExited));

        // Terminal reachability per publish mode (loopback host port, or container name on the
        // shared network). The browser only ever talks to the Core's authenticated proxy,
        // never to the workflow container directly.
        var terminalEndpoint = await ResolveTerminalEndpointAsync(client, created.ID, request, settings, ct);
        if (terminalEndpoint is not null)
            logger.LogInformation("Workflow terminal available. Endpoint={Endpoint}", terminalEndpoint);
        return new WorkflowLaunchResult(terminalEndpoint);
    }

    /// <summary>
    /// Waits for the container to exit, captures its exit code and log tail, removes the
    /// container, and reports the exit. Long-running by design (as long as the workflow itself);
    /// every failure here is logged, never thrown — the watcher must not take the runner down.
    /// </summary>
    private async Task WatchContainerAsync(
        DockerWorkflowLauncherSettings settings, string containerId, Func<ContainerExit, Task>? onExited)
    {
        try
        {
            using var client = clientFactory.CreateClient(settings.DockerSocketPath);
            var wait = await client.Containers.WaitContainerAsync(containerId, CancellationToken.None);

            string? logTail = null;
            try
            {
                using var logs = await client.Containers.GetContainerLogsAsync(
                    containerId, tty: false,
                    new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Tail = "40" },
                    CancellationToken.None);
                using var stdout = new MemoryStream();
                using var stderr = new MemoryStream();
                await logs.CopyOutputToAsync(Stream.Null, stdout, stderr, CancellationToken.None);
                logTail = (System.Text.Encoding.UTF8.GetString(stdout.ToArray()) + "\n"
                           + System.Text.Encoding.UTF8.GetString(stderr.ToArray())).Trim();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not read logs of exited container {ContainerId}.", containerId);
            }

            try
            {
                await client.Containers.RemoveContainerAsync(
                    containerId, new ContainerRemoveParameters { Force = true }, CancellationToken.None);
            }
            catch (DockerContainerNotFoundException)
            {
                // Already gone — fine.
            }

            logger.LogInformation(
                "Workflow container exited. ContainerId={ContainerId} ExitCode={ExitCode}",
                containerId[..Math.Min(12, containerId.Length)], wait.StatusCode);

            if (onExited is not null)
                await onExited(new ContainerExit(wait.StatusCode, logTail));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Container exit watcher failed for {ContainerId}.", containerId);
        }
    }

    /// <summary>
    /// Verifies each injected plugin's referenced member surface against the shared assemblies
    /// the image actually ships (read via the container archive API before start).
    /// </summary>
    private async Task VerifyPluginCompatibilityAsync(
        IDockerClient client, string containerId, IReadOnlyList<SlotPluginFile> plugins, CancellationToken ct)
    {
        var imageAssemblies = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in plugins)
        {
            var pluginBytes = await File.ReadAllBytesAsync(plugin.DllPath, ct);
            IReadOnlyList<string> referenced;
            try
            {
                referenced = PluginCompatibilityChecker.ReferencedAssemblyNames(pluginBytes);
            }
            catch (BadImageFormatException)
            {
                // Not a readable .NET assembly (test doubles, corrupt file) — nothing to
                // compare here; loading it will fail loudly later if it is genuinely broken.
                continue;
            }
            var targets = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in referenced
                         .Where(n => n.StartsWith("Auxilia", StringComparison.OrdinalIgnoreCase)))
            {
                if (!imageAssemblies.TryGetValue(name, out var bytes))
                    imageAssemblies[name] = bytes =
                        await TryReadContainerFileAsync(client, containerId, $"/app/{name}.dll", ct);
                if (bytes is not null)
                    targets[name] = bytes;
            }

            if (targets.Count == 0)
                continue;
            var missing = PluginCompatibilityChecker.FindMissingReferences(pluginBytes, targets);
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    $"slot plugin '{Path.GetFileName(plugin.DllPath)}' is incompatible with the workflow "
                    + $"image (missing: {string.Join("; ", missing.Take(3))}"
                    + $"{(missing.Count > 3 ? $" — and {missing.Count - 3} more" : "")}) — "
                    + "rebuild the workflow image, or the plugin, so both ship the same contract build.");
        }
    }

    /// <summary>One file's bytes from the created (not yet started) container; null when absent.</summary>
    private static async Task<byte[]?> TryReadContainerFileAsync(
        IDockerClient client, string containerId, string path, CancellationToken ct)
    {
        try
        {
            var archive = await client.Containers.GetArchiveFromContainerAsync(
                containerId, new GetArchiveFromContainerParameters { Path = path }, false, ct);
            // Docker's chunked response stream signals its end by THROWING on the final read —
            // buffer it fully first so the tar reader sees a well-behaved stream.
            using var buffered = new MemoryStream();
            await using (var stream = archive.Stream)
            {
                try
                {
                    await stream.CopyToAsync(buffered, ct);
                }
                catch (EndOfStreamException)
                {
                    // expected: the chunked transfer ended
                }
            }
            buffered.Position = 0;
            await using var reader = new TarReader(buffered);
            while (await reader.GetNextEntryAsync(cancellationToken: ct) is { } entry)
            {
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                    || entry.DataStream is null)
                    continue;
                using var buffer = new MemoryStream();
                await entry.DataStream.CopyToAsync(buffer, ct);
                return buffer.ToArray();
            }
            return null;
        }
        catch (DockerApiException)
        {
            return null; // the image does not ship this assembly — nothing to compare
        }
    }

    private async Task TryRemoveContainerAsync(IDockerClient client, string containerId)
    {
        try
        {
            await client.Containers.RemoveContainerAsync(
                containerId, new ContainerRemoveParameters { Force = true }, CancellationToken.None);
        }
        catch (DockerApiException ex)
        {
            logger.LogWarning(ex, "Could not remove container {ContainerId} after a failed launch.", containerId);
        }
    }

    /// <summary>
    /// Builds (or reuses) the composed environment image: the workflow image plus one Dockerfile
    /// fragment per selected capability, tagged by the content hash of base + fragments.
    /// </summary>
    private async Task<string> EnsureComposedImageAsync(
        IDockerClient client, string baseImage, IReadOnlyList<string> layers, CancellationToken ct)
    {
        // The base image's ID (content digest) goes into the tag: a rebuilt workflow image with
        // the same name must never reuse a composed image built from its predecessor.
        string baseIdentity;
        try
        {
            baseIdentity = baseImage + "@" + (await client.Images.InspectImageAsync(baseImage, ct)).ID;
        }
        catch (DockerImageNotFoundException)
        {
            // Not local yet (the compose build will pull it) — no stale composition can exist.
            baseIdentity = baseImage;
        }
        var tag = ComposedImageTag(baseIdentity, layers);
        try
        {
            await client.Images.InspectImageAsync(tag, ct);
            logger.LogInformation("Composed environment image cached. Tag={Tag}", tag);
            return tag;
        }
        catch (DockerImageNotFoundException)
        {
            // fall through to build
        }

        var dockerfile = ComposeDockerfile(baseImage, layers);
        logger.LogInformation(
            "Composing environment image. Base={Base} Layers={LayerCount} Tag={Tag}",
            baseImage, layers.Count, tag);

        using var context = BuildDockerfileTar(dockerfile);
        string? buildError = null;
        var progress = new Progress<JSONMessage>(m =>
        {
            if (m.ErrorMessage is { Length: > 0 } error)
                buildError = error;
        });
        await client.Images.BuildImageFromDockerfileAsync(
            new ImageBuildParameters { Dockerfile = "Dockerfile", Tags = [tag] },
            context, null, null, progress, ct);
        if (buildError is not null)
            throw new InvalidOperationException($"environment image build failed: {buildError}");

        // The build API streams; make sure the tagged image actually exists before launch.
        await client.Images.InspectImageAsync(tag, ct);
        logger.LogInformation("Composed environment image built. Tag={Tag}", tag);
        return tag;
    }

    /// <summary>Content-addressed tag: same base + same fragments = the same cached image.</summary>
    internal static string ComposedImageTag(string baseImage, IReadOnlyList<string> layers)
    {
        var content = baseImage + "\n\0" + string.Join("\n\0", layers);
        var hash = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));
        return $"auxilia-env:{hash[..16]}";
    }

    /// <summary>The generated Dockerfile: the workflow image as base, one fragment per capability.</summary>
    internal static string ComposeDockerfile(string baseImage, IReadOnlyList<string> layers)
        => $"FROM {baseImage}\n" + string.Join("\n", layers.Select(l => l.TrimEnd())) + "\n";

    private static MemoryStream BuildDockerfileTar(string dockerfile)
    {
        var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, leaveOpen: true))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "Dockerfile")
            {
                DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(dockerfile)),
            };
            writer.WriteEntry(entry);
        }
        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// Removes composed environment images (<c>auxilia-env:*</c>) older than the configured max
    /// age and not referenced by any container. Because the tags are content-addressed, the sweep
    /// can never break a launch — a purged composition that is selected again simply rebuilds.
    /// Scoped strictly to the <c>auxilia-env</c> repository; nothing else on the host is touched.
    /// </summary>
    public async Task<int> SweepComposedImagesAsync(CancellationToken ct = default)
    {
        var settings = settingsOptions.Value;
        if (settings.ComposedImageMaxAgeDays <= 0)
            return 0;
        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(settings.ComposedImageMaxAgeDays);
        using var client = clientFactory.CreateClient(settings.DockerSocketPath);
        var images = await client.Images.ListImagesAsync(new ImagesListParameters
        {
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["reference"] = new Dictionary<string, bool> { ["auxilia-env"] = true },
            },
        }, ct);
        var inUse = (await client.Containers.ListContainersAsync(new ContainersListParameters { All = true }, ct))
            .Select(c => c.ImageID)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToHashSet();
        var removed = 0;
        foreach (var image in images)
        {
            if (image.Created >= cutoff || inUse.Contains(image.ID))
                continue;
            foreach (var tag in image.RepoTags ?? [])
            {
                logger.LogInformation(
                    "Removing expired composed environment image {Tag} (created {CreatedUtc:u}).",
                    tag, image.Created);
                await client.Images.DeleteImageAsync(tag, new ImageDeleteParameters(), ct);
            }
            removed++;
        }
        return removed;
    }

    internal const string WorkflowLabel = "auxilia.workflow";
    internal const string InstanceIdLabel = "auxilia.instance-id";

    /// <summary>
    /// Every workflow container is labeled so the re-adoption pass (and any operator tooling)
    /// can find Auxilia's containers WITHOUT touching anything else on the Docker host, and map
    /// each one back to its run via <see cref="InstanceIdLabel"/>. Single-runner-per-host
    /// assumption; a shared host would need per-runner ownership labels with stable ids.
    /// </summary>
    internal static Dictionary<string, string> BuildContainerLabels(WorkflowLaunchRequest request)
    {
        var labels = new Dictionary<string, string> { [WorkflowLabel] = "1" };
        if (request.InstanceId is { } instanceId)
            labels[InstanceIdLabel] = instanceId.ToString("D");
        return labels;
    }

    /// <summary>All workflow containers on this host (running AND exited), for re-adoption.</summary>
    public async Task<IReadOnlyList<WorkflowContainerInfo>> ListWorkflowContainersAsync(CancellationToken ct = default)
    {
        var settings = settingsOptions.Value;
        using var client = clientFactory.CreateClient(settings.DockerSocketPath);
        var containers = await client.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool> { [$"{WorkflowLabel}=1"] = true },
            },
        }, ct);
        return containers.Select(c => new WorkflowContainerInfo(
            c.ID,
            c.Labels is not null
            && c.Labels.TryGetValue(InstanceIdLabel, out var raw)
            && Guid.TryParse(raw, out var id) ? id : null,
            string.Equals(c.State, "running", StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>
    /// Re-attaches the exit watcher to an existing container (re-adoption after a restart).
    /// For an already-exited container the wait returns immediately, so its REAL exit code and
    /// log tail are collected instead of a generic failure.
    /// </summary>
    public void AttachExitWatcher(string containerId, Func<ContainerExit, Task> onExited)
        => _ = Task.Run(() => WatchContainerAsync(settingsOptions.Value, containerId, onExited));

    /// <summary>Clean-kill primitive: force-removes a container that cannot be re-adopted.</summary>
    public async Task RemoveContainerAsync(string containerId, CancellationToken ct = default)
    {
        var settings = settingsOptions.Value;
        using var client = clientFactory.CreateClient(settings.DockerSocketPath);
        try
        {
            await client.Containers.RemoveContainerAsync(
                containerId, new ContainerRemoveParameters { Force = true }, ct);
        }
        catch (DockerContainerNotFoundException)
        {
            // Already gone — fine.
        }
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
        AddPodVolumeBinds(binds, request);

        var parameters = new CreateContainerParameters
        {
            Image = settings.RuntimeImage,
            Cmd = [executablePath],
            Env = env,
            Labels = BuildContainerLabels(request),
            HostConfig = new HostConfig
            {
                // No AutoRemove: the exit watcher collects the exit code + log tail first,
                // then removes the container — a crash must leave evidence, not vanish.
                Binds = binds,
                ExtraHosts = settings.MapHostGateway ? ["host.docker.internal:host-gateway"] : null
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

        ApplyTerminalPort(parameters, request, settings);
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
        AddPodVolumeBinds(binds, request);

        var parameters = new CreateContainerParameters
        {
            Image = request.DockerImageUri,
            Cmd   = null,
            Env   = env,
            Labels = BuildContainerLabels(request),
            HostConfig = new HostConfig
            {
                // No AutoRemove: the exit watcher collects the exit code + log tail first,
                // then removes the container — a crash must leave evidence, not vanish.
                Binds      = binds,
                ExtraHosts = settings.MapHostGateway ? ["host.docker.internal:host-gateway"] : null
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

        ApplyTerminalPort(parameters, request, settings);
        return parameters;
    }

    /// <summary>
    /// Exposes the workflow's declared web-terminal port and names the container. In "loopback"
    /// mode the port is additionally published to an ephemeral 127.0.0.1 host port (for a
    /// host-process Core beside this runner); in "container-network" mode it stays
    /// container-to-container ("&lt;name&gt;:&lt;port&gt;" on the shared network). Either way the
    /// Core's authenticated proxy is the only thing end clients ever talk to.
    /// </summary>
    internal static void ApplyTerminalPort(
        CreateContainerParameters parameters, WorkflowLaunchRequest request,
        DockerWorkflowLauncherSettings settings)
    {
        if (request.PublishTerminalPort is not { } port)
            return;
        parameters.ExposedPorts = new Dictionary<string, EmptyStruct> { [$"{port}/tcp"] = default };
        if (request.TerminalContainerName is { Length: > 0 } name)
            parameters.Name = name;
        if (UsesLoopbackTerminal(settings))
        {
            parameters.HostConfig ??= new HostConfig();
            parameters.HostConfig.PortBindings = new Dictionary<string, IList<PortBinding>>
            {
                // Empty HostPort = the daemon assigns an ephemeral port; 127.0.0.1 keeps the
                // terminal off every non-local interface.
                [$"{port}/tcp"] = [new PortBinding { HostIP = "127.0.0.1", HostPort = "" }]
            };
        }
    }

    /// <summary>Every pod volume rides into the workflow container under <c>/workspace/pod/&lt;name&gt;</c>.</summary>
    internal static void AddPodVolumeBinds(List<string> binds, WorkflowLaunchRequest request)
    {
        if (request.Pod is not { } pod)
            return;
        binds.AddRange(pod.Volumes.Select(v =>
            $"{v.DockerVolumeName}:{Auxilia.Workflows.WorkflowEnvironmentVariables.PodVolumeRoot}/{v.Name}"));
    }

    internal static bool UsesLoopbackTerminal(DockerWorkflowLauncherSettings settings)
        => !string.Equals(settings.TerminalPublishMode, "container-network", StringComparison.OrdinalIgnoreCase);

    /// <summary>The Core-reachable endpoint of a started container's terminal, per publish mode.</summary>
    private static async Task<string?> ResolveTerminalEndpointAsync(
        IDockerClient client, string containerId, WorkflowLaunchRequest request,
        DockerWorkflowLauncherSettings settings, CancellationToken ct)
    {
        if (request.PublishTerminalPort is not { } port)
            return null;
        if (!UsesLoopbackTerminal(settings))
            return request.TerminalContainerName is { Length: > 0 } name ? $"{name}:{port}" : null;

        var inspection = await client.Containers.InspectContainerAsync(containerId, ct);
        var bindings = inspection.NetworkSettings?.Ports is { } ports
                       && ports.TryGetValue($"{port}/tcp", out var bound)
            ? bound
            : null;
        var hostPort = bindings?.FirstOrDefault(b => b.HostPort is { Length: > 0 })?.HostPort;
        return hostPort is null ? null : $"127.0.0.1:{hostPort}";
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

                // Bundled dependency closure (BundleDependencies plugins): lands beside the
                // plugin in the app base dir, where the default load context resolves it.
                foreach (var dependency in file.DependencyPaths ?? [])
                {
                    var entry = new PaxTarEntry(TarEntryType.RegularFile, Path.GetFileName(dependency));
                    entry.DataStream = new MemoryStream(File.ReadAllBytes(dependency));
                    writer.WriteEntry(entry);
                }
            }
        }
        stream.Seek(0, SeekOrigin.Begin);
        return stream;
    }
}
