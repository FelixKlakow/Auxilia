using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Governance.Policy;

public sealed class PolicyEngine(
    IDataAccess<PrincipalRecord> principals,
    IDataAccess<RoleAssignmentRecord> roleAssignments,
    WorkflowTypeAccessStore accessStore,
    AuditLog auditLog) : IPolicyEngine
{
    public async Task<PolicyDecision> EvaluateAsync(PolicyContext context, CancellationToken ct = default)
    {
        var decision = await DecideAsync(context, ct);

        await auditLog.AppendAsync(
            context.PrincipalId.ToString(),
            $"policy.{(decision.Allowed ? "allowed" : "denied")}",
            $"{context.Action}:{context.Resource}",
            decision.Reason,
            ct: ct);

        return decision;
    }

    private async Task<PolicyDecision> DecideAsync(PolicyContext context, CancellationToken ct)
    {
        var principal = await principals.ReadAsync(context.PrincipalId, ct);
        if (principal is null)
            return PolicyDecision.Deny("unknown-principal");
        if (principal.Status != "Active")
            return PolicyDecision.Deny("principal-disabled");

        var assignmentsQuery = await roleAssignments.ReadAsync(ct);
        var roles = assignmentsQuery
            .Where(a => a.PrincipalId == context.PrincipalId)
            .Select(a => a.RoleName)
            .ToList();

        // Workflow-type access lists take precedence: when entries exist for this
        // (workflow type, action), they are the exclusive grant source.
        if (context.WorkflowType is not null)
        {
            var entries = await accessStore.GetEntriesAsync(context.WorkflowType, context.Action, ct);
            if (entries.Count > 0)
            {
                var matched = entries.Any(e =>
                    e.PrincipalId == context.PrincipalId ||
                    (e.RoleName is not null && roles.Contains(e.RoleName)));
                return matched
                    ? PolicyDecision.Allow("workflow-type-access")
                    : PolicyDecision.Deny("workflow-type-access-list-excludes-principal");
            }
        }

        var permitted = roles
            .SelectMany(BuiltInRoles.PermissionsOf)
            .Contains(context.Action);

        return permitted
            ? PolicyDecision.Allow("role-permission")
            : PolicyDecision.Deny("no-role-grants-action");
    }
}
