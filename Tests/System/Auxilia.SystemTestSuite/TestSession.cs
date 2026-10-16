using System.Diagnostics;

namespace Auxilia.SystemTestSuite;

/// <summary>
/// Labels everything the test runners create (workflow containers, egress gateways, run networks,
/// pod objects) with this test process's session id, so an environment's teardown removes exactly
/// those — a test that passes before its run's containment is released leaves nothing behind on
/// the shared Docker host, and nothing outside this session is ever touched.
/// </summary>
internal static class TestSession
{
    public const string LabelKey = "auxilia.test-session";

    public static readonly string Id = Guid.NewGuid().ToString("N");

    /// <summary>The runner setting that stamps the session label (<c>WorkflowLauncher:ContainerLabels</c>).</summary>
    public static string RunnerVariable => $"WorkflowLauncher__ContainerLabels__{LabelKey}";

    public static async Task SweepAsync()
    {
        var filter = $"label={LabelKey}={Id}";
        // Containers first: a run network cannot go while its gateway is still attached.
        foreach (var (list, remove) in new[]
                 {
                     ($"ps -aq --filter {filter}", "rm -f"),
                     ($"network ls -q --filter {filter}", "network rm"),
                     ($"volume ls -q --filter {filter}", "volume rm -f"),
                 })
        {
            var ids = (await DockerAsync(list)).Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (ids.Length > 0)
                await DockerAsync($"{remove} {string.Join(' ', ids)}");
        }
    }

    private static async Task<string> DockerAsync(string arguments)
    {
        var psi = new ProcessStartInfo("docker", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start docker.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return await stdout;
    }
}
