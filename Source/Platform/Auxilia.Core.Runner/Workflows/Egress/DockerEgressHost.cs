using System.Text;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Runner.Workflows.Egress;

/// <summary>
/// One run's egress topology: its private <c>--internal</c> network (the workflow container's
/// ONLY network to the outside world) and the egress gateway on it, which alone also sits on the
/// outbound network. Everything is data: the allowlist, the platform relays, the terminal.
/// </summary>
public sealed record EgressPlan(
    Guid InstanceId,
    string AllowList,
    IReadOnlyList<string> Forwards,
    int? TerminalPort)
{
    /// <summary>The only broker identity the gateway's AMQP relay admits — the run's own.</summary>
    public string BusUser => Auxilia.Messaging.WorkloadBusIdentity.UserNameFor(InstanceId);

    public const string GatewayAlias = "egress";
    public const string WorkflowAlias = "workflow";
    public const int ProxyPort = 3128;
    public const int BusPort = 5672;

    public string NetworkName => $"auxilia-run-{InstanceId:N}";
    public string GatewayName => $"auxilia-egress-{InstanceId:N}";
    public string ProxyUrl => $"http://{GatewayAlias}:{ProxyPort}";

    /// <summary>
    /// Names of the run's relayed TCP endpoints (<c>tcp://host:port</c>): inside the workload they
    /// resolve to the gateway, which carries each connection on to the real host.
    /// </summary>
    public IReadOnlyList<string> TcpHosts { get; init; } = [];

    /// <summary>The gateway's address on the run network — known once it is up.</summary>
    public string? GatewayAddress { get; init; }

    /// <summary>
    /// Platform endpoints (<c>host:port</c>) the gateway refuses before any allow entry, by name
    /// and resolved address: the broker (reached only through the identity-pinned relay) and the
    /// operator's <c>ProtectedEndpoints</c>. Other ports of the same hosts stay reachable.
    /// </summary>
    public IReadOnlyList<string> ProtectedEndpoints { get; init; } = [];
}

