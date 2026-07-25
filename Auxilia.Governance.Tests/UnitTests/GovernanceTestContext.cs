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

    public AuditLog AuditLog { get; }
    public PrincipalDirectory Directory { get; }
    public WorkflowTypeAccessStore AccessStore { get; }
    public PolicyEngine PolicyEngine { get; }
    public LocalIdentityProvider IdentityProvider { get; }
    public ExternalIdentityProvisioner Provisioner { get; }
    public GroupMappingResolver GroupMappingResolver { get; }

    public GovernanceTestContext()
    {
        AuditLog = new AuditLog(AuditRecords, TimeProvider.System);
        Directory = new PrincipalDirectory(Principals, RoleAssignments, Credentials, AuditLog);
        AccessStore = new WorkflowTypeAccessStore(AccessRecords);
        PolicyEngine = new PolicyEngine(Principals, RoleAssignments, AccessStore, AuditLog);
        IdentityProvider = new LocalIdentityProvider(Credentials, Principals, RoleAssignments);
        Provisioner = new ExternalIdentityProvisioner(Principals, RoleAssignments, AuditLog);
        GroupMappingResolver = new GroupMappingResolver(GroupMappings);
    }

    public async Task<Guid> NewPrincipalWithRoleAsync(string role)
    {
        var principal = await Directory.CreateHumanAsync("Test User", $"user-{Guid.NewGuid():N}", "pw", CancellationToken.None);
        await Directory.AssignRoleAsync(principal.Id, role);
        return principal.Id;
    }

    public async Task<int> AuditCountAsync(string action)
    {
        var query = await AuditRecords.ReadAsync();
        return query.Count(r => r.Action == action);
    }
}
