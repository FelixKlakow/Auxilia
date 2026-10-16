using Auxilia.Core.Contracts;
using Auxilia.Core.Runner.Workflows;
using Auxilia.Core.Runner.Workflows.Egress;
using Auxilia.Workflows;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Auxilia.Core.Runner.Tests.Workflows;

/// <summary>
/// Raw-TCP egress (tcp://host:port): plugin manifests declare templates over their own settings,
/// the runner expands them from the run's non-secret values, and the gateway relays each declared
/// endpoint under its own name — never a wildcard, never a secret, never a platform service.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class TcpEndpointTests
{
    private static readonly PluginManifest Mail = new("mail", "", "", "",
    [
        new SettingDescriptor("Host", "Host", SettingKind.Text),
        new SettingDescriptor("Port", "Port", SettingKind.Number, DefaultValue: "3025"),
        new SettingDescriptor("Password", "Password", SettingKind.Secret),
    ])
    {
        NetworkEndpoints = ["tcp://{Host}:{Port}", "tcp://{Password}:1", "static.example.com"]
    };

    private static Mock<ICoreCredentialClient> CoreServing(params IReadOnlyDictionary<string, string>[] bindings)
    {
        var core = new Mock<ICoreCredentialClient>();
        core.Setup(c => c.ResolveSettingValuesAsync(It.IsAny<Guid>(), "token", "mail",
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedSlotSettings(bindings));
        return core;
    }

    [Test]
    public async Task PluginTemplates_ExpandPerBinding_WithManifestDefaults()
    {
        var core = CoreServing(
            new Dictionary<string, string> { ["Host"] = "mail.example.com" },
            new Dictionary<string, string> { ["Host"] = "smtp.example.org", ["Port"] = "465" });
        var resolver = new NetworkPolicyResolver(NullLogger<NetworkPolicyResolver>.Instance, core.Object);

        var endpoints = await resolver.ResolvePluginEndpointsAsync(Mail, Guid.NewGuid(), "token", CancellationToken.None);

        Assert.That(endpoints, Is.EquivalentTo(new[]
        {
            "static.example.com", "tcp://mail.example.com:3025", "tcp://smtp.example.org:465"
        }));
    }

    [Test]
    public async Task ATemplateOverASecret_IsNeverResolved()
    {
        var core = CoreServing(new Dictionary<string, string> { ["Host"] = "mail.example.com" });
        var resolver = new NetworkPolicyResolver(NullLogger<NetworkPolicyResolver>.Instance, core.Object);

        await resolver.ResolvePluginEndpointsAsync(Mail, Guid.NewGuid(), "token", CancellationToken.None);

        core.Verify(c => c.ResolveSettingValuesAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
            It.Is<IReadOnlyList<string>>(keys => keys.Contains("Password")), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestCase("*")]
    [TestCase("evil.example.com:22")]
    [TestCase("a.example.com,b.example.com")]
    [TestCase("tcp://x")]
    [TestCase("")]
    public void ASettingValue_CanNeverWidenTheEntry(string value)
        => Assert.That(NetworkPolicyResolver.Expand("tcp://{Host}:25",
            new Dictionary<string, string> { ["Host"] = value }, null), Is.Null);

    private static EffectiveNetworkPolicy Restricted(params string[] endpoints)
        => new(NetworkPolicyMode.DefaultDeny, endpoints);

    private static readonly DockerWorkflowLauncherSettings Launcher = new() { RabbitMqHost = "rabbitmq", RabbitMqPort = 5672 };

    [Test]
    public void TcpEndpoints_BecomeNamedRelays_InEveryMode()
    {
        foreach (var policy in new[] { Restricted(), new EffectiveNetworkPolicy(NetworkPolicyMode.AllowAll, []) })
        {
            var plan = WorkflowDispatcher.BuildEgressPlan(Guid.NewGuid(), policy,
                ["tcp://Mail.Example.com:3025", "api.example.com"], null, Launcher, new WorkflowDispatcherSettings());

            Assert.That(plan.Forwards, Does.Contain("3025=mail.example.com:3025/tcp"));
            Assert.That(plan.TcpHosts, Is.EqualTo(new[] { "mail.example.com" }));
            Assert.That(plan.AllowList, Does.Not.Contain("tcp://"));
        }
    }

    [Test]
    public void TheBrokerEndpoint_IsProtected_WhileItsHostStaysUsable()
    {
        // The dev shape: broker and a git server behind the same host name.
        var launcher = new DockerWorkflowLauncherSettings
        {
            RabbitMqHost = "host.docker.internal", RabbitMqPort = 5672,
            ProtectedEndpoints = ["host.docker.internal:15672"]
        };
        var plan = WorkflowDispatcher.BuildEgressPlan(Guid.NewGuid(), Restricted(),
            ["host.docker.internal"], null, launcher, new WorkflowDispatcherSettings());

        Assert.That(plan.AllowList, Does.Contain("host.docker.internal"));
        Assert.That(plan.ProtectedEndpoints,
            Is.EquivalentTo(new[] { "host.docker.internal:5672", "host.docker.internal:15672" }));
        Assert.Throws<EgressPlanException>(() => WorkflowDispatcher.BuildEgressPlan(Guid.NewGuid(), Restricted(),
            ["tcp://host.docker.internal:15672"], null, launcher, new WorkflowDispatcherSettings()));
    }

    [TestCase("tcp://a.example.com:25", "tcp://b.example.com:25")]
    [TestCase("tcp://a.example.com:5672")]
    [TestCase("tcp://10.0.0.5:25")]
    public void UnrelayableTcpEndpoints_FailThePlan(params string[] endpoints)
        => Assert.Throws<EgressPlanException>(() => WorkflowDispatcher.BuildEgressPlan(
            Guid.NewGuid(), Restricted(), endpoints, null, Launcher, new WorkflowDispatcherSettings()));

    [Test]
    public void TheWorkload_ResolvesRelayedNames_ToItsGateway()
    {
        var plan = new EgressPlan(Guid.NewGuid(), "", [], null)
        {
            TcpHosts = ["mail.example.com"], GatewayAddress = "10.199.0.2"
        };
        var request = new WorkflowLaunchRequest("/x", new Dictionary<string, string>()) { Egress = plan };

        Assert.That(DockerWorkflowLauncher.WorkloadExtraHosts(request, new DockerWorkflowLauncherSettings()),
            Is.EqualTo(new[] { "mail.example.com:10.199.0.2" }));
        Assert.That(WorkflowDispatcher.EgressEnvironment(plan, null)
                .Single(kv => kv.Key == "NO_PROXY").Value, Does.Contain("mail.example.com"));
    }
}
