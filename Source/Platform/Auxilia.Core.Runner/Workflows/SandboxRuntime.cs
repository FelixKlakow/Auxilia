using Docker.DotNet;

namespace Auxilia.Core.Runner.Workflows;

/// <summary>
/// The OCI runtime agent workloads run under — the second kernel boundary around them. An
/// explicitly configured <see cref="DockerWorkflowLauncherSettings.ContainerRuntime"/> wins;
/// otherwise the first of <see cref="DockerWorkflowLauncherSettings.PreferredSandboxRuntimes"/>
/// the daemon offers is chosen automatically, so a host that has a sandbox installed always uses
/// it. With <see cref="DockerWorkflowLauncherSettings.RequireSandboxRuntime"/> a launch without
/// one is refused instead of falling back to the shared host kernel.
/// </summary>
public static class SandboxRuntime
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static IReadOnlyCollection<string>? _offered;

    /// <exception cref="InvalidOperationException">A sandbox is required and the daemon offers none.</exception>
    public static async Task<string?> ResolveAsync(
        IDockerClient client, DockerWorkflowLauncherSettings settings, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(settings.ContainerRuntime))
            return settings.ContainerRuntime;
        var runtime = Select(await OfferedAsync(client, ct), settings.PreferredSandboxRuntimes);
        if (runtime is null && settings.RequireSandboxRuntime == true)
            throw new InvalidOperationException(
                "this runner requires a sandboxed container runtime but the Docker daemon offers none of: "
                + string.Join(", ", settings.PreferredSandboxRuntimes));
        return runtime;
    }

    /// <summary>The first preferred runtime the daemon offers, or null.</summary>
    internal static string? Select(IReadOnlyCollection<string> offered, IEnumerable<string> preferred)
        => preferred.FirstOrDefault(p => offered.Contains(p, StringComparer.Ordinal));

    private static async Task<IReadOnlyCollection<string>> OfferedAsync(IDockerClient client, CancellationToken ct)
    {
        if (_offered is { } known)
            return known;
        await Gate.WaitAsync(ct);
        try
        {
            if (_offered is { } raced)
                return raced;
            var info = await client.System.GetSystemInfoAsync(ct);
            return _offered = info?.Runtimes?.Keys.ToList() ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return []; // unanswerable probe: no sandbox known — RequireSandboxRuntime then refuses
        }
        finally
        {
            Gate.Release();
        }
    }
}
