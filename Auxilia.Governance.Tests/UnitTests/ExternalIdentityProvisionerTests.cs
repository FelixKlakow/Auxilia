using Auxilia.Governance.Identity;
using Auxilia.PlatformData.Entities;

namespace Auxilia.Governance.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class ExternalIdentityProvisionerTests
{
    private GovernanceTestContext _ctx = null!;

    [SetUp]
    public void SetUp() => _ctx = new GovernanceTestContext();

    private static ExternalIdentity Entra(string subject, string name = "Ada Lovelace", params string[] groups)
        => new("entra", subject, name, $"{subject}@contoso.com", groups);

    [Test]
    public async Task FirstSignIn_ProvisionsHumanPrincipal_WithExternalSubjectAndNoCredential()
    {
        var session = await _ctx.Provisioner.ProvisionAsync(Entra("sub-1"));

        Assert.That(session, Is.Not.Null);
        var principal = (await _ctx.Principals.ReadAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(session!.PrincipalId, Is.EqualTo(principal.Id));
            Assert.That(principal.Kind, Is.EqualTo("Human"));
            Assert.That(principal.DisplayName, Is.EqualTo("Ada Lovelace"));
            Assert.That(principal.ExternalSubject, Is.EqualTo("entra|sub-1"));
            Assert.That(principal.Status, Is.EqualTo("Active"));
        });
        // Federated principals never get a local credential.
        Assert.That(await _ctx.Credentials.ReadAsync(), Is.Empty);
    }

    [Test]
    public async Task RepeatSignIn_SameSubject_IsIdempotent_AndReusesPrincipal()
    {
        var first = await _ctx.Provisioner.ProvisionAsync(Entra("sub-1"));
        var second = await _ctx.Provisioner.ProvisionAsync(Entra("sub-1"));

        Assert.That(second!.PrincipalId, Is.EqualTo(first!.PrincipalId));
        Assert.That((await _ctx.Principals.ReadAsync()).Count(), Is.EqualTo(1),
            "Signing in again must not create a second principal.");
    }

    [Test]
    public async Task DifferentSubjects_ProvisionDistinctPrincipals()
    {
        var a = await _ctx.Provisioner.ProvisionAsync(Entra("sub-a"));
        var b = await _ctx.Provisioner.ProvisionAsync(Entra("sub-b"));

        Assert.That(a!.PrincipalId, Is.Not.EqualTo(b!.PrincipalId));
        Assert.That((await _ctx.Principals.ReadAsync()).Count(), Is.EqualTo(2));
    }

    [Test]
    public async Task Session_CarriesDirectRoleAssignments()
    {
        var session = await _ctx.Provisioner.ProvisionAsync(Entra("sub-1"));
        await _ctx.Directory.AssignRoleAsync(session!.PrincipalId, BuiltInRoles.User);

        var reSignIn = await _ctx.Provisioner.ProvisionAsync(Entra("sub-1"));

        Assert.That(reSignIn!.Roles, Is.EquivalentTo(new[] { BuiltInRoles.User }));
    }

    [Test]
    public async Task DisplayName_IsRefreshedFromTheDirectory_OnSubsequentSignIn()
    {
        var first = await _ctx.Provisioner.ProvisionAsync(Entra("sub-1", "Ada L."));
        var renamed = await _ctx.Provisioner.ProvisionAsync(Entra("sub-1", "Ada Byron"));

        Assert.That(renamed!.DisplayName, Is.EqualTo("Ada Byron"));
        var principal = await _ctx.Principals.ReadAsync(first!.PrincipalId);
        Assert.That(principal!.DisplayName, Is.EqualTo("Ada Byron"));
    }

    [Test]
    public async Task DisabledPrincipal_CannotSignInAgain()
    {
        var session = await _ctx.Provisioner.ProvisionAsync(Entra("sub-1"));
        await _ctx.Directory.DisableAsync(session!.PrincipalId);

        var denied = await _ctx.Provisioner.ProvisionAsync(Entra("sub-1"));

        Assert.That(denied, Is.Null, "A disabled account must not be silently re-enabled by signing in.");
    }

    [Test]
    public async Task Provisioning_IsAudited()
    {
        await _ctx.Provisioner.ProvisionAsync(Entra("sub-1"));

        Assert.Multiple(async () =>
        {
            Assert.That(await _ctx.AuditCountAsync("principal.provisioned"), Is.EqualTo(1));
            Assert.That(await _ctx.AuditCountAsync("principal.signed-in"), Is.EqualTo(1));
        });
    }
}