/// <summary>A gateway log line, as the runner reads it (the gateway's JSON-lines contract).</summary>
public sealed record EgressEvent(string Event, string? Kind, string? Host, int? Port, string? Decision, string? Reason)
{
    public static EgressEvent? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{')
            return null;
        try
        {
            var e = JsonDocument.Parse(line).RootElement;
            string? Text(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new EgressEvent(Text("event") ?? "", Text("kind"), Text("host"),
                e.TryGetProperty("port", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null,
                Text("decision"), Text("reason"));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public interface IEgressHost
{
    /// <summary>
    /// Creates the run network and starts its gateway; returns once the gateway listens.
    /// <paramref name="onEvent"/> receives every gateway event live (denials above all).
    /// </summary>
    /// <summary>Brings the run's network and gateway up; returns the gateway's address on the run network.</summary>
    Task<string?> MaterializeAsync(IDockerClient client, EgressPlan plan, Func<EgressEvent, Task> onEvent, CancellationToken ct);

    /// <summary>Re-attaches the live event feed to a re-adopted run's gateway (no-op without one).</summary>
    Task ResumeAsync(Guid instanceId, Func<EgressEvent, Task> onEvent, CancellationToken ct = default);

    /// <summary>The run's terminal endpoint as the Core reaches it (published on the gateway).</summary>
    Task<string?> ResolveTerminalEndpointAsync(IDockerClient client, EgressPlan plan, CancellationToken ct);

    /// <summary>Removes the gateway and the network; returns the gateway's full log. Idempotent.</summary>
    Task<string?> TeardownAsync(Guid instanceId, CancellationToken ct = default);

    /// <summary>Tears down every egress topology whose instance is not live (startup sweep).</summary>
    Task<int> SweepOrphanedAsync(IReadOnlySet<Guid> liveInstanceIds, CancellationToken ct = default);
}

public sealed class DockerEgressHost(
    IOptions<DockerWorkflowLauncherSettings> settingsOptions,
    IDockerClientFactory clientFactory,
    ILogger<DockerEgressHost> logger) : IEgressHost
{
    internal const string EgressLabel = "auxilia.egress";

    public async Task<string?> MaterializeAsync(
        IDockerClient client, EgressPlan plan, Func<EgressEvent, Task> onEvent, CancellationToken ct)
    {
        var settings = settingsOptions.Value;
        await client.Networks.CreateNetworkAsync(new NetworksCreateParameters
        {
            Name = plan.NetworkName,
            Internal = true,
            Labels = settings.Stamp(Labels(plan.InstanceId))
        }, ct);

        var created = await client.Containers.CreateContainerAsync(BuildGatewayParameters(plan, settings), ct);
        // Outbound leg: the gateway — and only the gateway — also joins the egress network.
        await client.Networks.ConnectNetworkAsync(OutboundNetwork(settings), new NetworkConnectParameters
        {
            Container = created.ID
        }, ct);
        await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);
        await WaitUntilListeningAsync(client, created.ID, ct);
        StartFeed(created.ID, plan.InstanceId, onEvent);
        logger.LogInformation(
            "Egress gateway up. Instance={InstanceId} Network={Network} Allow={Allow}",
            plan.InstanceId, plan.NetworkName, plan.AllowList.Length == 0 ? "<none>" : plan.AllowList);
        var inspected = await client.Containers.InspectContainerAsync(created.ID, ct);
        return inspected.NetworkSettings?.Networks is { } networks
               && networks.TryGetValue(plan.NetworkName, out var endpoint)
               && endpoint.IPAddress is { Length: > 0 } address
            ? address
            : null;
    }

    /// <summary>Extracted static so unit tests verify parameter construction without a daemon.</summary>
    internal static CreateContainerParameters BuildGatewayParameters(EgressPlan plan, DockerWorkflowLauncherSettings settings)
    {
        var parameters = new CreateContainerParameters
        {
            Image = settings.EgressGatewayImage,
            Name = plan.GatewayName,
            Labels = settings.Stamp(Labels(plan.InstanceId)),
            Env =
            [
                $"EGRESS_ALLOW={plan.AllowList}",
                $"EGRESS_FORWARDS={string.Join(",", plan.Forwards)}",
                $"EGRESS_PORT={EgressPlan.ProxyPort}",
                $"EGRESS_AMQP_USER={plan.BusUser}",
                $"EGRESS_PROTECTED={string.Join(",", plan.ProtectedEndpoints)}",
            ],
            HostConfig = new HostConfig
            {
                // The gateway needs no privilege at all: it is unprivileged, read-only, and
                // cannot escalate; its process count and memory are small and bounded.
                Privileged = false,
                CapDrop = ["ALL"],
                SecurityOpt = ["no-new-privileges:true"],
                ReadonlyRootfs = true,
                PidsLimit = 256,
                Memory = 256L * 1024 * 1024,
                RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
            },
            NetworkingConfig = new NetworkingConfig
            {
                EndpointsConfig = new Dictionary<string, EndpointSettings>
                {
                    [plan.NetworkName] = new() { Aliases = [EgressPlan.GatewayAlias] }
                }
            }
        };
        if (plan.TerminalPort is { } port)
        {
            // The run's web terminal is relayed by the gateway (the workflow container has no
            // network but the run's own): published on loopback for a host-process Core, or
            // reachable by this container's name on the egress network for a containerized one.
            parameters.ExposedPorts = new Dictionary<string, EmptyStruct> { [$"{port}/tcp"] = default };
            if (DockerWorkflowLauncher.UsesLoopbackTerminal(settings))
                parameters.HostConfig.PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    [$"{port}/tcp"] = [new PortBinding { HostIP = "127.0.0.1", HostPort = "" }]
                };
        }
        return parameters;
    }

    /// <summary>The network the gateway's outbound leg joins (the operator's egress network, or Docker's default bridge).</summary>
    internal static string OutboundNetwork(DockerWorkflowLauncherSettings settings)
        => string.IsNullOrWhiteSpace(settings.NetworkName) ? "bridge" : settings.NetworkName;

    public async Task<string?> ResolveTerminalEndpointAsync(IDockerClient client, EgressPlan plan, CancellationToken ct)
    {
        if (plan.TerminalPort is not { } port)
            return null;
        if (!DockerWorkflowLauncher.UsesLoopbackTerminal(settingsOptions.Value))
            return $"{plan.GatewayName}:{port}";
        var inspection = await client.Containers.InspectContainerAsync(plan.GatewayName, ct);
        var hostPort = inspection.NetworkSettings?.Ports is { } ports && ports.TryGetValue($"{port}/tcp", out var bound)
            ? bound?.FirstOrDefault(b => b.HostPort is { Length: > 0 })?.HostPort
            : null;
        return hostPort is null ? null : $"127.0.0.1:{hostPort}";
    }

    public async Task ResumeAsync(Guid instanceId, Func<EgressEvent, Task> onEvent, CancellationToken ct = default)
    {
        using var client = clientFactory.CreateClient(settingsOptions.Value.DockerSocketPath);
        var gateway = (await ListGatewaysAsync(client, instanceId, ct)).FirstOrDefault();
        if (gateway is not null)
            StartFeed(gateway.ID, instanceId, onEvent, since: DateTimeOffset.UtcNow);
    }

    public async Task<string?> TeardownAsync(Guid instanceId, CancellationToken ct = default)
    {
        using var client = clientFactory.CreateClient(settingsOptions.Value.DockerSocketPath);
        string? log = null;
        foreach (var gateway in await ListGatewaysAsync(client, instanceId, ct))
        {
            log = await TryReadLogAsync(client, gateway.ID, ct);
            try
            {
                await client.Containers.RemoveContainerAsync(gateway.ID, new ContainerRemoveParameters { Force = true }, ct);
            }
            catch (DockerContainerNotFoundException)
            {
                // Already gone — fine.
            }
        }
        var networks = await client.Networks.ListNetworksAsync(new NetworksListParameters
        {
            Filters = LabelFilter($"{EgressLabel}=1", $"{DockerWorkflowLauncher.InstanceIdLabel}={instanceId:D}")
        }, ct);
        foreach (var network in networks)
        {
            try
            {
                await client.Networks.DeleteNetworkAsync(network.ID, ct);
            }
            catch (DockerApiException)
            {
                // Still attached while the workflow container winds down — the sweep catches it.
            }
        }
        return log;
    }

    public async Task<int> SweepOrphanedAsync(IReadOnlySet<Guid> liveInstanceIds, CancellationToken ct = default)
    {
        using var client = clientFactory.CreateClient(settingsOptions.Value.DockerSocketPath);
        var containers = await client.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = LabelFilter($"{EgressLabel}=1")
        }, ct);
        var networks = await client.Networks.ListNetworksAsync(new NetworksListParameters
        {
            Filters = LabelFilter($"{EgressLabel}=1")
        }, ct);
        var orphaned = containers.Select(c => c.Labels).Concat(networks.Select(n => n.Labels))
            .Select(labels => labels is not null
                              && labels.TryGetValue(DockerWorkflowLauncher.InstanceIdLabel, out var raw)
                              && Guid.TryParse(raw, out var id) ? id : (Guid?)null)
            .Where(id => id is { } value && !liveInstanceIds.Contains(value))
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        foreach (var instanceId in orphaned)
        {
            logger.LogWarning("Sweeping the orphaned egress gateway of instance {InstanceId}.", instanceId);
            await TeardownAsync(instanceId, ct);
        }
        return orphaned.Count;
    }

    private static async Task<IList<ContainerListResponse>> ListGatewaysAsync(
        IDockerClient client, Guid instanceId, CancellationToken ct)
        => await client.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = LabelFilter($"{EgressLabel}=1", $"{DockerWorkflowLauncher.InstanceIdLabel}={instanceId:D}")
        }, ct);

    private async Task WaitUntilListeningAsync(IDockerClient client, string containerId, CancellationToken ct)
    {
        var startupTimeout = TimeSpan.FromSeconds(Math.Max(5, settingsOptions.Value.EgressGatewayStartupSeconds));
        var deadline = DateTime.UtcNow + startupTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var inspection = await client.Containers.InspectContainerAsync(containerId, ct);
            if (inspection.State is { Running: false, Status: not "created" })
                throw new InvalidOperationException(
                    $"the egress gateway exited during startup (exit code {inspection.State.ExitCode}): "
                    + await TryReadLogAsync(client, containerId, ct));
            if ((await TryReadLogAsync(client, containerId, ct))?.Contains("\"event\":\"listening\"") == true)
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
        }
        throw new InvalidOperationException(
            $"the egress gateway did not start within {startupTimeout.TotalSeconds:0}s — is the image "
            + $"'{settingsOptions.Value.EgressGatewayImage}' available on this host?");
    }

    /// <summary>Follows the gateway's log for the life of the container, parsing each event.</summary>
    private void StartFeed(string containerId, Guid instanceId, Func<EgressEvent, Task> onEvent, DateTimeOffset? since = null)
        => _ = Task.Run(async () =>
        {
            try
            {
                using var client = clientFactory.CreateClient(settingsOptions.Value.DockerSocketPath);
                using var stream = await client.Containers.GetContainerLogsAsync(containerId, tty: false,
                    new ContainerLogsParameters
                    {
                        ShowStdout = true, Follow = true,
                        Since = since?.ToUnixTimeSeconds().ToString()
                    }, CancellationToken.None);
                var pending = new StringBuilder();
                var buffer = new byte[16 * 1024];
                while (true)
                {
                    var read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, CancellationToken.None);
                    if (read.EOF)
                        break;
                    pending.Append(Encoding.UTF8.GetString(buffer, 0, read.Count));
                    var text = pending.ToString();
                    var lastNewline = text.LastIndexOf('\n');
                    if (lastNewline < 0)
                        continue;
                    pending.Clear().Append(text[(lastNewline + 1)..]);
                    foreach (var line in text[..lastNewline].Split('\n'))
                        if (EgressEvent.TryParse(line.Trim()) is { } evt)
                            await onEvent(evt);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "The egress event feed of {InstanceId} ended.", instanceId);
            }
        });

    private static async Task<string?> TryReadLogAsync(IDockerClient client, string containerId, CancellationToken ct)
    {
        try
        {
            using var logs = await client.Containers.GetContainerLogsAsync(containerId, tty: false,
                new ContainerLogsParameters { ShowStdout = true, ShowStderr = true }, ct);
            using var stdout = new MemoryStream();
            using var stderr = new MemoryStream();
            await logs.CopyOutputToAsync(Stream.Null, stdout, stderr, ct);
            return (Encoding.UTF8.GetString(stdout.ToArray()) + Encoding.UTF8.GetString(stderr.ToArray())).Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Dictionary<string, string> Labels(Guid instanceId) => new()
    {
        [EgressLabel] = "1",
        [DockerWorkflowLauncher.InstanceIdLabel] = instanceId.ToString("D")
    };

    private static Dictionary<string, IDictionary<string, bool>> LabelFilter(params string[] labels)
        => new() { ["label"] = labels.ToDictionary(l => l, _ => true) };
}
