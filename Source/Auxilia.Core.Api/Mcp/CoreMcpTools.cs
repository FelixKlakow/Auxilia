using System.ComponentModel;
using System.Text.Json;
using Auxilia.Core.Api;
using Auxilia.Core.Api.Auth;
using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;
using Auxilia.Governance;
using Auxilia.Governance.IdentityImport;
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
    AuditReadService audit,
    ConnectorService connectors,
    ProviderCatalogService providerCatalog,
    WorkflowSchemaReadService workflowSchemas,
    WorkflowTypeRegistryService workflowRegistry,
    WorkflowTypeApprovalPipeline approvalPipeline,
    GroupDirectory groups,
    GroupMappingDirectory groupMappings,
    IdentityImportService identityImport,
    PrincipalDirectory principals,
    PrincipalAdminService principalAdmin,
    Auxilia.UniversalDataAccess.IDataAccess<Data.CoreArtifactRecord> artifacts)
{
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    [McpServerTool(Name = "run_workflow")]
    [Description("Dispatches a run of a registered (Active) workflow type, on the fly. The Core resolves " +
                 "the signed package from its workflow-type registry.")]
    public async Task<CallToolResult> RunWorkflowAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Registered workflow type name.")] string workflowType,
        [Description("Optional JSON object of run context key/values.")] string? contextJson = null,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } principalId)
            return NoPrincipal();
        if (await DenyAsync(principalId, PermissionActions.WorkflowTrigger, workflowType, cancellationToken) is { } denial)
            return denial;

        try
        {
            var accepted = await runs.RunInlineAsync(
                new RunRequest(workflowType, ParseObject(contextJson)), principalId, cancellationToken);
            return JsonResult(new { runId = accepted.RunId, commandId = accepted.CommandId });
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "register_workflow_type")]
    [Description("Registers a workflow type into the Core registry (permanent until unregistered). A package " +
                 "signed by a trusted publisher activates immediately; anything else (or a docker:// image) " +
                 "enters Pending until the signing authority approves or denies it.")]
    public async Task<CallToolResult> RegisterWorkflowTypeAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Workflow type name (must match the packaged schema's workflow name).")] string workflowType,
        [Description("Package coordinate: docker://image:tag or an https .workflow.zip URL.")] string? packageUri = null,
        [Description("Alternatively, the package ZIP transferred as base64 (stored and served by the Core).")]
        string? packageBase64 = null,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } principalId)
            return NoPrincipal();
        if (await DenyAsync(principalId, PermissionActions.WorkflowTypeManage, null, cancellationToken) is { } denial)
            return denial;
        var outcome = await workflowRegistry.RegisterAsync(
            new RegisterWorkflowTypeRequest(workflowType, packageUri, packageBase64), principalId, cancellationToken);
        if (outcome.Registration is not { } registration)
            return Error(outcome.Error!);
        if (registration.Status == WorkflowTypeStatus.Pending)
            approvalPipeline.KickOff(registration.WorkflowType);
        return JsonResult(registration);
    }

    [McpServerTool(Name = "approve_workflow_type")]
    [Description("Signing authority: approves a pending workflow-type registration (a Core-stored package is " +
                 "re-signed with the platform key when configured). The type becomes Active and runnable.")]
    public async Task<CallToolResult> ApproveWorkflowTypeAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Workflow type name.")] string workflowType,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } principalId)
            return NoPrincipal();
        if (await DenyAsync(principalId, PermissionActions.WorkflowTypeSign, null, cancellationToken) is { } denial)
            return denial;
        var outcome = await workflowRegistry.ApproveAsync(workflowType, principalId.ToString("D"), cancellationToken);
        return outcome.Registration is { } registration ? JsonResult(registration) : Error(outcome.Error!);
    }

    [McpServerTool(Name = "deny_workflow_type")]
    [Description("Signing authority: denies a workflow-type registration with a recorded reason; the type is " +
                 "not runnable.")]
    public async Task<CallToolResult> DenyWorkflowTypeAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Workflow type name.")] string workflowType,
        [Description("Why the registration is refused.")] string reason,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } principalId)
            return NoPrincipal();
        if (await DenyAsync(principalId, PermissionActions.WorkflowTypeSign, null, cancellationToken) is { } denial)
            return denial;
        var outcome = await workflowRegistry.DenyAsync(
            workflowType, reason, principalId.ToString("D"), cancellationToken);
        return outcome.Registration is { } registration ? JsonResult(registration) : Error(outcome.Error!);
    }

    [McpServerTool(Name = "unregister_workflow_type")]
    [Description("Removes a workflow type from the registry (and its Core-stored package). It can no longer run.")]
    public async Task<CallToolResult> UnregisterWorkflowTypeAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Workflow type name.")] string workflowType,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } principalId)
            return NoPrincipal();
        if (await DenyAsync(principalId, PermissionActions.WorkflowTypeManage, null, cancellationToken) is { } denial)
            return denial;
        return await workflowRegistry.UnregisterAsync(workflowType, principalId, cancellationToken)
            ? JsonResult(new { workflowType, unregistered = true })
            : Error("workflow type is not registered.");
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

        var config = await configurations.GetAsync(id, ViewerOf(context), cancellationToken);
        if (config is null)
            return Error("configuration not found.");
        if (await DenyAsync(principalId, PermissionActions.WorkflowTrigger, config.WorkflowType, cancellationToken) is { } denial)
            return denial;

        try
        {
            var accepted = await runs.RunConfigurationAsync(id, principalId, null, cancellationToken);
            return JsonResult(new { runId = accepted.RunId, commandId = accepted.CommandId });
        }
        catch (ConnectorAccessDeniedException ex)
        {
            return Error(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "list_configurations")]
    [Description("Lists stored run configurations visible to you: company-scoped ones plus personal " +
                 "ones you own or were granted.")]
    public async Task<CallToolResult> ListConfigurationsAsync(
        RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is null)
            return NoPrincipal();
        return JsonResult(await configurations.QueryAsync(
            new ConfigurationQuery(), ViewerOf(context), cancellationToken));
    }

    [McpServerTool(Name = "set_configuration_grants")]
    [Description("Replaces a personal configuration's access grants (owner or a configuration manager). " +
                 "Grants is a JSON array of {kind,id}, kind = Principal, Group (first-class platform group) " +
                 "or DirectoryGroup.")]
    public async Task<CallToolResult> SetConfigurationGrantsAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Configuration id (GUID).")] string configurationId,
        [Description("JSON array, e.g. [{\"kind\":\"Group\",\"id\":\"<group-id>\"}].")] string grantsJson,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } principalId)
            return NoPrincipal();
        if (!Guid.TryParse(configurationId, out var id))
            return Error("configurationId must be a GUID.");
        if (!await configurations.IsOwnerAsync(id, principalId, cancellationToken)
            && await DenyAsync(principalId, PermissionActions.WorkflowConfigurationManage, null, cancellationToken) is { } denial)
            return denial;
        try
        {
            var grantList = JsonSerializer.Deserialize<List<AccessGrant>>(grantsJson, JsonOptions) ?? [];
            return await configurations.SetGrantsAsync(id, grantList, cancellationToken) is { } updated
                ? JsonResult(updated)
                : Error("configuration not found.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return Error(ex.Message);
        }
    }

    /// <summary>The visibility filter for the calling MCP principal (managers see everything).</summary>
    private static ConfigurationViewer ViewerOf(RequestContext<CallToolRequestParams> context) => new(
        CoreClaims.PrincipalIdOf(context.User),
        context.User is { } user && CoreClaims.HasRolePermission(user, PermissionActions.WorkflowConfigurationManage));

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

    [McpServerTool(Name = "cancel_run")]
    [Description("Requests cancellation of a running workflow by run id.")]
    public async Task<CallToolResult> CancelRunAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Run id (GUID).")] string runId,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } principalId)
            return NoPrincipal();
        if (!Guid.TryParse(runId, out var id))
            return Error("runId must be a GUID.");
        var run = await runView.GetAsync(id, cancellationToken);
        if (await DenyAsync(principalId, PermissionActions.WorkflowCancel, run?.WorkflowType, cancellationToken) is { } denial)
            return denial;
        await runs.CancelAsync(id, cancellationToken);
        return JsonResult(new { runId = id, cancelRequested = true });
    }

    [McpServerTool(Name = "list_artifacts")]
    [Description("Lists persisted-artifact metadata, newest first. Optional exact-match filters on " +
                 "artifact type and work item id — chaining decisions read this, payloads come via REST.")]
    public async Task<CallToolResult> ListArtifactsAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Exact artifact type, e.g. \"code-review-result\".")] string? artifactType = null,
        [Description("Exact work item id.")] string? workItemId = null,
        [Description("Maximum artifacts to return (default 50, max 500).")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } principalId)
            return NoPrincipal();
        if (await DenyAsync(principalId, PermissionActions.ArtifactConsume, artifactType, cancellationToken) is { } denial)
            return denial;
        var all = (await artifacts.ReadAsync(cancellationToken)).AsEnumerable();
        if (!string.IsNullOrWhiteSpace(artifactType))
            all = all.Where(a => string.Equals(a.ArtifactType, artifactType, StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(workItemId))
            all = all.Where(a => string.Equals(a.WorkItemId, workItemId, StringComparison.Ordinal));
        var page = all.OrderByDescending(a => a.CreatedUtc).Take(Math.Clamp(limit, 1, 500))
            .Select(a => new ArtifactDto(
                a.Id, a.ArtifactType, a.WorkflowType, a.WorkItemId, a.RunInstanceId,
                a.Version, a.ContentHash, a.SizeBytes, a.CreatedUtc)).ToList();
        return JsonResult(page);
    }

    [McpServerTool(Name = "get_artifact")]
    [Description("Returns one persisted artifact's metadata by id.")]
    public async Task<CallToolResult> GetArtifactAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Artifact id (GUID).")] string artifactId,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } principalId)
            return NoPrincipal();
        if (!Guid.TryParse(artifactId, out var id))
            return Error("artifactId must be a GUID.");
        if (await DenyAsync(principalId, PermissionActions.ArtifactConsume, artifactId, cancellationToken) is { } denial)
            return denial;
        var a = await artifacts.ReadAsync(id, cancellationToken);
        return a is null
            ? Error("artifact not found.")
            : JsonResult(new ArtifactDto(
                a.Id, a.ArtifactType, a.WorkflowType, a.WorkItemId, a.RunInstanceId,
                a.Version, a.ContentHash, a.SizeBytes, a.CreatedUtc));
    }

    [McpServerTool(Name = "read_audit")]
    [Description("Reads the Core's centralized audit log, newest first. Optional exact-match filters on " +
                 "actor, action, and subject, plus an inclusive-from/exclusive-to UTC time range.")]
    public async Task<CallToolResult> ReadAuditAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Maximum entries to return (default 100, max 500).")] int limit = 100,
        [Description("Optional exact match on the actor (principal id or service name).")] string? actor = null,
        [Description("Optional exact match on the action, e.g. workflow.trigger.")] string? action = null,
        [Description("Optional exact match on the subject (the resource acted on).")] string? subject = null,
        [Description("Optional inclusive lower bound on the UTC timestamp (ISO 8601).")] DateTimeOffset? fromUtc = null,
        [Description("Optional exclusive upper bound on the UTC timestamp (ISO 8601).")] DateTimeOffset? toUtc = null,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } principalId)
            return NoPrincipal();
        if (await DenyAsync(principalId, PermissionActions.AuditRead, null, cancellationToken) is { } denial)
            return denial;
        return JsonResult(await audit.QueryAsync(
            new AuditQuery(actor, action, subject, fromUtc, toUtc, Take: Math.Clamp(limit, 1, 500)),
            cancellationToken));
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
    [Description("Creates a connector. Settings are a JSON object; values are stored encrypted. " +
                 "Scope Personal makes it an identity-linked connector owned by you.")]
    public async Task<CallToolResult> CreateConnectorAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Connector name.")] string name,
        [Description("Provider type.")] string providerType,
        [Description("JSON object of settings.")] string settingsJson,
        [Description("Scope: Personal (identity-linked, owned by you — the default) or Company (shared platform-wide; requires the connector-management permission).")]
        string scope = ResourceScope.Personal,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } principalId)
            return NoPrincipal();
        // Company connectors require the connector-management permission; personal ones are self-owned.
        if (scope != ResourceScope.Personal
            && await DenyAsync(principalId, PermissionActions.SlotConfigWrite, null, cancellationToken) is { } denial)
            return denial;
        var connector = await connectors.CreateAsync(
            new CreateConnector(name, providerType, ParseObject(settingsJson), scope), principalId, cancellationToken);
        return JsonResult(connector);
    }

    [McpServerTool(Name = "set_connector_grants")]
    [Description("Replaces a personal connector's access grants (owner or a connector manager only). " +
                 "Grants is a JSON array of {kind,id}, kind = Principal, Group (first-class platform group) or DirectoryGroup.")]
    public async Task<CallToolResult> SetConnectorGrantsAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Connector id (GUID).")] string connectorId,
        [Description("JSON array, e.g. [{\"kind\":\"DirectoryGroup\",\"id\":\"<group-object-id>\"}].")] string grantsJson,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } principalId)
            return NoPrincipal();
        if (!Guid.TryParse(connectorId, out var id))
            return Error("connectorId must be a GUID.");
        if (await connectors.GetAsync(id, cancellationToken) is not { } connector)
            return Error("connector not found.");
        if (connector.OwnerPrincipalId != principalId
            && await DenyAsync(principalId, PermissionActions.SlotConfigWrite, null, cancellationToken) is { } denial)
            return denial;

        List<AccessGrant>? grants;
        try
        {
            grants = JsonSerializer.Deserialize<List<AccessGrant>>(grantsJson);
        }
        catch (JsonException)
        {
            return Error("grants must be a JSON array of {kind,id} objects.");
        }
        return await connectors.SetGrantsAsync(id, grants ?? [], cancellationToken)
            ? JsonResult(new { connectorId = id, grants = grants ?? [] })
            : Error("connector not found.");
    }

    [McpServerTool(Name = "list_provider_catalog")]
    [Description("Lists the slot-provider catalog: which registered providers are available (deny-by-default) " +
                 "for workflow configuration, their category, and curated setting descriptors.")]
    public async Task<CallToolResult> ListProviderCatalogAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Maximum entries to return (default 200, max 500).")] int limit = 200,
        [Description("Optional filter: true = only available providers, false = only hidden.")] bool? available = null,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } principalId)
            return NoPrincipal();
        if (await DenyAsync(principalId, PermissionActions.ProviderCatalogManage, null, cancellationToken) is { } denial)
            return denial;
        return JsonResult(await providerCatalog.QueryAsync(
            new ProviderCatalogQuery(available, Take: Math.Clamp(limit, 1, 500)), cancellationToken));
    }

    [McpServerTool(Name = "set_provider_availability")]
    [Description("Enables or disables a slot provider (deny-by-default): only available providers are " +
                 "offered when configuring workflows.")]
    public async Task<CallToolResult> SetProviderAvailabilityAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Provider type name.")] string providerType,
        [Description("True to make the provider available; false to hide it.")] bool available,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } principalId)
            return NoPrincipal();
        if (await DenyAsync(principalId, PermissionActions.ProviderCatalogManage, null, cancellationToken) is { } denial)
            return denial;
        try
        {
            return JsonResult(await providerCatalog.SetAvailabilityAsync(
                principalId.ToString("D"), providerType, available, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "set_provider_setting")]
    [Description("Disables or re-enables one manifest-declared setting of a provider: a disabled setting " +
                 "disappears from every editor and is no longer required.")]
    public async Task<CallToolResult> SetProviderSettingAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Provider type name.")] string providerType,
        [Description("Setting key declared by the provider manifest.")] string settingKey,
        [Description("True to disable the setting; false to re-enable it.")] bool disabled,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } principalId)
            return NoPrincipal();
        if (await DenyAsync(principalId, PermissionActions.ProviderCatalogManage, null, cancellationToken) is { } denial)
            return denial;
        try
        {
            return JsonResult(await providerCatalog.SetSettingDisabledAsync(
                principalId.ToString("D"), providerType, settingKey, disabled, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "list_workflow_types")]
    [Description("Lists the registered workflow types (id, version, lifecycle, tags) the Core has cataloged " +
                 "from the runner. Used to pick a workflow to configure.")]
    public async Task<CallToolResult> ListWorkflowTypesAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Maximum types to return (default 100, max 500).")] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } principalId)
            return NoPrincipal();
        if (await DenyAsync(principalId, PermissionActions.WorkflowConfigurationManage, null, cancellationToken) is { } denial)
            return denial;
        return JsonResult(await workflowSchemas.QueryTypesAsync(
            new WorkflowTypeQuery(Take: Math.Clamp(limit, 1, 500)), cancellationToken));
    }

    [McpServerTool(Name = "get_workflow_schema")]
    [Description("Returns a registered workflow type's full schema: declared slots and their capability " +
                 "requirements, run inputs, views, trigger kinds, and environment requirements.")]
    public async Task<CallToolResult> GetWorkflowSchemaAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Workflow type name.")] string workflowType,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } principalId)
            return NoPrincipal();
        if (await DenyAsync(principalId, PermissionActions.WorkflowConfigurationManage, null, cancellationToken) is { } denial)
            return denial;
        var schema = await workflowSchemas.GetSchemaAsync(workflowType, cancellationToken);
        return schema is null ? Error("workflow type not found.") : JsonResult(schema);
    }

    [McpServerTool(Name = "list_principals")]
    [Description("Lists principals (humans, AI agents, services) with their resolved roles. Optional filters: " +
                 "kind (Human/AiAgent/Service), enabled, and a name/subject substring search.")]
    public async Task<CallToolResult> ListPrincipalsAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Maximum principals to return (default 50, max 500).")] int limit = 50,
        [Description("Optional filter on kind: Human, AiAgent, or Service.")] string? kind = null,
        [Description("Optional filter: true = only enabled, false = only disabled.")] bool? enabled = null,
        [Description("Optional case-insensitive substring match on display name or external subject.")] string? search = null,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } principalId)
            return NoPrincipal();
        if (await DenyAsync(principalId, PermissionActions.PrincipalAdminister, null, cancellationToken) is { } denial)
            return denial;
        return JsonResult(await principalAdmin.QueryAsync(
            new PrincipalQuery(kind, enabled, search, Take: Math.Clamp(limit, 1, 500)), cancellationToken));
    }

    [McpServerTool(Name = "create_principal")]
    [Description("Creates a local human principal that signs in with a username and password. New principals " +
                 "hold only the roles later assigned to them (deny-by-default).")]
    public async Task<CallToolResult> CreatePrincipalAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Display name.")] string displayName,
        [Description("Sign-in username.")] string username,
        [Description("Initial password.")] string password,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } actor)
            return NoPrincipal();
        if (await DenyAsync(actor, PermissionActions.PrincipalAdminister, null, cancellationToken) is { } denial)
            return denial;
        var principal = await principals.CreateHumanAsync(displayName, username, password, cancellationToken);
        return JsonResult(PrincipalAdminService.ToDto(principal, []));
    }

    [McpServerTool(Name = "create_ai_principal")]
    [Description("Creates an AI or service principal that authenticates with an API key. The API key is " +
                 "returned exactly once here and is never retrievable again — capture it now.")]
    public async Task<CallToolResult> CreateAiPrincipalAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Display name.")] string displayName,
        [Description("Kind: AiAgent or Service.")] string kind = "AiAgent",
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } actor)
            return NoPrincipal();
        if (await DenyAsync(actor, PermissionActions.PrincipalAdminister, null, cancellationToken) is { } denial)
            return denial;
        try
        {
            var (principal, apiKey) = await principals.CreateApiKeyPrincipalAsync(displayName, kind, cancellationToken);
            return JsonResult(new CreatedApiKeyPrincipal(PrincipalAdminService.ToDto(principal, []), apiKey));
        }
        catch (ArgumentException ex)
        {
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "assign_principal_role")]
    [Description("Assigns a Direct built-in role to a principal (idempotent). Never affects roles held " +
                 "through group membership.")]
    public async Task<CallToolResult> AssignPrincipalRoleAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Principal id (GUID).")] string principalId,
        [Description("Role name (Administrator, Operator, User, Auditor).")] string roleName,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } actor)
            return NoPrincipal();
        if (await DenyAsync(actor, PermissionActions.PrincipalAdminister, null, cancellationToken) is { } denial)
            return denial;
        if (!Guid.TryParse(principalId, out var pid))
            return Error("principalId must be a GUID.");
        try
        {
            await principals.AssignRoleAsync(pid, roleName, cancellationToken);
            return JsonResult(new { principalId = pid, roleName, assigned = true });
        }
        catch (ArgumentException ex)
        {
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "revoke_principal_role")]
    [Description("Revokes a Direct role from a principal (idempotent). A role held only through a group is " +
                 "left untouched.")]
    public async Task<CallToolResult> RevokePrincipalRoleAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Principal id (GUID).")] string principalId,
        [Description("Role name to revoke.")] string roleName,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } actor)
            return NoPrincipal();
        if (await DenyAsync(actor, PermissionActions.PrincipalAdminister, null, cancellationToken) is { } denial)
            return denial;
        if (!Guid.TryParse(principalId, out var pid))
            return Error("principalId must be a GUID.");
        var removed = await principals.RevokeRoleAsync(pid, roleName, cancellationToken);
        return JsonResult(new { principalId = pid, roleName, revoked = removed });
    }

    [McpServerTool(Name = "set_principal_enabled")]
    [Description("Enables or disables a principal. A disabled principal can no longer authenticate.")]
    public async Task<CallToolResult> SetPrincipalEnabledAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Principal id (GUID).")] string principalId,
        [Description("True to enable, false to disable.")] bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } actor)
            return NoPrincipal();
        if (await DenyAsync(actor, PermissionActions.PrincipalAdminister, null, cancellationToken) is { } denial)
            return denial;
        if (!Guid.TryParse(principalId, out var pid))
            return Error("principalId must be a GUID.");
        return await principals.SetEnabledAsync(pid, enabled, cancellationToken)
            ? JsonResult(new { principalId = pid, enabled })
            : Error("principal not found.");
    }

    [McpServerTool(Name = "create_group")]
    [Description("Creates a first-class group (identity administration).")]
    public async Task<CallToolResult> CreateGroupAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Group name.")] string name,
        [Description("Optional description.")] string? description = null,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } principalId)
            return NoPrincipal();
        if (await DenyAsync(principalId, PermissionActions.PrincipalAdminister, null, cancellationToken) is { } denial)
            return denial;
        var group = await groups.CreateAsync(name, description, cancellationToken);
        return JsonResult(new { id = group.Id, name = group.Name });
    }

    [McpServerTool(Name = "list_groups")]
    [Description("Lists groups with their members and roles.")]
    public async Task<CallToolResult> ListGroupsAsync(
        RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } principalId)
            return NoPrincipal();
        if (await DenyAsync(principalId, PermissionActions.PrincipalAdminister, null, cancellationToken) is { } denial)
            return denial;
        var result = new List<object>();
        foreach (var g in await groups.ListAsync(cancellationToken))
            result.Add(new
            {
                id = g.Id,
                name = g.Name,
                members = await groups.MembersAsync(g.Id, cancellationToken),
                roles = await groups.RolesAsync(g.Id, cancellationToken)
            });
        return JsonResult(new { groups = result });
    }

    [McpServerTool(Name = "add_group_member")]
    [Description("Adds a principal to a group.")]
    public async Task<CallToolResult> AddGroupMemberAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Group id (GUID).")] string groupId,
        [Description("Principal id (GUID).")] string principalId,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } actor)
            return NoPrincipal();
        if (await DenyAsync(actor, PermissionActions.PrincipalAdminister, null, cancellationToken) is { } denial)
            return denial;
        if (!Guid.TryParse(groupId, out var gid) || !Guid.TryParse(principalId, out var pid))
            return Error("groupId and principalId must be GUIDs.");
        await groups.AddMemberAsync(gid, pid, cancellationToken);
        return JsonResult(new { groupId = gid, principalId = pid, added = true });
    }

    [McpServerTool(Name = "assign_group_role")]
    [Description("Grants a built-in role to every member of a group.")]
    public async Task<CallToolResult> AssignGroupRoleAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Group id (GUID).")] string groupId,
        [Description("Role name (Administrator, Operator, User, Auditor).")] string roleName,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } actor)
            return NoPrincipal();
        if (await DenyAsync(actor, PermissionActions.PrincipalAdminister, null, cancellationToken) is { } denial)
            return denial;
        if (!Guid.TryParse(groupId, out var gid))
            return Error("groupId must be a GUID.");
        try
        {
            await groups.AssignRoleAsync(gid, roleName, cancellationToken);
            return JsonResult(new { groupId = gid, roleName, assigned = true });
        }
        catch (ArgumentException ex)
        {
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "list_group_mappings")]
    [Description("Lists directory group→role mappings applied to federated (Entra) sign-ins.")]
    public async Task<CallToolResult> ListGroupMappingsAsync(
        RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } principalId)
            return NoPrincipal();
        if (await DenyAsync(principalId, PermissionActions.IdentitySourceManage, null, cancellationToken) is { } denial)
            return denial;
        var mappings = (await groupMappings.ListAsync(cancellationToken))
            .Select(m => new { id = m.Id, identityProvider = m.IdentityProvider, groupClaim = m.GroupClaim, roleName = m.RoleName });
        return JsonResult(new { groupMappings = mappings });
    }

    [McpServerTool(Name = "create_group_mapping")]
    [Description("Maps an identity-provider group claim to a built-in role granted at sign-in.")]
    public async Task<CallToolResult> CreateGroupMappingAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Identity provider name (e.g. entra).")] string identityProvider,
        [Description("Group claim value (the directory group's object id).")] string groupClaim,
        [Description("Role name (Administrator, Operator, User, Auditor).")] string roleName,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } principalId)
            return NoPrincipal();
        if (await DenyAsync(principalId, PermissionActions.IdentitySourceManage, null, cancellationToken) is { } denial)
            return denial;
        try
        {
            var mapping = await groupMappings.CreateAsync(identityProvider, groupClaim, roleName, cancellationToken);
            return JsonResult(new { id = mapping.Id, identityProvider, groupClaim, roleName });
        }
        catch (ArgumentException ex)
        {
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "remove_group_mapping")]
    [Description("Removes a directory group→role mapping by id.")]
    public async Task<CallToolResult> RemoveGroupMappingAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Group-mapping id (GUID).")] string mappingId,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } principalId)
            return NoPrincipal();
        if (await DenyAsync(principalId, PermissionActions.IdentitySourceManage, null, cancellationToken) is { } denial)
            return denial;
        if (!Guid.TryParse(mappingId, out var id))
            return Error("mappingId must be a GUID.");
        return await groupMappings.RemoveAsync(id, cancellationToken)
            ? JsonResult(new { id, removed = true })
            : Error("group mapping not found.");
    }

    [McpServerTool(Name = "list_identity_connectors")]
    [Description("Lists the available identity-import connector types (e.g. ldap, csv) and the settings each accepts.")]
    public async Task<CallToolResult> ListIdentityConnectorsAsync(
        RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } principalId)
            return NoPrincipal();
        if (await DenyAsync(principalId, PermissionActions.IdentitySourceManage, null, cancellationToken) is { } denial)
            return denial;
        return JsonResult(new { connectors = identityImport.Connectors.Select(c => c.ToDescriptorDto()) });
    }

    [McpServerTool(Name = "list_identity_sources")]
    [Description("Lists configured identity sources (bulk user provisioning from LDAP/AD or CSV). Secret setting values are never returned.")]
    public async Task<CallToolResult> ListIdentitySourcesAsync(
        RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } principalId)
            return NoPrincipal();
        if (await DenyAsync(principalId, PermissionActions.IdentitySourceManage, null, cancellationToken) is { } denial)
            return denial;
        return JsonResult(new { sources = (await identityImport.ListAsync(cancellationToken)).Select(s => s.ToDto()) });
    }

    [McpServerTool(Name = "save_identity_source")]
    [Description("Creates or updates an identity source. settingsJson and groupRoleMappingsJson are JSON objects. " +
                 "To edit an existing source, pass its current name as existingName (the name is immutable). " +
                 "Secret settings are write-only; send an empty value to keep the stored secret.")]
    public async Task<CallToolResult> SaveIdentitySourceAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Source name (immutable natural key).")] string name,
        [Description("Connector type, e.g. ldap or csv.")] string connectorType,
        [Description("JSON object of connector settings.")] string settingsJson,
        [Description("Default role granted to every imported user (empty for none).")] string defaultRole = "",
        [Description("JSON object mapping external group → role name.")] string? groupRoleMappingsJson = null,
        [Description("Disable users that disappear from the source (never deletes).")] bool disableMissing = false,
        [Description("When editing, the current source name; null to create.")] string? existingName = null,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } principalId)
            return NoPrincipal();
        if (await DenyAsync(principalId, PermissionActions.IdentitySourceManage, null, cancellationToken) is { } denial)
            return denial;
        var request = new SaveIdentitySourceRequest(
            name, connectorType, ParseObject(settingsJson), defaultRole,
            ParseObject(groupRoleMappingsJson), disableMissing, existingName);
        try
        {
            var saved = await identityImport.SaveAsync(principalId.ToString("D"), request.ToDraft(), cancellationToken);
            return JsonResult(saved.ToDto());
        }
        catch (ArgumentException ex)
        {
            return Error(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "test_identity_source")]
    [Description("Verifies a configured identity source is reachable and reports how many users it would import.")]
    public async Task<CallToolResult> TestIdentitySourceAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Identity source id (GUID).")] string sourceId,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } principalId)
            return NoPrincipal();
        if (await DenyAsync(principalId, PermissionActions.IdentitySourceManage, null, cancellationToken) is { } denial)
            return denial;
        if (!Guid.TryParse(sourceId, out var id))
            return Error("sourceId must be a GUID.");
        try
        {
            return JsonResult((await identityImport.TestConnectionAsync(id, cancellationToken)).ToDto());
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "import_identity_source")]
    [Description("Runs the import for a source: an idempotent upsert of principals into the Core identity store " +
                 "(re-import updates in place, never duplicates; missing users are at most disabled). Returns the run summary.")]
    public async Task<CallToolResult> ImportIdentitySourceAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Identity source id (GUID).")] string sourceId,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } principalId)
            return NoPrincipal();
        if (await DenyAsync(principalId, PermissionActions.IdentitySourceManage, null, cancellationToken) is { } denial)
            return denial;
        if (!Guid.TryParse(sourceId, out var id))
            return Error("sourceId must be a GUID.");
        try
        {
            var summary = await identityImport.ImportAsync(principalId.ToString("D"), id, cancellationToken);
            return JsonResult(summary.ToDto());
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "delete_identity_source")]
    [Description("Deletes an identity source configuration by id; principals it imported stay untouched.")]
    public async Task<CallToolResult> DeleteIdentitySourceAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Identity source id (GUID).")] string sourceId,
        CancellationToken cancellationToken = default)
    {
        if (CoreClaims.PrincipalIdOf(context.User) is not { } principalId)
            return NoPrincipal();
        if (await DenyAsync(principalId, PermissionActions.IdentitySourceManage, null, cancellationToken) is { } denial)
            return denial;
        if (!Guid.TryParse(sourceId, out var id))
            return Error("sourceId must be a GUID.");
        return await identityImport.DeleteAsync(principalId.ToString("D"), id, cancellationToken)
            ? JsonResult(new { id, removed = true })
            : Error("identity source not found.");
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
