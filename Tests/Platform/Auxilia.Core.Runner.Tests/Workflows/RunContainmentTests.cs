using Auxilia.Core.Runner.Workflows;
using Auxilia.Core.Runner.Workflows.Egress;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess.Implementations;

namespace Auxilia.Core.Runner.Tests.Workflows;

/// <summary>
/// A run's containment beyond its container: the egress plan is pure data from the run's
/// policy, its own workspace hosts and the platform's endpoints; every denied egress attempt is
/// audited the moment it happens; enough of them trip the kill switch; release tears the
/// topology down and keeps its log as evidence.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class RunContainmentTests
{
    private static readonly Guid Run = Guid.NewGuid();

    [Test]
    public void EgressPlan_JoinsPolicyEndpoints_WorkspaceHosts_AndPlatformEndpoints()
    {
        var plan = WorkflowDispatcher.BuildEgressPlan(
            Run,
            new EffectiveNetworkPolicy(NetworkPolicyMode.DefaultDeny, ["api.anthropic.com"]),
            ["github.com", "blocked.example"],
            terminalPort: 7681,
            new DockerWorkflowLauncherSettings { RabbitMqHost = "rabbitmq", RabbitMqPort = 5672 },
            new WorkflowDispatcherSettings
            {
                PlatformEgressEndpoints = ["core-api:8080"],
                BlockedEndpoints = ["blocked.example"]
            });

        Assert.That(plan.AllowList.Split(','), Is.EquivalentTo(new[] { "api.anthropic.com", "github.com", "core-api:8080" }));
        Assert.That(plan.Forwards, Is.EqualTo(new[] { "5672=rabbitmq:5672/amqp", "7681=workflow:7681/terminal" }));
    }

    [Test]
    public void EgressPlan_AllowAll_IsTheWildcard_StillThroughTheGateway()
        => Assert.That(WorkflowDispatcher.BuildEgressPlan(
                Run, new EffectiveNetworkPolicy(NetworkPolicyMode.AllowAll, []), [], null,
                new DockerWorkflowLauncherSettings(), new WorkflowDispatcherSettings()).AllowList,
            Is.EqualTo("*"), "allow-all is still proxied, logged and audited");

    [Test]
    public void EgressPlan_ZeroEndpoints_AllowsNothing()
        => Assert.That(WorkflowDispatcher.BuildEgressPlan(
                Run, new EffectiveNetworkPolicy(NetworkPolicyMode.DefaultDeny, []), [], null,
                new DockerWorkflowLauncherSettings(), new WorkflowDispatcherSettings()).AllowList,
            Is.Empty);

    [TestCase("https://github.com/org/repo.git", "github.com")]
    [TestCase("ssh://git@git.corp:2222/repo.git", "git.corp")]
    [TestCase("git@github.com:org/repo.git", "github.com")]
    [TestCase("/var/repos/local", null)]
    public void WorkspaceHosts_AreDerivedFromCloneSources(string source, string? host)
        => Assert.That(WorkflowDispatcher.EgressHostOf(source), Is.EqualTo(host));

    [Test]
    public void TheWorkloadSeesTheBrokerAndTheWorldOnlyThroughItsGateway()
    {
        var env = WorkflowDispatcher.EgressEnvironment(
            new EgressPlan(Run, "", [], null), pod: null).ToDictionary();

        Assert.That(env["RabbitMq__Host"], Is.EqualTo("egress"));
        Assert.That(env["HTTPS_PROXY"], Is.EqualTo("http://egress:3128"));
        Assert.That(env["NO_PROXY"], Does.Contain("localhost").And.Contain("127.0.0.1"));
    }

    [Test]
    public async Task EveryDenial_IsAudited_AndAllowedDestinationsOnce()
    {
        var records = new InMemoryDataAccess<AuditRecord>();
        var containment = TestStores.NewContainment(auditLog: new AuditLog(records, TimeProvider.System));

        await containment.OnEgressEventAsync(Run, new EgressEvent("connect", "connect", "exfil.example", 443, "deny", "x"));
        await containment.OnEgressEventAsync(Run, new EgressEvent("connect", "connect", "exfil.example", 443, "deny", "x"));
        await containment.OnEgressEventAsync(Run, new EgressEvent("connect", "connect", "api.nuget.org", 443, "allow", null));
        await containment.OnEgressEventAsync(Run, new EgressEvent("connect", "connect", "api.nuget.org", 443, "allow", null));

        var actions = (await records.ReadAsync()).Select(r => r.Action).ToList();
        Assert.That(actions.Count(a => a == "workflow.egress.denied"), Is.EqualTo(2));
        Assert.That(actions.Count(a => a == "workflow.egress.allowed"), Is.EqualTo(1));
    }

    [Test]
    public async Task TheDenialLimit_TripsTheKillSwitch_Once()
    {
        var containment = TestStores.NewContainment(settings: new WorkflowDispatcherSettings { EgressDenialLimit = 2 });
        var tripped = new List<(Guid, int)>();
        containment.EgressDenialLimitExceeded += (id, n) => { tripped.Add((id, n)); return Task.CompletedTask; };

        for (var i = 0; i < 4; i++)
            await containment.OnEgressEventAsync(Run, new EgressEvent("connect", "connect", "x.example", 443, "deny", null));

        Assert.That(tripped, Is.EqualTo(new[] { (Run, 2) }));
    }

    [Test]
    public async Task Release_OnceTheContainerIsGone_TearsTheEgressDown_AndKeepsItsLog()
    {
        var egress = new FakeEgressHost { TeardownLog = "{\"event\":\"connect\"}" };
        var issuer = new FakeBrokerIdentityIssuer();
        await issuer.IssueAsync(Auxilia.Messaging.WorkloadBusIdentity.UserNameFor(Run), null!);
        var containment = TestStores.NewContainment(issuer, egress);

        await containment.ReleaseAsync(Run, containerGone: false);
        Assert.That(issuer.Active, Is.Empty, "the broker identity dies first, on every path");
        Assert.That(egress.TornDown, Is.Empty, "the egress stays while the container may still be exiting");

        await containment.ReleaseAsync(Run, containerGone: true);
        Assert.That(egress.TornDown, Does.Contain(Run));
    }
}
