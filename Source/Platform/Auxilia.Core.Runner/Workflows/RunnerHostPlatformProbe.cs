using Microsoft.Extensions.Options;

namespace Auxilia.Core.Runner.Workflows;

/// <summary>
/// Probes the Docker daemon once for the platform it executes (OS type + architecture) — the
/// daemon's answer, not the runner process's OS, since the socket may be remote. The result is
/// cached for the process lifetime; until the daemon is reachable the probe yields null and the
/// heartbeat simply advertises no platform.
/// </summary>
public sealed class RunnerHostPlatformProbe(
    IDockerClientFactory dockerClients,
    IOptions<DockerWorkflowLauncherSettings> settings,
    ILogger<RunnerHostPlatformProbe> logger)
{
    private (string Os, string Architecture)? _cached;

    public async Task<(string Os, string Architecture)?> GetAsync(CancellationToken ct)
    {
        if (_cached is { } cached)
            return cached;
        try
        {
            using var client = dockerClients.CreateClient(settings.Value.DockerSocketPath);
            var info = await client.System.GetSystemInfoAsync(ct);
            if (info.OSType is not { Length: > 0 })
                return null;
            _cached = (info.OSType.ToLowerInvariant(), info.Architecture ?? "");
            logger.LogInformation(
                "Docker daemon platform probed. Os={Os} Architecture={Architecture}",
                _cached.Value.Os, _cached.Value.Architecture);
            return _cached;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Docker daemon platform probe failed — will retry on the next beat.");
            return null;
        }
    }
}
