using Auxilia.Governance.Policy;

namespace Auxilia.Governance.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class PolicyEngineTests
{
    private GovernanceTestContext _ctx = null!;

    [SetUp]
    public void SetUp() => _ctx = new GovernanceTestContext();

    [Test]
    public async Task UnknownPrincipal_IsDenied()
    {
        var decision = await _ctx.PolicyEngine.EvaluateAsync(
            new PolicyContext(Guid.NewGuid(), PermissionActions.WorkflowTrigger, "wf-1"));

        Assert.That(decision.Allowed, Is.False);
        Assert.That(decision.Reason, Is.EqualTo("unknown-principal"));
    }

    [Test]
    public async Task DisabledPrincipal_IsDenied()
    {
        var principalId = await _ctx.NewPrincipalWithRoleAsync(BuiltInRoles.User);
        await _ctx.Directory.DisableAsync(principalId);

        var decision = await _ctx.PolicyEngine.EvaluateAsync(
            new PolicyContext(principalId, PermissionActions.WorkflowTrigger, "wf-1"));

        Assert.That(decision.Allowed, Is.False);
        Assert.That(decision.Reason, Is.EqualTo("principal-disabled"));
    }

    [Test]
    public async Task UserRole_MayTriggerWorkflows_ButNotAdministerPolicy()
    {
        var principalId = await _ctx.NewPrincipalWithRoleAsync(BuiltInRoles.User);

        var trigger = await _ctx.PolicyEngine.EvaluateAsync(
            new PolicyContext(principalId, PermissionActions.WorkflowTrigger, "wf-1"));
        var admin = await _ctx.PolicyEngine.EvaluateAsync(
            new PolicyContext(principalId, PermissionActions.PolicyAdminister, "platform"));

        Assert.Multiple(() =>
        {
            Assert.That(trigger.Allowed, Is.True);
            Assert.That(admin.Allowed, Is.False);
        });
    }

    [Test]
    public async Task AuditorRole_MayReadAudit_ButNotTrigger()
    {
        var principalId = await _ctx.NewPrincipalWithRoleAsync(BuiltInRoles.Auditor);

        var audit = await _ctx.PolicyEngine.EvaluateAsync(
            new PolicyContext(principalId, PermissionActions.AuditRead, "audit"));
        var trigger = await _ctx.PolicyEngine.EvaluateAsync(
            new PolicyContext(principalId, PermissionActions.WorkflowTrigger, "wf-1"));

        Assert.Multiple(() =>
        {
            Assert.That(audit.Allowed, Is.True);
            Assert.That(trigger.Allowed, Is.False);
        });
    }

    [Test]
    public async Task PrincipalWithoutRoles_IsDeniedEverything()
    {
        var principal = await _ctx.Directory.CreateHumanAsync("No Roles", "noroles", "pw");

        var decision = await _ctx.PolicyEngine.EvaluateAsync(
            new PolicyContext(principal.Id, PermissionActions.RunObserve, "run-1"));

        Assert.That(decision.Allowed, Is.False);
        Assert.That(decision.Reason, Is.EqualTo("no-role-grants-action"));
    }

    [Test]
    public async Task WorkflowTypeAccessList_WhenPresent_IsExclusive()
    {
        // Both principals hold the User role (which normally grants workflow.trigger)…
        var granted = await _ctx.NewPrincipalWithRoleAsync(BuiltInRoles.User);
        var excluded = await _ctx.NewPrincipalWithRoleAsync(BuiltInRoles.User);

        // …but an access list for this workflow type only includes one of them.
        await _ctx.AccessStore.GrantPrincipalAsync("secure-wf", PermissionActions.WorkflowTrigger, granted);

        var grantedDecision = await _ctx.PolicyEngine.EvaluateAsync(
            new PolicyContext(granted, PermissionActions.WorkflowTrigger, "run") { WorkflowType = "secure-wf" });
        var excludedDecision = await _ctx.PolicyEngine.EvaluateAsync(
            new PolicyContext(excluded, PermissionActions.WorkflowTrigger, "run") { WorkflowType = "secure-wf" });

        Assert.Multiple(() =>
        {
            Assert.That(grantedDecision.Allowed, Is.True);
            Assert.That(grantedDecision.Reason, Is.EqualTo("workflow-type-access"));
            Assert.That(excludedDecision.Allowed, Is.False);
            Assert.That(excludedDecision.Reason, Is.EqualTo("workflow-type-access-list-excludes-principal"));
        });
    }

    [Test]
    public async Task WorkflowTypeAccessList_GrantsByRole()
    {
        var auditor = await _ctx.NewPrincipalWithRoleAsync(BuiltInRoles.Auditor);
        await _ctx.AccessStore.GrantRoleAsync("audit-wf", PermissionActions.WorkflowTrigger, BuiltInRoles.Auditor);

        var decision = await _ctx.PolicyEngine.EvaluateAsync(
            new PolicyContext(auditor, PermissionActions.WorkflowTrigger, "run") { WorkflowType = "audit-wf" });

        Assert.That(decision.Allowed, Is.True, "Access list can grant beyond role permissions.");
    }

    [Test]
    public async Task WorkflowTypeWithoutAccessList_FallsBackToRolePermissions()
    {
        var user = await _ctx.NewPrincipalWithRoleAsync(BuiltInRoles.User);

        var decision = await _ctx.PolicyEngine.EvaluateAsync(
            new PolicyContext(user, PermissionActions.WorkflowTrigger, "run") { WorkflowType = "open-wf" });

        Assert.That(decision.Allowed, Is.True);
        Assert.That(decision.Reason, Is.EqualTo("role-permission"));
    }

    [Test]
    public async Task EveryDecision_AllowAndDeny_IsAudited()
    {
        var user = await _ctx.NewPrincipalWithRoleAsync(BuiltInRoles.User);

        await _ctx.PolicyEngine.EvaluateAsync(
            new PolicyContext(user, PermissionActions.WorkflowTrigger, "wf-1"));
        await _ctx.PolicyEngine.EvaluateAsync(
            new PolicyContext(user, PermissionActions.PolicyAdminister, "platform"));

        Assert.Multiple(async () =>
        {
            Assert.That(await _ctx.AuditCountAsync("policy.allowed"), Is.EqualTo(1));
            Assert.That(await _ctx.AuditCountAsync("policy.denied"), Is.EqualTo(1));
        });
    }
}
