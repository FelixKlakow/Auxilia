using Auxilia.Governance;
using Auxilia.Governance.Policy;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Auxilia.BackendService.Dashboard;

/// <summary>
/// Live fan-out hub (ARCHITECTURE §15): clients subscribe per run or per view; every
/// subscription is authorized through the Policy Engine — access to a view follows the
/// workflow type it belongs to.
/// </summary>
[Authorize]
public sealed class ViewDataHub(
    IPolicyEngine policyEngine,
    IDataAccess<WorkflowInstanceRecord> instances) : Hub
{
    public static string RunGroup(Guid instanceId) => $"run:{instanceId:N}";
    public static string ViewGroup(Guid instanceId, string viewName) => $"view:{instanceId:N}:{viewName}";
    public const string AllRunsGroup = "runs";

    public async Task SubscribeToRuns()
    {
        if (await AllowedAsync(PermissionActions.RunObserve, "runs", null))
            await Groups.AddToGroupAsync(Context.ConnectionId, AllRunsGroup);
        else
            throw new HubException("run.observe denied");
    }

    public async Task SubscribeToRun(Guid instanceId)
    {
        var record = await instances.ReadAsync(instanceId);
        if (record is null)
            throw new HubException("unknown run");
        if (!await AllowedAsync(PermissionActions.RunObserve, instanceId.ToString(), record.WorkflowType))
            throw new HubException("run.observe denied");

        await Groups.AddToGroupAsync(Context.ConnectionId, RunGroup(instanceId));
    }

    public async Task SubscribeToView(Guid instanceId, string viewName)
    {
        var record = await instances.ReadAsync(instanceId);
        if (record is null)
            throw new HubException("unknown run");
        if (!await AllowedAsync(PermissionActions.ViewSubscribe, $"{instanceId}:{viewName}", record.WorkflowType))
            throw new HubException("view.subscribe denied");

        await Groups.AddToGroupAsync(Context.ConnectionId, ViewGroup(instanceId, viewName));
    }

    private async Task<bool> AllowedAsync(string action, string resource, string? workflowType)
    {
        var principalId = DashboardAuthEndpoints.PrincipalIdOf(Context.User!);
        if (principalId is null)
            return false;

        var decision = await policyEngine.EvaluateAsync(
            new PolicyContext(principalId.Value, action, resource) { WorkflowType = workflowType });
        return decision.Allowed;
    }
}
