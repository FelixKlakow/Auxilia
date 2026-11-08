using System.Diagnostics;
using System.Runtime.InteropServices;
using Auxilia.Workflows.Sandbox;
using Auxilia.Workflows.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Workflows.Testing.DummyWorkflows;

/// <summary>
/// Reports what a run can actually do from inside its container, as findings in its persisted
/// <c>probe</c> view: the identity agent code runs as, which paths the SDK and the agent can
/// write, what the run reaches through its egress gateway and without it — and, on request
/// (<c>PROBE_MEMORY=exhaust</c>), it allocates until the kernel stops the container at its
/// memory ceiling. Context: <c>PROBE_TARGETS</c> = comma-separated URLs to fetch.
/// </summary>
public static class ContainmentProbeWorkflow
{
    public const string WorkflowName = "containment-probe-workflow";
    public const string ViewName = "probe";

    /// <summary>One observation: <see cref="Probe"/> names what was tried, <see cref="Outcome"/> what happened.</summary>
    public sealed record ProbeFinding(string Probe, string Outcome);

    public static Task RunAsync(string[] args) =>
        WorkflowBuilder
            .Create(WorkflowName)
            .DeclaresView<ProbeFinding>(ViewName, ViewRendering.Log, ViewLifecycle.LiveAndPersisted)
            .DeclaresTrigger(TriggerDeclaration.Manual, "Probe the run's containment.")
            .WithApplication(ExecuteAsync)
            .Run(args);

    private static async Task ExecuteAsync(IServiceProvider provider, CancellationToken ct)
    {
        var views = provider.GetRequiredService<IViewPublisher>();
        async Task Report(string probe, string outcome)
        {
            Console.WriteLine($"[ContainmentProbe] {probe}: {outcome}");
            await views.PublishAsync(ViewName, new ProbeFinding(probe, outcome), ct);
        }

        await Report("agent-uid", (await RunConfinedAsync("id -u", ct)).Output);
        foreach (var path in new[] { "/usr/local", "/etc", "/tmp", "/app" })
            await Report($"sdk-write:{path}", TryWrite(Path.Combine(path, $"auxilia-probe-{Guid.NewGuid():N}")));
        foreach (var path in new[] { "/usr/local", "/tmp", AgentSandbox.Home })
        {
            var (exit, _) = await RunConfinedAsync($"echo probe > {path}/auxilia-agent-probe-{Guid.NewGuid():N}", ct);
            await Report($"agent-write:{path}", exit == 0 ? "written" : "denied");
        }

        var targets = (System.Environment.GetEnvironmentVariable("WORKFLOW_CONTEXT__PROBE_TARGETS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var target in targets)
        {
            // Through the run's gateway (the platform's proxy variables), and around it.
            await Report($"egress:{target}", await FetchAsync(target, useProxy: true, ct));
            await Report($"direct:{target}", await FetchAsync(target, useProxy: false, ct));
        }

        if (System.Environment.GetEnvironmentVariable("WORKFLOW_CONTEXT__PROBE_MEMORY") == "exhaust")
        {
            await Report("memory", "allocating until stopped");
            ExhaustMemory();
        }
    }

    private static string TryWrite(string path)
    {
        try
        {
            File.WriteAllText(path, "probe");
            File.Delete(path);
            return "written";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "denied";
        }
    }

    /// <summary>A shell command as the agent identity — exactly how the SDK starts agent processes.</summary>
    private static async Task<(int Exit, string Output)> RunConfinedAsync(string command, CancellationToken ct)
    {
        var startInfo = AgentSandbox.Confine(new ProcessStartInfo("sh")
        {
            ArgumentList = { "-c", command },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });
        using var process = Process.Start(startInfo)!;
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, output.Trim());
    }

    private static async Task<string> FetchAsync(string url, bool useProxy, CancellationToken ct)
    {
        using var handler = new HttpClientHandler { UseProxy = useProxy };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using var response = await http.GetAsync(url, ct);
            return ((int)response.StatusCode).ToString();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return "unreachable";
        }
    }

    /// <summary>Native allocations the GC cannot refuse — only the kernel's memory ceiling stops this.</summary>
    private static void ExhaustMemory()
    {
        const int chunk = 32 * 1024 * 1024;
        var held = new List<IntPtr>();
        while (true)
        {
            var block = Marshal.AllocHGlobal(chunk);
            for (var offset = 0; offset < chunk; offset += 4096)
                Marshal.WriteByte(block, offset, 1);
            held.Add(block);
        }
    }
}
