using System.IO.Compression;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Auxilia.Workflows;
using Auxilia.Workflows.Companions;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Workflows.Testing.DummyWorkflows;

/// <summary>
/// The run-pod worked example (test-fabric design §A) as a driven test case: the workflow
/// manages a simulated distributed system — a pod rabbit, two central pieces (fleet-manager
/// over RabbitMQ, coordinator over TCP), and declared machines — then GROWS the fleet at
/// runtime via <see cref="IPodController"/>, asserts the availability handshake after every
/// change, and ships the pod's logs (zipped) plus a verdict report as its artifacts.
/// The COMPANION topology lives in the registered schema (the test hand-builds it with the
/// real image digests); this binary declares only what must ride its own manifest: the
/// pod-control envelope and the outputs.
/// </summary>
public static class PodScenarioWorkflow
{
    public const string WorkflowName = "pod-scenario-workflow";

    public static Task RunAsync(string[] args) =>
        WorkflowBuilder.Create(WorkflowName)
            // Envelope = exactly the two runtime spawns below: proves (on the real daemon)
            // that declared companions never consume the runtime envelope.
            .RequiresPodControl(2, "Simulated machines spawned by the test case", "logs")
            .DeclaresOutput("logs-zip", "logs.zip", "Every pod service's log file, zipped")
            .DeclaresOutput("scenario-report", "report.json", "Observed availability counts")
            .WithApplication(ExecuteAsync)
            .Run(args);

    private static async Task ExecuteAsync(IServiceProvider provider, CancellationToken ct)
    {
        var declaredCount = int.Parse(
            System.Environment.GetEnvironmentVariable("Workflow__Companion__MACHINE__COUNT")
            ?? throw new InvalidOperationException("no machine-count announcement"));
        var pod = provider.GetService<IPodController>()
                  ?? throw new InvalidOperationException("no IPodController — pod control was not delivered");

        // Phase 1: the declared fleet must become fully available (manager pings machines,
        // publishes over the pod rabbit, coordinator serves the list over TCP).
        await WaitForAvailabilityAsync(declaredCount, ct);

        // Phase 2: the test case grows the fleet at runtime — two spawned machines, the
        // manager is told about them, availability must follow.
        foreach (var name in new[] { "machine-r1", "machine-r2" })
        {
            var spawned = await pod.SpawnAsync(new CompanionSpec(name, "sim-machine")
            {
                EnvironmentVariables = new Dictionary<string, string> { ["ROLE"] = "machine" },
                Readiness = new CompanionReadinessProbe(CompanionReadinessProbe.Tcp, 9000),
                VolumeMounts = new Dictionary<string, string> { ["logs"] = "/var/log/app" }
            }, ct);
            await ControlAsync($"ADD {spawned.Endpoint}", ct);
        }
        await WaitForAvailabilityAsync(declaredCount + 2, ct);

        // Phase 3: shrink — stop one spawned machine, remove it from the manager.
        await pod.StopAsync("machine-r1", ct);
        await ControlAsync("REMOVE machine-r1:9000", ct);
        await WaitForAvailabilityAsync(declaredCount + 1, ct);

        // Phase 4: isolation probes THROUGH a pod-internal container (the coordinator).
        // A pod peer must be reachable (prober sanity), the PLATFORM bus and the internet
        // must not — the --internal pod network is the guarantee under test.
        var platformBus = $"{System.Environment.GetEnvironmentVariable("RabbitMq__Host")}"
                          + $":{System.Environment.GetEnvironmentVariable("RabbitMq__Port") ?? "5672"}";
        var egress = new
        {
            podPeer = await SendLineAsync("coordinator", 7000, "PROBE fleet-manager:8080", ct),
            platformBus = await SendLineAsync("coordinator", 7000, $"PROBE {platformBus}", ct),
            internet = await SendLineAsync("coordinator", 7000, "PROBE 1.1.1.1:443", ct)
        };

        // Verdict artifacts: the pod's logs from the shared volume, zipped, plus the report.
        var outputDir = System.Environment.GetEnvironmentVariable(WorkflowEnvironmentVariables.OutputDirectory)
                        ?? throw new InvalidOperationException("no output directory");
        Directory.CreateDirectory(outputDir);
        ZipFile.CreateFromDirectory($"{WorkflowEnvironmentVariables.PodVolumeRoot}/logs",
            Path.Combine(outputDir, "logs.zip"));
        await File.WriteAllTextAsync(Path.Combine(outputDir, "report.json"),
            JsonSerializer.Serialize(new
            {
                declared = declaredCount,
                afterSpawn = declaredCount + 2,
                afterStop = declaredCount + 1,
                egress
            }), ct);
    }

    private static async Task WaitForAvailabilityAsync(int expected, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(150);
        string last = "<none>";
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                last = await SendLineAsync("coordinator", 7000, "LIST", ct);
                if (int.Parse(last.Split('|')[0]) == expected)
                    return;
            }
            catch
            {
                // coordinator not up yet — keep waiting
            }
            await Task.Delay(1000, ct);
        }
        throw new InvalidOperationException(
            $"availability never reached {expected} (last answer: {last})");
    }

    private static async Task ControlAsync(string command, CancellationToken ct)
    {
        var reply = await SendLineAsync("fleet-manager", 8080, command, ct);
        if (reply != "OK")
            throw new InvalidOperationException($"fleet-manager refused '{command}': {reply}");
    }

    private static async Task<string> SendLineAsync(string host, int port, string line, CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, ct);
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        await using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
        await writer.WriteLineAsync(line);
        return await reader.ReadLineAsync(ct) ?? "";
    }
}
