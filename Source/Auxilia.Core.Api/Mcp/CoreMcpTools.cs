using System.ComponentModel;
using System.Text.Json;
using Auxilia.Core.Api.Auth;
using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;
using Auxilia.Governance;
using Auxilia.Governance.Policy;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Auxilia.Core.Api.Mcp;

/// <summary>
/// The Core's operations as MCP tools — AI/service parity with the REST API over the same
/// service layer. Every tool runs as the authenticated MCP principal and sensitive actions are
/// authorized through the Policy Engine (ARCHITECTURE §5): MCP is a first-class, authenticated
/// interface, not a hand-maintained mirror.
/// </summary>
[McpServerToolType]
public sealed class CoreMcpTools(
    IPolicyEngine policyEngine,
    RunService runs,
    RunConfigurationService configurations,
    RunReadService runView,
    ConnectorService connectors)
{
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    [McpServerTool(Name = "run_workflow")]
    [Description("Dispatches a run of the given workflow type from its package URI, on the fly.")]
    public async Task<CallToolResult> RunWorkflowAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Workflow type name.")] string workflowType,
        [Description("Package URI, e.g. docker://image:tag or an https .workflow.zip URL.")] string packageUri,
        [Description("Optional JSON object of run context key/values.")] string? contextJson = null,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } principalId)
            return NoPrincipal();
        if (await DenyAsync(principalId, PermissionActions.WorkflowTrigger, workflowType, cancellationToken) is { } denial)
            return denial;

        var accepted = await runs.RunInlineAsync(
            new RunRequest(workflowType, packageUri, ParseObject(contextJson)), cancellationToken);
        return JsonResult(new { runId = accepted.RunId, commandId = accepted.CommandId });
    }

    [McpServerTool(Name = "run_configuration")]
    [Description("Dispatches a run from a stored configuration id.")]
    public async Task<CallToolResult> RunConfigurationAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Configuration id (GUID).")] string configurationId,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } principalId)
            return NoPrincipal();
        if (!Guid.TryParse(configurationId, out var id))
            return Error("configurationId must be a GUID.");

        var config = await configurations.GetAsync(id, cancellationToken);
        if (config is null)
            return Error("configuration not found.");
        if (await DenyAsync(principalId, PermissionActions.WorkflowTrigger, config.WorkflowType, cancellationToken) is { } denial)
            return denial;

        try
        {
            var accepted = await runs.RunConfigurationAsync(id, null, cancellationToken);
            return JsonResult(new { runId = accepted.RunId, commandId = accepted.CommandId });
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "list_configurations")]
    [Description("Lists stored run configurations.")]
    public async Task<CallToolResult> ListConfigurationsAsync(
        RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is null)
            return NoPrincipal();
        return JsonResult(await configurations.QueryAsync(new ConfigurationQuery(), cancellationToken));
    }

    [McpServerTool(Name = "list_runs")]
    [Description("Lists runs, newest first.")]
    public async Task<CallToolResult> ListRunsAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Maximum runs to return (default 50, max 500).")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is null)
            return NoPrincipal();
        return JsonResult(await runView.QueryAsync(new RunQuery(Take: Math.Clamp(limit, 1, 500)), cancellationToken));
    }

    [McpServerTool(Name = "get_run")]
    [Description("Returns a run's status by id.")]
    public async Task<CallToolResult> GetRunAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Run id (GUID).")] string runId,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is null)
            return NoPrincipal();
        if (!Guid.TryParse(runId, out var id))
            return Error("runId must be a GUID.");
        var status = await runView.GetAsync(id, cancellationToken);
        return status is null ? Error("run not found.") : JsonResult(status);
    }

    [McpServerTool(Name = "list_connectors")]
    [Description("Lists connectors (setting keys only — secret values are never returned).")]
    public async Task<CallToolResult> ListConnectorsAsync(
        RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is null)
            return NoPrincipal();
        return JsonResult(await connectors.QueryAsync(new ConnectorQuery(), cancellationToken));
    }

    [McpServerTool(Name = "create_connector")]
    [Description("Creates a connector. Settings are a JSON object; values are stored encrypted.")]
    public async Task<CallToolResult> CreateConnectorAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Connector name.")] string name,
        [Description("Provider type.")] string providerType,
        [Description("JSON object of settings.")] string settingsJson,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } principalId)
            return NoPrincipal();
        if (await DenyAsync(principalId, PermissionActions.SlotConfigWrite, null, cancellationToken) is { } denial)
            return denial;
        var connector = await connectors.CreateAsync(
            new CreateConnector(name, providerType, ParseObject(settingsJson)), cancellationToken);
        return JsonResult(connector);
    }

    private async Task<CallToolResult?> DenyAsync(
        Guid principalId, string action, string? workflowType, CancellationToken ct)
    {
        var decision = await policyEngine.EvaluateAsync(
            new PolicyContext(principalId, action, workflowType ?? action) { WorkflowType = workflowType }, ct);
        return decision.Allowed ? null : Error($"{action} denied by policy: {decision.Reason}");
    }

    private static Dictionary<string, string> ParseObject(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();

    private static CallToolResult JsonResult(object payload) => new()
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
