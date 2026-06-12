using Microsoft.Extensions.Logging.Abstractions;

namespace Auxilia.Governance.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class GovernanceSeederTests
{
    private GovernanceTestContext _ctx = null!;

    [SetUp]
    public void SetUp() => _ctx = new GovernanceTestContext();

    private GovernanceSeeder MakeSeeder(string? username, string? password) => new(
        _ctx.Directory,
        new GovernanceSettings { BootstrapAdminUsername = username, BootstrapAdminPassword = password },
        NullLogger<GovernanceSeeder>.Instance);

    [Test]
    public async Task FirstStart_CreatesBootstrapAdministrator_ThatCanAuthenticate()
    {
        await MakeSeeder("admin", "initial-pw").SeedAsync();

        Assert.That(await _ctx.Directory.AnyAdministratorExistsAsync(), Is.True);
        var session = await _ctx.IdentityProvider.AuthenticatePasswordAsync("admin", "initial-pw");
        Assert.That(session, Is.Not.Null);
        Assert.That(session!.Roles, Does.Contain(BuiltInRoles.Administrator));
    }

    [Test]
    public async Task SecondStart_DoesNotCreateASecondAdministrator()
    {
        await MakeSeeder("admin", "initial-pw").SeedAsync();
        await MakeSeeder("admin2", "other-pw").SeedAsync();

        Assert.That(await _ctx.IdentityProvider.AuthenticatePasswordAsync("admin2", "other-pw"), Is.Null);
    }

    [Test]
    public async Task WithoutBootstrapConfig_SeedsNothing()
    {
        await MakeSeeder(null, null).SeedAsync();
        Assert.That(await _ctx.Directory.AnyAdministratorExistsAsync(), Is.False);
    }
}
