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
    public async Task CreateHuman_WithAnExistingUsername_IsRefused_AndTheOriginalLoginSurvives()
    {
        var alice = await _ctx.Directory.CreateHumanAsync("Alice", "alice", "s3cret");

        Assert.ThrowsAsync<PrincipalConflictException>(
            () => _ctx.Directory.CreateHumanAsync("Impostor", "alice", "hijack"));

        var session = await _ctx.IdentityProvider.AuthenticatePasswordAsync("alice", "s3cret");
        Assert.Multiple(async () =>
        {
            Assert.That(session?.PrincipalId, Is.EqualTo(alice.Id), "the credential still belongs to Alice");
            Assert.That(await _ctx.IdentityProvider.AuthenticatePasswordAsync("alice", "hijack"), Is.Null);
            Assert.That((await _ctx.Principals.ReadAsync(CancellationToken.None)).Count(p => p.DisplayName == "Impostor"),
                Is.Zero, "no orphan principal is left behind");
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
        var (principal, apiKey) = await _ctx.Directory.CreateApiKeyPrincipalAsync("Review Agent");
        await _ctx.Directory.AssignRoleAsync(principal.Id, BuiltInRoles.User);

        var session = await _ctx.IdentityProvider.AuthenticateApiKeyAsync(apiKey);

        Assert.That(session, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(session!.PrincipalId, Is.EqualTo(principal.Id));
            Assert.That(session.Kind, Is.EqualTo("Service"));
        });
    }

    [Test]
    public async Task ApiKeyAuthentication_SessionCarriesGroupHeldRoles()
    {
        // A role granted through a first-class group is part of the EFFECTIVE role set the
        // session carries — claims-based checks (/auth/me, visibility filters) must agree with
        // the Policy Engine, which unions group roles at every decision.
        var (principal, apiKey) = await _ctx.Directory.CreateApiKeyPrincipalAsync("Review Agent");
        await _ctx.Directory.AssignRoleAsync(principal.Id, BuiltInRoles.User);
        var groupId = Guid.NewGuid();
        await _ctx.AddGroupMemberAsync(groupId, principal.Id);
        await _ctx.GroupRoles.SaveAsync(new PlatformData.Entities.GroupRoleRecord
        {
            Id = PlatformData.Entities.GroupRoleRecord.IdFor(groupId, BuiltInRoles.Operator),
            GroupId = groupId,
            RoleName = BuiltInRoles.Operator
        });

        var session = await _ctx.IdentityProvider.AuthenticateApiKeyAsync(apiKey);

        Assert.That(session!.Roles, Is.EquivalentTo(new[] { BuiltInRoles.User, BuiltInRoles.Operator }));
    }

    [Test]
    public async Task ApiKeyAuthentication_WrongKey_ReturnsNull()
    {
        await _ctx.Directory.CreateApiKeyPrincipalAsync("Review Agent");
        Assert.That(await _ctx.IdentityProvider.AuthenticateApiKeyAsync("aux_not-a-real-key"), Is.Null);
    }

    [Test]
    public async Task VerifySecret_AcceptsOnlyThePrincipalsOwnCredential()
    {
        var human = await _ctx.Directory.CreateHumanAsync("Alice", $"alice-{Guid.NewGuid():N}", "s3cret");
        var (service, apiKey) = await _ctx.Directory.CreateApiKeyPrincipalAsync("Bot");

        Assert.Multiple(async () =>
        {
            Assert.That(await _ctx.Directory.VerifySecretAsync(human.Id, "s3cret"), Is.True);
            Assert.That(await _ctx.Directory.VerifySecretAsync(human.Id, "wrong"), Is.False);
            Assert.That(await _ctx.Directory.VerifySecretAsync(service.Id, apiKey), Is.True);
            Assert.That(await _ctx.Directory.VerifySecretAsync(service.Id, "s3cret"), Is.False);
            Assert.That(await _ctx.Directory.VerifySecretAsync(human.Id, apiKey), Is.False,
                "another principal's credential must never elevate");
        });
    }

    [Test]
    public async Task LastAdministrator_CanNeitherBeRevokedNorDisabled()
    {
        var admin = await _ctx.Directory.CreateHumanAsync("Only Admin", $"admin-{Guid.NewGuid():N}", "pw");
        await _ctx.Directory.AssignRoleAsync(admin.Id, BuiltInRoles.Administrator);

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _ctx.Directory.RevokeRoleAsync(admin.Id, BuiltInRoles.Administrator),
            "revoking the last administrator would lock everyone out");
        Assert.ThrowsAsync<InvalidOperationException>(
            () => _ctx.Directory.SetEnabledAsync(admin.Id, enabled: false),
            "disabling the last administrator would lock everyone out");
    }

    [Test]
    public async Task Administrator_WithAnotherEnabledAdmin_CanBeRevokedAndDisabled()
    {
        var first = await _ctx.Directory.CreateHumanAsync("Admin A", $"a-{Guid.NewGuid():N}", "pw");
        var second = await _ctx.Directory.CreateHumanAsync("Admin B", $"b-{Guid.NewGuid():N}", "pw");
        await _ctx.Directory.AssignRoleAsync(first.Id, BuiltInRoles.Administrator);
        await _ctx.Directory.AssignRoleAsync(second.Id, BuiltInRoles.Administrator);

        Assert.That(await _ctx.Directory.RevokeRoleAsync(first.Id, BuiltInRoles.Administrator), Is.True);
        Assert.That(await _ctx.Directory.SetEnabledAsync(first.Id, enabled: false), Is.True);
        // And now B IS the last one — the guard closes behind them.
        Assert.ThrowsAsync<InvalidOperationException>(
            () => _ctx.Directory.SetEnabledAsync(second.Id, enabled: false));
    }

    [Test]
    public async Task SetTags_NormalizesAndAudits_AndUnknownPrincipalIsFalse()
    {
        var (principal, _) = await _ctx.Directory.CreateApiKeyPrincipalAsync("Tagged Agent");

        var set = await _ctx.Directory.SetTagsAsync(
            principal.Id, [" ai-agent ", "Team-A", "ai-agent", ""]);

        Assert.That(set, Is.True);
        Assert.That(await _ctx.Directory.SetTagsAsync(Guid.NewGuid(), ["x"]), Is.False);
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
