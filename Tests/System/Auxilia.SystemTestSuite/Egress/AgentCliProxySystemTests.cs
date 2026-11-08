using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace Auxilia.SystemTestSuite.Egress;

/// <summary>
/// The agent CLIs the shipped workflow images bundle honour the proxy variables the runner
/// injects (<c>HTTPS_PROXY</c>/<c>HTTP_PROXY</c>/<c>NO_PROXY</c>, <c>NODE_USE_ENV_PROXY</c>): on a
/// run network whose only way out is the egress gateway (empty allowlist), each CLI's first call
/// to its service shows up in the gateway's log as a refused CONNECT. A CLI ignoring the
/// variables would not reach the gateway at all — external names do not even resolve there.
/// No real AI call is possible: the gateway refuses everything.
/// </summary>
[TestFixture]
[Category("System")]
public sealed class AgentCliProxySystemTests
{
    private const string ClaudeImage = EndToEnd.EndToEndEnvironment.ClaudeWorkflowImageName;
    private const string CopilotImage = EndToEnd.EndToEndEnvironment.ImplementationWorkflowImageName;

    private INetwork _run = null!;
    private IContainer _gateway = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await TestImages.BuildEgressGatewayAsync();
        await TestImages.BuildImageAsync(ClaudeImage, "Source/Workflows/Auxilia.ClaudeCode.Workflow/Dockerfile");
        await TestImages.BuildImageAsync(CopilotImage, "Source/Workflows/Auxilia.Implementation.Workflow/Dockerfile");

        _run = new NetworkBuilder().WithName($"auxilia-cliproxy-{Guid.NewGuid():N}"[..30])
            .WithCreateParameterModifier(p => p.Internal = true).Build();
        await _run.CreateAsync();
        _gateway = new ContainerBuilder(TestImages.EgressGatewayImageName)
            .WithNetwork(_run).WithNetworkAliases("egress")
            .WithEnvironment("EGRESS_ALLOW", "")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("\"event\":\"listening\""))
            .Build();
        await _gateway.StartAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_gateway is not null) await _gateway.DisposeAsync();
        if (_run is not null) await _run.DisposeAsync();
    }

    [Test]
    [CancelAfter(180_000)]
    public Task ClaudeCode_ReachesItsServiceOnlyThroughTheGateway(CancellationToken ct)
        => AssertDialsThroughTheGatewayAsync(ClaudeImage, "claude", ["-p", "ping"],
            new() { ["ANTHROPIC_API_KEY"] = "sk-ant-proxy-probe", ["DISABLE_AUTOUPDATER"] = "1" },
            "api.anthropic.com", ct);

    [Test]
    [CancelAfter(180_000)]
    public Task CopilotCli_ReachesItsServiceOnlyThroughTheGateway(CancellationToken ct)
        => AssertDialsThroughTheGatewayAsync(CopilotImage, "copilot", ["-p", "ping", "--allow-all-tools"],
            new() { ["COPILOT_GITHUB_TOKEN"] = "gho_0123456789abcdefghijABCDEFGHIJ012345" },
            "api.github.com", ct);

    private async Task AssertDialsThroughTheGatewayAsync(
        string image, string cli, string[] arguments, Dictionary<string, string> environment,
        string expectedHost, CancellationToken ct)
    {
        var builder = new ContainerBuilder(image)
            .WithNetwork(_run)
            .WithEntrypoint(cli)
            .WithCommand(arguments)
            .WithWaitStrategy(Wait.ForUnixContainer());
        // Exactly the proxy view the runner gives every workflow container.
        foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy" })
            builder = builder.WithEnvironment(name, "http://egress:3128");
        builder = builder
            .WithEnvironment("NO_PROXY", "localhost,127.0.0.1,::1,egress,workflow")
            .WithEnvironment("no_proxy", "localhost,127.0.0.1,::1,egress,workflow")
            .WithEnvironment("NODE_USE_ENV_PROXY", "1");
        foreach (var (name, value) in environment)
            builder = builder.WithEnvironment(name, value);

        await using var agent = builder.Build();
        await agent.StartAsync(ct);

        var marker = $"\"host\":\"{expectedHost}\"";
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(90);
        string log;
        do
        {
            (log, _) = await _gateway.GetLogsAsync(ct: ct);
            if (log.Contains(marker, StringComparison.Ordinal))
                break;
            await Task.Delay(1000, ct);
        } while (DateTimeOffset.UtcNow < deadline);

        var (agentOut, agentErr) = await agent.GetLogsAsync(ct: ct);
        Assert.That(log.Split('\n').Any(l => l.Contains(marker) && l.Contains("\"decision\":\"deny\"")), Is.True,
            $"{cli} must dial {expectedHost} through the run's gateway (which refuses it here).\n"
            + $"gateway log:\n{log}\n{cli} output:\n{agentOut}\n{agentErr}");
    }
}
