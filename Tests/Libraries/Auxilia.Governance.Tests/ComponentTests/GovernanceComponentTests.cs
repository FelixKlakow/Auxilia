using Auxilia.Core.Contracts;
using Auxilia.Governance.Identity;
using Auxilia.Governance.Policy;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Auxilia.Governance.Tests.ComponentTests;

/// <summary>
/// Full DI wiring via AddGovernance: bootstrap seeding, authentication, authorization,
/// and audit-trail completeness through the real container.
/// </summary>
[TestFixture]
[Category("Component")]
public class GovernanceComponentTests
{
    private IHost _host = null!;

    [SetUp]
    public async Task SetUp()
    {
        var dataSettings = new PlatformDataSettings { Backend = PlatformDataBackend.InMemory };
        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddSingleton(TimeProvider.System);
                services.AddPlatformEntity<AuditRecord>(dataSettings);
                services.AddSettingsProtection(dataSettings);
                services.AddSingleton<AuditLog>();
                services.AddGovernance(dataSettings, new GovernanceSettings
                {
                    BootstrapAdminUsername = "admin",
                    BootstrapAdminPassword = "bootstrap-pw"
                });
            })
            .Build();

        await _host.Services.GetRequiredService<GovernanceSeeder>().SeedAsync();
    }

    [TearDown]
    public void TearDown() => _host.Dispose();

    [Test]
    public async Task BootstrapAdmin_CanAuthenticate_AndAdministerPolicy()
    {
        var identity = _host.Services.GetRequiredService<IIdentityProvider>();
        var policy = _host.Services.GetRequiredService<IPolicyEngine>();

        var session = await identity.AuthenticatePasswordAsync("admin", "bootstrap-pw");
        Assert.That(session, Is.Not.Null);

        var decision = await policy.EvaluateAsync(
            new PolicyContext(session!.PrincipalId, PermissionActions.PolicyAdminister, "platform"));
        Assert.That(decision.Allowed, Is.True);
    }

    [Test]
    public async Task AiPrincipal_AuthenticatesViaApiKey_AndPassesSamePolicyChecksAsHumans()
    {
        var directory = _host.Services.GetRequiredService<PrincipalDirectory>();
        var identity = _host.Services.GetRequiredService<IIdentityProvider>();
        var policy = _host.Services.GetRequiredService<IPolicyEngine>();

        var (ai, apiKey) = await directory.CreateApiKeyPrincipalAsync("Review Agent", "AiAgent");
        await directory.AssignRoleAsync(ai.Id, BuiltInRoles.User);

        var session = await identity.AuthenticateApiKeyAsync(apiKey);
        Assert.That(session, Is.Not.Null);

        var allowed = await policy.EvaluateAsync(
            new PolicyContext(session!.PrincipalId, PermissionActions.WorkflowTrigger, "wf-1"));
        var denied = await policy.EvaluateAsync(
            new PolicyContext(session.PrincipalId, PermissionActions.SlotConfigWrite, "wf-1"));

        Assert.Multiple(() =>
        {
            Assert.That(allowed.Allowed, Is.True, "AI principal with User role may trigger.");
            Assert.That(denied.Allowed, Is.False, "AI principal without Operator role may not configure slots.");
        });
    }

    [Test]
    public async Task DeniedOperation_LeavesAnAuditRecord()
    {
        var directory = _host.Services.GetRequiredService<PrincipalDirectory>();
        var policy = _host.Services.GetRequiredService<IPolicyEngine>();
        var audit = _host.Services.GetRequiredService<IDataAccess<AuditRecord>>();

        var nobody = await directory.CreateHumanAsync("No Roles", "noroles", "pw");
        await policy.EvaluateAsync(new PolicyContext(nobody.Id, PermissionActions.AuditRead, "audit"));

        var query = await audit.ReadAsync();
        var denial = query.SingleOrDefault(r =>
            r.Action == "policy.denied" && r.Actor == nobody.Id.ToString());
        Assert.That(denial, Is.Not.Null);
        Assert.That(denial!.Subject, Is.EqualTo($"{PermissionActions.AuditRead}:audit"));
    }
}
