using System.Text.Json;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Auxilia.Workflows.Testing.DummyWorkflows;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Auxilia.SystemTestSuite.CoreClientSurface;

/// <summary>
/// A run's containment as the run itself experiences it — real Core, real runner, real Docker
/// daemon; the containment-probe workflow reports from inside its container what it could do:
/// <list type="bullet">
/// <item>egress: its declared endpoint is reachable through its gateway, every other host is
/// refused AND audited as blocked, nothing is reachable around the gateway, and a run without
/// endpoints reaches nothing; the per-attempt egress log is anchored in the audit chain;</item>
/// <item>the container: agent code runs as the unprivileged agent user, the image is read-only
/// (writable only through binds, scratch and the agent's home), and the kernel stops the
/// container at its memory ceiling — the run fails saying so.</item>
/// </list>
/// The two web servers sit on the operator network, where only run gateways are attached.
/// </summary>
[TestFixture]
[Category("System")]
public sealed class RunContainmentSystemTests
{
    private const string TargetImage = "nginx:1.27-alpine";
    private const string AllowedTarget = "probe-allowed";
    private const string DeniedTarget = "probe-denied";

    private static ICoreClient Admin => _admin ??= CoreClientEnvironment.CreateClient();
    private static ICoreClient? _admin;

    private IContainer _allowed = null!;
    private IContainer _denied = null!;

    [OneTimeSetUp]
    public async Task StartTargetsAsync()
    {
        _allowed = new ContainerBuilder(TargetImage)
            .WithNetwork(CoreClientEnvironment.Network).WithNetworkAliases(AllowedTarget).Build();
        _denied = new ContainerBuilder(TargetImage)
            .WithNetwork(CoreClientEnvironment.Network).WithNetworkAliases(DeniedTarget).Build();
        await Task.WhenAll(_allowed.StartAsync(), _denied.StartAsync());
    }

    [OneTimeTearDown]
    public async Task StopTargetsAsync()
    {
        if (_allowed is not null) await _allowed.DisposeAsync();
        if (_denied is not null) await _denied.DisposeAsync();
    }

    [Test]
    [CancelAfter(180_000)]
    public async Task ARun_ReachesOnlyItsDeclaredEndpoint_ThroughItsGateway_AndEveryRefusalIsAudited(CancellationToken ct)
    {
        var (runId, findings) = await ProbeAsync(ct,
            ("NetworkAllow", $"{AllowedTarget}:80"),
            ("PROBE_TARGETS", $"http://{AllowedTarget}/,http://{DeniedTarget}/"));

        Assert.Multiple(() =>
        {
            Assert.That(findings[$"egress:http://{AllowedTarget}/"], Is.EqualTo("200"), "the declared endpoint is reachable");
            Assert.That(findings[$"egress:http://{DeniedTarget}/"], Is.EqualTo("403"), "any other host is refused by the gateway");
            Assert.That(findings[$"direct:http://{AllowedTarget}/"], Is.EqualTo("unreachable"),
                "there is no route around the gateway — not even to the declared endpoint");
            Assert.That(findings[$"direct:http://{DeniedTarget}/"], Is.EqualTo("unreachable"));
        });

        // The refusal is in the central, chained audit trail (forwarded from the runner).
        var denials = await AuditAsync("workflow.egress.denied", runId, ct, expectAny: true);
        Assert.That(denials.Select(d => d.Outcome), Does.Contain($"{DeniedTarget}:80"),
            "the blocked attempt is audited with its destination");
        Assert.That(denials.Select(d => d.Outcome), Does.Not.Contain($"{AllowedTarget}:80"));
        var allowed = await AuditAsync("workflow.egress.allowed", runId, ct, expectAny: true);
        Assert.That(allowed.Select(a => a.Outcome), Does.Contain($"{AllowedTarget}:80"));

        // The summary anchors the complete per-attempt log in the chain: its digest is the
        // egress-log artifact's content hash.
        var summary = (await AuditAsync("workflow.egress.summary", runId, ct, expectAny: true)).Single();
        using var detail = JsonDocument.Parse(summary.DetailJson!);
        var anchored = detail.RootElement.GetProperty("egressLogSha256").GetString();
        // The log is persisted after the summary and mirrored to the Core over the bus.
        IReadOnlyList<ArtifactDto> egressLog = [];
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (egressLog.Count == 0 && DateTimeOffset.UtcNow < deadline)
        {
            egressLog = (await Admin.QueryArtifactsAsync(new ArtifactQuery(ArtifactType: "egress-log", RunId: runId), ct)).Items;
            if (egressLog.Count == 0)
                await Task.Delay(500, ct);
        }
        Assert.That(egressLog.Select(a => a.ContentHash), Does.Contain(anchored),
            "every allowed and denied attempt is recorded in a log the audit chain vouches for");
    }

