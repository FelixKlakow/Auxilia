using Auxilia.Governance.Policy;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess.Implementations;

namespace Auxilia.Governance.Tests;

[TestFixture]
[Category("Unit")]
public sealed class GroupTests
{
    private static AuditLog NewAudit() => new(new InMemoryDataAccess<AuditRecord>(), TimeProvider.System);

    [Test]
    public async Task GroupDirectory_CreateAddMemberAssignRole_Works()
    {
        var directory = new GroupDirectory(
            new InMemoryDataAccess<GroupRecord>(),
            new InMemoryDataAccess<GroupMembershipRecord>(),
            new InMemoryDataAccess<GroupRoleRecord>(),
            NewAudit());

        var group = await directory.CreateAsync("devs", "Developers");
        var principalId = Guid.NewGuid();
        await directory.AddMemberAsync(group.Id, principalId);
        await directory.AssignRoleAsync(group.Id, BuiltInRoles.Operator);

        Assert.That((await directory.ListAsync()).Single().Name, Is.EqualTo("devs"));
        Assert.That(await directory.MembersAsync(group.Id), Does.Contain(principalId));
        Assert.That(await directory.RolesAsync(group.Id), Does.Contain(BuiltInRoles.Operator));
    }

    [Test]
    public void GroupDirectory_AssignUnknownRole_Throws()
    {
        var directory = new GroupDirectory(
            new InMemoryDataAccess<GroupRecord>(),
            new InMemoryDataAccess<GroupMembershipRecord>(),
            new InMemoryDataAccess<GroupRoleRecord>(),
            NewAudit());

        Assert.ThrowsAsync<ArgumentException>(() => directory.AssignRoleAsync(Guid.NewGuid(), "NotARole"));
    }

    [Test]
    public async Task GroupDirectory_RemoveMember_ClearsMembership()
    {
        var directory = new GroupDirectory(
            new InMemoryDataAccess<GroupRecord>(),
            new InMemoryDataAccess<GroupMembershipRecord>(),
            new InMemoryDataAccess<GroupRoleRecord>(),
            NewAudit());

        var group = await directory.CreateAsync("devs");
        var principalId = Guid.NewGuid();
        await directory.AddMemberAsync(group.Id, principalId);
        Assert.That(await directory.RemoveMemberAsync(group.Id, principalId), Is.True);
        Assert.That(await directory.MembersAsync(group.Id), Is.Empty);
    }

    [Test]
    public async Task PolicyEngine_PrincipalInGroupWithRole_GrantsGroupPermissions()
    {
        var principals = new InMemoryDataAccess<PrincipalRecord>();
        var memberships = new InMemoryDataAccess<GroupMembershipRecord>();
        var groupRoles = new InMemoryDataAccess<GroupRoleRecord>();
        var engine = new PolicyEngine(
            principals,
            new InMemoryDataAccess<RoleAssignmentRecord>(),
            new WorkflowTypeAccessStore(new InMemoryDataAccess<WorkflowTypeAccessRecord>()),
            NewAudit(),
            new GroupRoleResolver(memberships, groupRoles));

        var principal = new PrincipalRecord { Kind = "Human", DisplayName = "u", Status = "Active" };
        await principals.SaveAsync(principal);
        var groupId = GroupRecord.IdFor(Tenants.DefaultTenantId, "devs");
        await memberships.SaveAsync(new GroupMembershipRecord
        {
            Id = GroupMembershipRecord.IdFor(groupId, principal.Id),
            GroupId = groupId,
            PrincipalId = principal.Id
        });
        await groupRoles.SaveAsync(new GroupRoleRecord
        {
            Id = GroupRoleRecord.IdFor(groupId, BuiltInRoles.User),
            GroupId = groupId,
            RoleName = BuiltInRoles.User
        });

        var decision = await engine.EvaluateAsync(
            new PolicyContext(principal.Id, PermissionActions.WorkflowTrigger, "wt"));
        Assert.That(decision.Allowed, Is.True, decision.Reason);
    }

    [Test]
    public async Task PolicyEngine_PrincipalNotInGroup_IsDenied()
    {
        var principals = new InMemoryDataAccess<PrincipalRecord>();
        var engine = new PolicyEngine(
            principals,
            new InMemoryDataAccess<RoleAssignmentRecord>(),
            new WorkflowTypeAccessStore(new InMemoryDataAccess<WorkflowTypeAccessRecord>()),
            NewAudit(),
            new GroupRoleResolver(new InMemoryDataAccess<GroupMembershipRecord>(), new InMemoryDataAccess<GroupRoleRecord>()));

        var principal = new PrincipalRecord { Kind = "Human", DisplayName = "u", Status = "Active" };
        await principals.SaveAsync(principal);

        var decision = await engine.EvaluateAsync(
            new PolicyContext(principal.Id, PermissionActions.WorkflowTrigger, "wt"));
        Assert.That(decision.Allowed, Is.False);
    }
}
