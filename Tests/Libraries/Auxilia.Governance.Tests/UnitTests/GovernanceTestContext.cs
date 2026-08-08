using Auxilia.Governance;
using Auxilia.Governance.Identity;
using Auxilia.Governance.Policy;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.UniversalDataAccess.Implementations;

namespace Auxilia.Governance.Tests.UnitTests;

/// <summary>All governance services wired over in-memory storage for unit tests.</summary>
internal sealed class GovernanceTestContext
{
    public IDataAccess<PrincipalRecord> Principals { get; } = new InMemoryDataAccess<PrincipalRecord>();
    public IDataAccess<RoleAssignmentRecord> RoleAssignments { get; } = new InMemoryDataAccess<RoleAssignmentRecord>();
    public IDataAccess<CredentialRecord> Credentials { get; } = new InMemoryDataAccess<CredentialRecord>();
    public IDataAccess<GroupMappingRecord> GroupMappings { get; } = new InMemoryDataAccess<GroupMappingRecord>();
    public IDataAccess<WorkflowTypeAccessRecord> AccessRecords { get; } = new InMemoryDataAccess<WorkflowTypeAccessRecord>();
    public IDataAccess<AuditRecord> AuditRecords { get; } = new InMemoryDataAccess<AuditRecord>();
    public IDataAccess<GroupMembershipRecord> GroupMemberships { get; } = new InMemoryDataAccess<GroupMembershipRecord>();
    public IDataAccess<GroupRoleRecord> GroupRoles { get; } = new InMemoryDataAccess<GroupRoleRecord>();

    public AuditLog AuditLog { get; }
    public PrincipalDirectory Directory { get; }
    public WorkflowTypeAccessStore AccessStore { get; }
    public PolicyEngine PolicyEngine { get; }
    public LocalIdentityProvider IdentityProvider { get; }
    public ExternalIdentityProvisioner Provisioner { get; }
    public GroupMappingResolver GroupMappingResolver { get; }
    public GroupMappingDirectory GroupMappingDirectory { get; }

    /// <summary>The platform default-access posture; restricted like production unless a test opens it.</summary>
    public MutableDefaultResourceAccess DefaultAccess { get; } = new();

    public GovernanceTestContext()
    {
        AuditLog = new AuditLog(AuditRecords, TimeProvider.System);
        Directory = new PrincipalDirectory(Principals, RoleAssignments, Credentials, AuditLog);
        AccessStore = new WorkflowTypeAccessStore(AccessRecords);
        PolicyEngine = new PolicyEngine(
            Principals, RoleAssignments, AccessStore, AuditLog,
            new GroupRoleResolver(GroupMemberships, GroupRoles),
            defaultAccess: DefaultAccess);
        IdentityProvider = new LocalIdentityProvider(Credentials, Principals, RoleAssignments);
        GroupMappingResolver = new GroupMappingResolver(GroupMappings);
        GroupMappingDirectory = new GroupMappingDirectory(GroupMappings, AuditLog);
        Provisioner = new ExternalIdentityProvisioner(Principals, RoleAssignments, GroupMappingResolver, AuditLog);
    }

    public async Task<Guid> NewPrincipalWithRoleAsync(string role)
    {
        var principal = await Directory.CreateHumanAsync("Test User", $"user-{Guid.NewGuid():N}", "pw", CancellationToken.None);
        await Directory.AssignRoleAsync(principal.Id, role);
        return principal.Id;
    }

    public Task AddGroupMemberAsync(Guid groupId, Guid principalId)
        => GroupMemberships.SaveAsync(new GroupMembershipRecord
        {
            Id = GroupMembershipRecord.IdFor(groupId, principalId),
            GroupId = groupId,
            PrincipalId = principalId
        });

    public async Task<int> AuditCountAsync(string action)
    {
        var query = await AuditRecords.ReadAsync();
        return query.Count(r => r.Action == action);
    }
}

/// <summary>A flippable <see cref="IDefaultResourceAccessPolicy"/> for tests.</summary>
internal sealed class MutableDefaultResourceAccess : IDefaultResourceAccessPolicy
{
    public bool Restricted { get; set; } = true;

    public Task<bool> IsRestrictedAsync(CancellationToken ct) => Task.FromResult(Restricted);
}
