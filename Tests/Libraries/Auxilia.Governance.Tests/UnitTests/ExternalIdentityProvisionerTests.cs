using System.Text.Json;
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

    private Task SeedMappingAsync(string groupClaim, string roleName, string identityProvider = "entra")
        => _ctx.GroupMappings.SaveAsync(new GroupMappingRecord
        {
            Id = GroupMappingRecord.IdFor(identityProvider, groupClaim, roleName),
            IdentityProvider = identityProvider,
            GroupClaim = groupClaim,
            RoleName = roleName
        });

    private async Task<string?> SourceOfAsync(Guid principalId, string roleName)
        => (await _ctx.RoleAssignments.ReadAsync())
            .SingleOrDefault(a => a.PrincipalId == principalId && a.RoleName == roleName)?.Source;

    [Test]
    public async Task SignIn_SessionCarriesGroupHeldRoles()
    {
        // Same effective-role rule as the local provider: a first-class-group role reaches the
        // session even though no direct assignment exists.
        var first = await _ctx.Provisioner.ProvisionAsync(Entra("sub-g"));
        var groupId = Guid.NewGuid();
        await _ctx.AddGroupMemberAsync(groupId, first!.PrincipalId);
        await _ctx.GroupRoles.SaveAsync(new GroupRoleRecord
        {
            Id = GroupRoleRecord.IdFor(groupId, BuiltInRoles.Operator),
            GroupId = groupId,
            RoleName = BuiltInRoles.Operator
        });

        var session = await _ctx.Provisioner.ProvisionAsync(Entra("sub-g"));

        Assert.That(session!.Roles, Is.EquivalentTo(new[] { BuiltInRoles.Operator }));
    }

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

    [Test]
    public async Task SignIn_MapsDirectoryGroupClaims_ToRoles()
    {
        await SeedMappingAsync("group-ops", BuiltInRoles.Operator);

        var session = await _ctx.Provisioner.ProvisionAsync(Entra("sub-1", groups: "group-ops"));

        Assert.That(session!.Roles, Does.Contain(BuiltInRoles.Operator));
        // The derived assignment is tagged as directory-sourced, not administered.
        Assert.That(await SourceOfAsync(session.PrincipalId, BuiltInRoles.Operator), Is.EqualTo("GroupMapping"));
    }

    [Test]
    public async Task SignIn_Reconciles_RevokesDirectoryRole_WhenGroupNoLongerPresent()
    {
        await SeedMappingAsync("group-ops", BuiltInRoles.Operator);
        var session = await _ctx.Provisioner.ProvisionAsync(Entra("sub-1", groups: "group-ops"));
        Assert.That(session!.Roles, Does.Contain(BuiltInRoles.Operator));

        // The user has left the group: the next sign-in carries no group claims.
        var reSignIn = await _ctx.Provisioner.ProvisionAsync(Entra("sub-1"));

        Assert.That(reSignIn!.Roles, Does.Not.Contain(BuiltInRoles.Operator));
        Assert.That(await SourceOfAsync(session.PrincipalId, BuiltInRoles.Operator), Is.Null,
            "A revoked directory role must leave no assignment behind.");
    }

    [Test]
    public async Task Reconciliation_NeverStripsDirectlyAssignedRoles()
    {
        var session = await _ctx.Provisioner.ProvisionAsync(Entra("sub-1"));
        await _ctx.Directory.AssignRoleAsync(session!.PrincipalId, BuiltInRoles.Auditor);

        // Sign in with a group claim that maps to nothing — reconciliation must not touch the direct grant.
        var reSignIn = await _ctx.Provisioner.ProvisionAsync(Entra("sub-1", groups: "unmapped-group"));

        Assert.That(reSignIn!.Roles, Does.Contain(BuiltInRoles.Auditor));
        Assert.That(await SourceOfAsync(session.PrincipalId, BuiltInRoles.Auditor), Is.EqualTo("Direct"));
    }

    [Test]
    public async Task DirectRole_IsNotDowngraded_NorRevoked_WhenAlsoMappedFromDirectory()
    {
        var session = await _ctx.Provisioner.ProvisionAsync(Entra("sub-1"));
        await _ctx.Directory.AssignRoleAsync(session!.PrincipalId, BuiltInRoles.Operator);
        await SeedMappingAsync("group-ops", BuiltInRoles.Operator);

        // Sign in while in the group: the pre-existing Direct grant stays Direct (not rewritten to GroupMapping).
        await _ctx.Provisioner.ProvisionAsync(Entra("sub-1", groups: "group-ops"));
        Assert.That(await SourceOfAsync(session.PrincipalId, BuiltInRoles.Operator), Is.EqualTo("Direct"));

        // Leaving the group must not revoke the role, because the grant is administered, not directory-derived.
        var afterLeaving = await _ctx.Provisioner.ProvisionAsync(Entra("sub-1"));
        Assert.That(afterLeaving!.Roles, Does.Contain(BuiltInRoles.Operator));
    }

    [Test]
    public async Task DirectoryRole_GrantAndRevoke_AreAudited()
    {
        await SeedMappingAsync("group-ops", BuiltInRoles.Operator);
        await _ctx.Provisioner.ProvisionAsync(Entra("sub-1", groups: "group-ops")); // grants
        await _ctx.Provisioner.ProvisionAsync(Entra("sub-1"));                       // revokes

        Assert.Multiple(async () =>
        {
            Assert.That(await _ctx.AuditCountAsync("role.assigned"), Is.EqualTo(1));
            Assert.That(await _ctx.AuditCountAsync("role.revoked"), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task SignIn_PersistsDirectoryGroupMemberships_OnThePrincipal()
    {
        var session = await _ctx.Provisioner.ProvisionAsync(Entra("sub-1", "Ada Lovelace", "group-a", "group-b"));

        var principal = await _ctx.Principals.ReadAsync(session!.PrincipalId);
        Assert.That(JsonSerializer.Deserialize<List<string>>(principal!.DirectoryGroupsJson),
            Is.EquivalentTo(new[] { "group-a", "group-b" }),
            "Directory group memberships are persisted so AD-group connector gating can be evaluated at dispatch.");
    }

    [Test]
    public async Task SignIn_RefreshesDirectoryGroups_WhenMembershipChanges()
    {
        await _ctx.Provisioner.ProvisionAsync(Entra("sub-1", "Ada Lovelace", "group-a"));
        var session = await _ctx.Provisioner.ProvisionAsync(Entra("sub-1", "Ada Lovelace", "group-b"));

        var principal = await _ctx.Principals.ReadAsync(session!.PrincipalId);
        Assert.That(JsonSerializer.Deserialize<List<string>>(principal!.DirectoryGroupsJson),
            Is.EquivalentTo(new[] { "group-b" }), "The directory is the source of truth; stale groups are dropped.");
    }
}
