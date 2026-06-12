using System.ComponentModel;
using System.Text.Json;
using Auxilia.BackendService.Dashboard;
using Auxilia.Governance;
using Auxilia.Governance.Policy;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.Views;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Auxilia.BackendService.Mcp;

/// <summary>
/// Every user-facing dashboard operation as an MCP tool (ARCHITECTURE §4/§5): AI agents get
/// full UI parity. Each tool is authorized through the Policy Engine with the caller's
/// authenticated principal; denials surface as tool errors carrying the policy reason.
/// </summary>
[McpServerToolType]
public sealed class AuxiliaMcpTools(
    IPolicyEngine policyEngine,
    IMessageBusClient messageBus,
    DashboardSettings dashboardSettings,
    IDataAccess<WorkflowInstanceRecord> instances,
    IDataAccess<ViewDataRecord> viewData,
    IDataAccess<AuditRecord> auditRecords,
    AuditLog auditLog)
{
    private const string CancelQueueName = "workflow.cancel-commands";
    private const int MaxItems = 500;

    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    [McpServerTool(Name = "trigger_workflow")]
    [Description("Triggers a run of the given workflow type from its signed package URI. " +
                 "Returns the command ID of the published run command.")]
    public async Task<CallToolResult> TriggerWorkflowAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Workflow type name, e.g. CodeReviewWorkflow.")] string workflowType,
        [Description("URI of the signed workflow package.")] string workflowPackageUri,
        [Description("Optional work item ID forwarded to the workflow as context.")] string? workItemId = null,
        CancellationToken cancellationToken = default)
    {
        if (PrincipalIdOf(context) is not { } principalId)
            return NoPrincipal();
        if (string.IsNullOrWhiteSpace(workflowType) || string.IsNullOrWhiteSpace(workflowPackageUri))
            return Error("workflowType and workflowPackageUri are required.");

        var denial = await DenyAsync(principalId,
            PermissionActions.WorkflowTrigger, workflowType, workflowType, cancellationToken);
        if (denial is not null)
            return denial;

        var workflowContext = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(workItemId))
            workflowContext["WorkItemId"] = workItemId.Trim();

        var command = new RunWorkflowCommand(
            Guid.NewGuid(), workflowType.Trim(), workflowPackageUri.Trim(),
            workflowContext, principalId);
        await messageBus.PublishAsync(dashboardSettings.CommandQueueName, command, cancellationToken);

        return Json(new { commandId = command.CommandId, queue = dashboardSettings.CommandQueueName });
    }

    [McpServerTool(Name = "get_workflow_status")]
    [Description("Returns state, type, timestamps, and error of a workflow run.")]
    public async Task<CallToolResult> GetWorkflowStatusAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Workflow instance ID (GUID).")] string instanceId,
        CancellationToken cancellationToken = default)
    {
        if (PrincipalIdOf(context) is not { } principalId)
            return NoPrincipal();
        if (!Guid.TryParse(instanceId, out var id))
            return Error("instanceId must be a GUID.");

        var run = await instances.ReadAsync(id, cancellationToken);
        if (run is null)
            return Error($"No workflow run with instance ID {id:D} exists.");

        var denial = await DenyAsync(principalId,
            PermissionActions.RunObserve, instanceId, run.WorkflowType, cancellationToken);
        if (denial is not null)
            return denial;

        return Json(new
        {
            instanceId = run.Id,
            workflowType = run.WorkflowType,
            state = run.State,
            lifetime = run.Lifetime,
            createdUtc = run.CreatedUtc,
            completedUtc = run.CompletedUtc,
            errorMessage = run.ErrorMessage
        });
    }

    [McpServerTool(Name = "list_runs")]
    [Description("Lists workflow runs, newest first.")]
    public async Task<CallToolResult> ListRunsAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Maximum number of runs to return (default 50, max 500).")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (PrincipalIdOf(context) is not { } principalId)
            return NoPrincipal();

        var denial = await DenyAsync(principalId,
            PermissionActions.RunObserve, "runs", workflowType: null, cancellationToken);
        if (denial is not null)
            return denial;

        var all = await instances.ReadAsync(cancellationToken);
        var runs = all
            .OrderByDescending(r => r.CreatedUtc)
            .Take(Math.Clamp(limit, 1, MaxItems))
            .Select(r => new
            {
                instanceId = r.Id,
                workflowType = r.WorkflowType,
                state = r.State,
                createdUtc = r.CreatedUtc,
                completedUtc = r.CompletedUtc
            })
            .ToList();
        return Json(new { runs });
    }

    [McpServerTool(Name = "cancel_workflow")]
    [Description("Requests cancellation of a running workflow instance.")]
    public async Task<CallToolResult> CancelWorkflowAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Workflow instance ID (GUID).")] string instanceId,
        CancellationToken cancellationToken = default)
    {
        if (PrincipalIdOf(context) is not { } principalId)
            return NoPrincipal();
        if (!Guid.TryParse(instanceId, out var id))
            return Error("instanceId must be a GUID.");

        var run = await instances.ReadAsync(id, cancellationToken);
        if (run is null)
            return Error($"No workflow run with instance ID {id:D} exists.");

        var denial = await DenyAsync(principalId,
            PermissionActions.WorkflowCancel, instanceId, run.WorkflowType, cancellationToken);
        if (denial is not null)
            return denial;

        await messageBus.PublishAsync(CancelQueueName, new CancelWorkflowCommand(id), cancellationToken);
        await auditLog.AppendAsync(
            principalId.ToString("D"), "mcp.cancel", id.ToString("D"), "cancel-requested",
            ct: cancellationToken);

        return Json(new { instanceId = id, queue = CancelQueueName, cancelRequested = true });
    }

    [McpServerTool(Name = "get_view_data")]
    [Description("Returns the persisted items of one view of a workflow run, ordered by sequence — " +
                 "works for live and finished runs alike.")]
    public async Task<CallToolResult> GetViewDataAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Workflow instance ID (GUID).")] string instanceId,
        [Description("Name of the view as declared by the workflow.")] string viewName,
        [Description("Only return items with sequence >= this value (default 0).")] long fromSequence = 0,
        CancellationToken cancellationToken = default)
    {
        if (PrincipalIdOf(context) is not { } principalId)
            return NoPrincipal();
        if (!Guid.TryParse(instanceId, out var id))
            return Error("instanceId must be a GUID.");

        var run = await instances.ReadAsync(id, cancellationToken);
        if (run is null)
            return Error($"No workflow run with instance ID {id:D} exists.");

        var denial = await DenyAsync(principalId,
            PermissionActions.ViewSubscribe, instanceId, run.WorkflowType, cancellationToken);
        if (denial is not null)
            return denial;

        var allItems = await viewData.ReadAsync(cancellationToken);
        var items = allItems
            .Where(i => i.WorkflowInstanceId == id && i.ViewName == viewName && i.Sequence >= fromSequence)
            .OrderBy(i => i.Sequence)
            .Take(MaxItems)
            .Select(i => new
            {
                sequence = i.Sequence,
                payloadJson = i.PayloadJson,
                timestampUtc = i.TimestampUtc
            })
            .ToList();
        return Json(new { instanceId = id, viewName, items });
    }

    [McpServerTool(Name = "list_views")]
    [Description("Lists the views a workflow run declares (name, rendering, lifecycle).")]
    public async Task<CallToolResult> ListViewsAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Workflow instance ID (GUID).")] string instanceId,
        CancellationToken cancellationToken = default)
    {
        if (PrincipalIdOf(context) is not { } principalId)
            return NoPrincipal();
        if (!Guid.TryParse(instanceId, out var id))
            return Error("instanceId must be a GUID.");

        var run = await instances.ReadAsync(id, cancellationToken);
        if (run is null)
            return Error($"No workflow run with instance ID {id:D} exists.");

        var denial = await DenyAsync(principalId,
            PermissionActions.RunObserve, instanceId, run.WorkflowType, cancellationToken);
        if (denial is not null)
            return denial;

        var views = ParseDescriptors(run.ViewsJson)
            .Select(v => new
            {
                name = v.Name,
                rendering = v.Rendering.ToString(),
                lifecycle = v.Lifecycle.ToString()
            })
            .ToList();
        return Json(new { instanceId = id, views });
    }

    [McpServerTool(Name = "read_audit")]
    [Description("Reads the platform audit log, newest first.")]
    public async Task<CallToolResult> ReadAuditAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Maximum number of entries to return (default 100, max 500).")] int limit = 100,
        [Description("Optional exact match on the audit action, e.g. mcp.cancel.")] string? actionFilter = null,
        CancellationToken cancellationToken = default)
    {
        if (PrincipalIdOf(context) is not { } principalId)
            return NoPrincipal();

        var denial = await DenyAsync(principalId,
            PermissionActions.AuditRead, "audit", workflowType: null, cancellationToken);
        if (denial is not null)
            return denial;

        var all = await auditRecords.ReadAsync(cancellationToken);
        var query = all.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(actionFilter))
            query = query.Where(a => a.Action == actionFilter);
        var entries = query
            .OrderByDescending(a => a.TimestampUtc)
            .Take(Math.Clamp(limit, 1, MaxItems))
            .Select(a => new
            {
                timestampUtc = a.TimestampUtc,
                actor = a.Actor,
                action = a.Action,
                subject = a.Subject,
                outcome = a.Outcome,
                detailJson = a.DetailJson
            })
            .ToList();
        return Json(new { entries });
    }

    /// <summary>Returns a tool error carrying the policy reason when the action is denied, else null.</summary>
    private async Task<CallToolResult?> DenyAsync(
        Guid principalId, string action, string resource, string? workflowType, CancellationToken ct)
    {
        var decision = await policyEngine.EvaluateAsync(
            new PolicyContext(principalId, action, resource) { WorkflowType = workflowType }, ct);
        return decision.Allowed
            ? null
            : Error($"{action} denied by policy: {decision.Reason}");
    }

    private static Guid? PrincipalIdOf(RequestContext<CallToolRequestParams> context)
        => context.User is { } user ? DashboardAuthEndpoints.PrincipalIdOf(user) : null;

    private static IReadOnlyList<ViewDescriptor> ParseDescriptors(string? viewsJson)
    {
        if (string.IsNullOrWhiteSpace(viewsJson))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<ViewDescriptor>>(viewsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static CallToolResult Json(object payload) => new()
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload, JsonOptions) }]
    };

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }]
    };

    private static CallToolResult NoPrincipal()
        => Error("No authenticated platform principal on this MCP session.");
}
