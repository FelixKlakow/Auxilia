using Auxilia.Core.Contracts;

namespace Auxilia.Governance;

/// <summary>
/// The four fixed v1 roles as permission sets (ARCHITECTURE.md §16). Custom roles are a
/// later, schema-compatible step — assignments already reference roles by name.
/// </summary>
public static class BuiltInRoles
{
    public const string Administrator = "Administrator";
    public const string Operator = "Operator";
    public const string User = "User";
    public const string Auditor = "Auditor";

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Permissions =
        new Dictionary<string, IReadOnlySet<string>>
        {
            [Administrator] = new HashSet<string>
            {
                PermissionActions.PrincipalAdminister,
                PermissionActions.IdentitySourceManage,
                PermissionActions.PolicyAdminister,
                PermissionActions.ProviderCatalogManage,
                PermissionActions.AuditRead,
                // Signing authority: approving/denying workflow-type registrations is deliberately
                // NOT an Operator permission — operators register, administrators sign.
                PermissionActions.WorkflowTypeSign,
                // Administrators can also do everything operators and users can.
                PermissionActions.WorkflowTypeManage,
                PermissionActions.SlotConfigWrite,
                PermissionActions.WorkflowConfigurationManage,
                PermissionActions.TriggerConfigure,
                PermissionActions.DashboardManage,
                PermissionActions.WorkflowTrigger,
                PermissionActions.RunOnBehalfOf,
                PermissionActions.WorkflowCancel,
                PermissionActions.WorkflowApprove,
                PermissionActions.RunObserve,
                PermissionActions.RunProvideInput,
                PermissionActions.RunOpenTerminal,
                PermissionActions.ViewSubscribe,
                PermissionActions.ArtifactConsume,
                PermissionActions.EventConsume
            },
            [Operator] = new HashSet<string>
            {
                PermissionActions.WorkflowTypeManage,
                PermissionActions.SlotConfigWrite,
                PermissionActions.WorkflowConfigurationManage,
                PermissionActions.TriggerConfigure,
                PermissionActions.DashboardManage,
                PermissionActions.WorkflowTrigger,
                PermissionActions.RunOnBehalfOf,
                PermissionActions.WorkflowCancel,
                PermissionActions.WorkflowApprove,
                PermissionActions.RunObserve,
                PermissionActions.RunProvideInput,
                // The raw session console (a shell in the run's container) is operator power —
                // deliberately NOT granted to User, unlike steering inputs.
                PermissionActions.RunOpenTerminal,
                PermissionActions.ViewSubscribe,
                PermissionActions.ArtifactConsume,
                PermissionActions.EventConsume
            },
            [User] = new HashSet<string>
            {
                PermissionActions.WorkflowTrigger,
                PermissionActions.RunObserve,
                PermissionActions.RunProvideInput,
                PermissionActions.ViewSubscribe
            },
            [Auditor] = new HashSet<string>
            {
                PermissionActions.AuditRead,
                PermissionActions.RunObserve
            }
        };

    public static IReadOnlySet<string> PermissionsOf(string roleName)
        => Permissions.TryGetValue(roleName, out var set) ? set : new HashSet<string>();

    public static bool Exists(string roleName) => Permissions.ContainsKey(roleName);

    public static IReadOnlyCollection<string> AllRoleNames => Permissions.Keys.ToList();
}
