namespace Auxilia.Governance;

/// <summary>Machine-readable action identifiers checked by the Policy Engine.</summary>
public static class PermissionActions
{
    public const string WorkflowTrigger = "workflow.trigger";
    public const string WorkflowCancel = "workflow.cancel";
    public const string WorkflowApprove = "workflow.approve";
    public const string RunObserve = "run.observe";
    public const string RunProvideInput = "run.provide-input";
    public const string ViewSubscribe = "view.subscribe";
    public const string ArtifactConsume = "artifact.consume";
    public const string SlotConfigWrite = "slot-config.write";
    public const string ProviderCatalogManage = "provider-catalog.manage";
    public const string WorkflowConfigurationManage = "workflow-configuration.manage";
    public const string WorkflowTypeManage = "workflow-type.manage";
    public const string TriggerConfigure = "trigger.configure";
    public const string DashboardManage = "dashboard.manage";
    public const string BundleManage = "bundle.manage";
    public const string PrincipalAdminister = "principal.administer";
    public const string PolicyAdminister = "policy.administer";
    public const string AuditRead = "audit.read";
}
