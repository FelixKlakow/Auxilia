using Auxilia.Governance.Identity;

namespace Auxilia.Governance.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class LocalIdentityProviderTests
{
    private GovernanceTestContext _ctx = null!;

    [SetUp]
    public void SetUp() => _ctx = new GovernanceTestContext();

    [Test]
    public async Task PasswordAuthentication_WithCorrectPassword_ReturnsSessionWithRoles()
    {
        var principal = await _ctx.Directory.CreateHumanAsync("Alice", "alice", "s3cret");
        await _ctx.Directory.AssignRoleAsync(principal.Id, BuiltInRoles.Operator);

        var session = await _ctx.IdentityProvider.AuthenticatePasswordAsync("alice", "s3cret");

        Assert.That(session, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(session!.PrincipalId, Is.EqualTo(principal.Id));
            Assert.That(session.Kind, Is.EqualTo("Human"));
            Assert.That(session.Roles, Is.EquivalentTo(new[] { BuiltInRoles.Operator }));
        });
    }

    [Test]
    public async Task PasswordAuthentication_WithWrongPassword_ReturnsNull()
    {
        await _ctx.Directory.CreateHumanAsync("Alice", "alice", "s3cret");
        Assert.That(await _ctx.IdentityProvider.AuthenticatePasswordAsync("alice", "wrong"), Is.Null);
    }

    [Test]
    public async Task PasswordAuthentication_UnknownUser_ReturnsNull()
    {
        Assert.That(await _ctx.IdentityProvider.AuthenticatePasswordAsync("nobody", "pw"), Is.Null);
    }

    [Test]
    public async Task PasswordAuthentication_DisabledPrincipal_ReturnsNull()
    {
        var principal = await _ctx.Directory.CreateHumanAsync("Alice", "alice", "s3cret");
        await _ctx.Directory.DisableAsync(principal.Id);

        Assert.That(await _ctx.IdentityProvider.AuthenticatePasswordAsync("alice", "s3cret"), Is.Null);
    }

    [Test]
    public async Task ApiKeyAuthentication_RoundTripsForAiPrincipal()
    {
        var (principal, apiKey) = await _ctx.Directory.CreateApiKeyPrincipalAsync("Review Agent", "AiAgent");
        await _ctx.Directory.AssignRoleAsync(principal.Id, BuiltInRoles.User);

        var session = await _ctx.IdentityProvider.AuthenticateApiKeyAsync(apiKey);

        Assert.That(session, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(session!.PrincipalId, Is.EqualTo(principal.Id));
            Assert.That(session.Kind, Is.EqualTo("AiAgent"));
        });
    }

    [Test]
    public async Task ApiKeyAuthentication_WrongKey_ReturnsNull()
    {
        await _ctx.Directory.CreateApiKeyPrincipalAsync("Review Agent", "AiAgent");
        Assert.That(await _ctx.IdentityProvider.AuthenticateApiKeyAsync("aux_not-a-real-key"), Is.Null);
    }

    [Test]
    public void CreateApiKeyPrincipal_HumanKind_Throws()
    {
        Assert.ThrowsAsync<ArgumentException>(
            () => _ctx.Directory.CreateApiKeyPrincipalAsync("Person", "Human"));
    }

    [Test]
    public void PasswordHasher_RoundTrip_And_RejectsWrongOrGarbage()
    {
        var envelope = PasswordHasher.Hash("correct horse");
        Assert.Multiple(() =>
        {
            Assert.That(PasswordHasher.Verify("correct horse", envelope), Is.True);
            Assert.That(PasswordHasher.Verify("wrong", envelope), Is.False);
            Assert.That(PasswordHasher.Verify("anything", "not-an-envelope"), Is.False);
            Assert.That(envelope, Does.Not.Contain("correct horse"));
        });
    }
}
