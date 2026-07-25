using Auxilia.Governance.Policy;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess.Implementations;

namespace Auxilia.Governance.Tests.UnitTests;

/// <summary>
/// The security specification for the built-in roles, verified through the real Policy Engine:
/// who may do what, deny-by-default for the unroled, and refusal of disabled / unknown principals.
/// Independent of <see cref="BuiltInRoles"/>' internal table so it catches a change that silently
/// widens a role.
/// </summary>
[TestFixture]
[Category("Unit")]
public class RolePermissionMatrixTests
{
    private GovernanceTestContext _ctx = null!;

    [SetUp]
    public void SetUp() => _ctx = new GovernanceTestContext();

    private async Task<bool> AllowsAsync(Guid principalId, string action)
        => (await _ctx.PolicyEngine.EvaluateAsync(new PolicyContext(principalId, action, action))).Allowed;

    private static object[] Matrix =>
    [
        // Administration is Administrator-only.
        new object[] { BuiltInRoles.Administrator, PermissionActions.PrincipalAdminister, true },
        new object[] { BuiltInRoles.Operator, PermissionActions.PrincipalAdminister, false },
        new object[] { BuiltInRoles.User, PermissionActions.PrincipalAdminister, false },
        new object[] { BuiltInRoles.Auditor, PermissionActions.PrincipalAdminister, false },
        new object[] { BuiltInRoles.Administrator, PermissionActions.IdentitySourceManage, true },
        new object[] { BuiltInRoles.Operator, PermissionActions.IdentitySourceManage, false },
        new object[] { BuiltInRoles.Administrator, PermissionActions.PolicyAdminister, true },
        new object[] { BuiltInRoles.Operator, PermissionActions.PolicyAdminister, false },
        // Configuration is Operator+ (not User/Auditor).
        new object[] { BuiltInRoles.Operator, PermissionActions.SlotConfigWrite, true },
        new object[] { BuiltInRoles.User, PermissionActions.SlotConfigWrite, false },
        new object[] { BuiltInRoles.Auditor, PermissionActions.SlotConfigWrite, false },
        new object[] { BuiltInRoles.Operator, PermissionActions.WorkflowConfigurationManage, true },
        new object[] { BuiltInRoles.User, PermissionActions.WorkflowConfigurationManage, false },
        // Triggering is User+ (not Auditor).
        new object[] { BuiltInRoles.User, PermissionActions.WorkflowTrigger, true },
        new object[] { BuiltInRoles.Operator, PermissionActions.WorkflowTrigger, true },
        new object[] { BuiltInRoles.Auditor, PermissionActions.WorkflowTrigger, false },
        new object[] { BuiltInRoles.User, PermissionActions.RunObserve, true },
        // Audit reading is Auditor and Administrator only.
        new object[] { BuiltInRoles.Auditor, PermissionActions.AuditRead, true },
        new object[] { BuiltInRoles.Administrator, PermissionActions.AuditRead, true },
        new object[] { BuiltInRoles.Operator, PermissionActions.AuditRead, false },
        new object[] { BuiltInRoles.User, PermissionActions.AuditRead, false }
    ];

    [TestCaseSource(nameof(Matrix))]
    public async Task Role_Grants_ExactlyTheExpectedAction(string role, string action, bool expected)
    {
        var principalId = await _ctx.NewPrincipalWithRoleAsync(role);
        Assert.That(await AllowsAsync(principalId, action), Is.EqualTo(expected),
            $"{role} {(expected ? "should" : "must NOT")} be granted {action}");
    }

    [Test]
    public async Task PrincipalWithNoRole_IsDeniedEverySensitiveAction()
    {
        var principal = await _ctx.Directory.CreateHumanAsync(
            "no-role", $"nr-{Guid.NewGuid():N}", "pw", CancellationToken.None);

        string[] sensitive =
        [
            PermissionActions.WorkflowTrigger, PermissionActions.PrincipalAdminister,
            PermissionActions.SlotConfigWrite, PermissionActions.AuditRead,
            PermissionActions.IdentitySourceManage, PermissionActions.WorkflowConfigurationManage
        ];
        foreach (var action in sensitive)
            Assert.That(await AllowsAsync(principal.Id, action), Is.False,
                $"deny-by-default failed: an unroled principal was granted {action}");
    }

    [Test]
    public async Task DisabledPrincipal_IsDenied_EvenWithAdministratorRole()
    {
        var principalId = await _ctx.NewPrincipalWithRoleAsync(BuiltInRoles.Administrator);
        await _ctx.Directory.DisableAsync(principalId);

        Assert.That(await AllowsAsync(principalId, PermissionActions.PrincipalAdminister), Is.False,
            "A disabled account must lose all access regardless of its roles.");
    }

    [Test]
    public async Task UnknownPrincipal_IsDenied()
        => Assert.That(await AllowsAsync(Guid.NewGuid(), PermissionActions.RunObserve), Is.False);

    [Test]
    public async Task GroupMembership_UnionsItsRole_WithTheDirectAssignment()
    {
        // A principal with a direct User role, additionally placed in an Operator group, gains
        // Operator permissions without losing anything — roles union, they don't override.
        var principalId = await _ctx.NewPrincipalWithRoleAsync(BuiltInRoles.User);
        var groups = new InMemoryDataAccess<GroupRecord>();
        var memberships = new InMemoryDataAccess<GroupMembershipRecord>();
        var groupRoles = new InMemoryDataAccess<GroupRoleRecord>();
        var groupDirectory = new GroupDirectory(groups, memberships, groupRoles, _ctx.AuditLog);
        var group = await groupDirectory.CreateAsync("operators");
        await groupDirectory.AddMemberAsync(group.Id, principalId);
        await groupDirectory.AssignRoleAsync(group.Id, BuiltInRoles.Operator);

        var engine = new PolicyEngine(
            _ctx.Principals, _ctx.RoleAssignments, _ctx.AccessStore, _ctx.AuditLog,
            new GroupRoleResolver(memberships, groupRoles));

        Assert.Multiple(async () =>
        {
            Assert.That((await engine.EvaluateAsync(
                new PolicyContext(principalId, PermissionActions.SlotConfigWrite, "x"))).Allowed, Is.True,
                "The group's Operator role should grant configuration.");
            Assert.That((await engine.EvaluateAsync(
                new PolicyContext(principalId, PermissionActions.WorkflowTrigger, "x"))).Allowed, Is.True,
                "The direct User role should still grant triggering.");
        });
    }
}
