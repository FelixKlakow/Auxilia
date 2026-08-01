using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Governance.Policy;

public sealed class PolicyEngine(
    IDataAccess<PrincipalRecord> principals,
    IDataAccess<RoleAssignmentRecord> roleAssignments,
    WorkflowTypeAccessStore accessStore,
    AuditLog auditLog,
    GroupRoleResolver? groupRoleResolver = null,
    Identity.PrincipalRoleCache? cache = null) : IPolicyEngine
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
        PrincipalRecord? principal;
        IReadOnlyList<string> directRoles;
        if (cache is null || !cache.TryGetPrincipal(context.PrincipalId, out principal, out directRoles))
        {
            principal = await principals.ReadAsync(context.PrincipalId, ct);
            var assignmentsQuery = await roleAssignments.ReadAsync(ct);
            directRoles = assignmentsQuery
                .Where(a => a.PrincipalId == context.PrincipalId)
                .Select(a => a.RoleName)
                .ToList();
            cache?.SetPrincipal(context.PrincipalId, principal, directRoles);
        }

        if (principal is null)
            return PolicyDecision.Deny("unknown-principal");
        if (principal.Status != "Active")
            return PolicyDecision.Deny("principal-disabled");

        var roles = directRoles.ToList();

        // Union in roles the principal holds through first-class group memberships.
        if (groupRoleResolver is not null)
        {
            if (cache is null || !cache.TryGetGroupRoles(context.PrincipalId, out var groupRoles))
            {
                groupRoles = (await groupRoleResolver.RolesForAsync(context.PrincipalId, ct)).ToList();
                cache?.SetGroupRoles(context.PrincipalId, groupRoles);
            }
            roles = roles.Concat(groupRoles).Distinct().ToList();
        }

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