    [Test]
    [CancelAfter(180_000)]
    public async Task ARunWithoutEndpoints_ReachesNothing(CancellationToken ct)
    {
        var (_, findings) = await ProbeAsync(ct, ("PROBE_TARGETS", $"http://{AllowedTarget}/,http://{DeniedTarget}/"));

        Assert.Multiple(() =>
        {
            Assert.That(findings[$"egress:http://{AllowedTarget}/"], Is.EqualTo("403"));
            Assert.That(findings[$"egress:http://{DeniedTarget}/"], Is.EqualTo("403"));
            Assert.That(findings[$"direct:http://{AllowedTarget}/"], Is.EqualTo("unreachable"));
        });
    }

    [Test]
    [CancelAfter(180_000)]
    public async Task AgentCode_RunsUnprivileged_OnAReadOnlyImage(CancellationToken ct)
    {
        var (_, findings) = await ProbeAsync(ct);

        Assert.Multiple(() =>
        {
            Assert.That(findings["agent-uid"], Is.EqualTo("10001"), "agent processes never run as root");
            Assert.That(findings["sdk-write:/usr/local"], Is.EqualTo("denied"), "the image is read-only — even for root");
            Assert.That(findings["sdk-write:/etc"], Is.EqualTo("denied"));
            Assert.That(findings["sdk-write:/tmp"], Is.EqualTo("written"), "scratch stays writable");
            Assert.That(findings["sdk-write:/app"], Is.EqualTo("written"), "the per-container application copy");
            Assert.That(findings["agent-write:/usr/local"], Is.EqualTo("denied"));
            Assert.That(findings["agent-write:/tmp"], Is.EqualTo("written"));
            Assert.That(findings["agent-write:/home/auxilia-agent"], Is.EqualTo("written"), "the agent's own home");
        });
    }

    [Test]
    [CancelAfter(180_000)]
    public async Task AContainer_IsStoppedAtItsMemoryLimit_AndTheRunSaysSo(CancellationToken ct)
    {
        var accepted = await Admin.RunAsync(new RunRequest(CoreClientEnvironment.ContainmentProbeWorkflowType,
            CoreClientEnvironment.ContextFor(ContainmentProbeWorkflow.WorkflowName, ("PROBE_MEMORY", "exhaust"))), ct);

        var (state, _) = await ClientStreamProbe.AwaitTerminalAsync(Admin, accepted.RunId, ct);
        var status = await Admin.GetRunAsync(accepted.RunId, ct);

        Assert.That(state, Is.EqualTo("Failed"));
        Assert.That(status!.Error, Does.Contain("stopped at its memory limit"),
            $"the kernel stops the container at {CoreClientEnvironment.ContainerMemoryMb} MiB — and the reason reaches the client");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Runs the probe to success and returns its instance id and findings (probe → outcome).</summary>
    private static async Task<(Guid RunId, Dictionary<string, string> Findings)> ProbeAsync(
        CancellationToken ct, params (string Key, string Value)[] context)
    {
        var accepted = await Admin.RunAsync(new RunRequest(CoreClientEnvironment.ContainmentProbeWorkflowType,
            CoreClientEnvironment.ContextFor(ContainmentProbeWorkflow.WorkflowName, context)), ct);
        var (state, _) = await ClientStreamProbe.AwaitTerminalAsync(Admin, accepted.RunId, ct);
        var status = await Admin.GetRunAsync(accepted.RunId, ct);
        Assert.That(state, Is.EqualTo("Success"), status?.Error);

        var findings = (await Admin.GetRunViewsAsync(accepted.RunId, ContainmentProbeWorkflow.ViewName, take: 500, ct: ct)).Items
            .Select(v => JsonSerializer.Deserialize<ContainmentProbeWorkflow.ProbeFinding>(
                v.PayloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!)
            .ToDictionary(f => f.Probe, f => f.Outcome);
        return (status!.RunId, findings);
    }

    /// <summary>Audit records of <paramref name="action"/> for a run, waiting for the runner's forwarding.</summary>
    private static async Task<IReadOnlyList<AuditEntry>> AuditAsync(
        string action, Guid runId, CancellationToken ct, bool expectAny)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var page = await Admin.QueryAuditAsync(new AuditQuery(Action: action, Subject: runId.ToString(), Take: 200), ct);
            if (!expectAny || page.Items.Count > 0 || DateTimeOffset.UtcNow > deadline)
                return page.Items;
            await Task.Delay(500, ct);
        }
    }
}
