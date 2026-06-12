using Auxilia.PlatformData.Entities;

namespace Auxilia.Governance.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class GroupMappingResolverTests
{
    private GovernanceTestContext _ctx = null!;

    [SetUp]
    public void SetUp() => _ctx = new GovernanceTestContext();

    private Task AddMappingAsync(string idp, string group, string role) =>
        _ctx.GroupMappings.SaveAsync(new GroupMappingRecord
        {
            Id = GroupMappingRecord.IdFor(idp, group, role),
            IdentityProvider = idp,
            GroupClaim = group,
            RoleName = role
        });

    [Test]
    public async Task ResolvesRolesForMatchingGroups_OfTheRequestedProviderOnly()
    {
        await AddMappingAsync("aad", "Dev-Team", BuiltInRoles.User);
        await AddMappingAsync("aad", "Platform-Admins", BuiltInRoles.Administrator);
        await AddMappingAsync("ldap", "Dev-Team", BuiltInRoles.Operator); // different provider

        var roles = await _ctx.GroupMappingResolver.ResolveRolesAsync("aad", ["Dev-Team", "Unrelated"]);

        Assert.That(roles, Is.EquivalentTo(new[] { BuiltInRoles.User }));
    }

    [Test]
    public async Task DuplicateRoleFromMultipleGroups_IsReturnedOnce()
    {
        await AddMappingAsync("aad", "Team-A", BuiltInRoles.User);
        await AddMappingAsync("aad", "Team-B", BuiltInRoles.User);

        var roles = await _ctx.GroupMappingResolver.ResolveRolesAsync("aad", ["Team-A", "Team-B"]);

        Assert.That(roles, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task NoMatchingGroups_ReturnsEmpty()
    {
        await AddMappingAsync("aad", "Dev-Team", BuiltInRoles.User);
        Assert.That(await _ctx.GroupMappingResolver.ResolveRolesAsync("aad", ["Other"]), Is.Empty);
    }
}
