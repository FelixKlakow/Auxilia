using Auxilia.Core.Api;
using Auxilia.Core.Api.Auth;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;
using Auxilia.Governance;
using Auxilia.Governance.Identity;
using Auxilia.Governance.IdentityImport;
using Auxilia.Governance.Policy;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// --- Messaging ---
builder.Services.AddSingleton<IMessageBusClient>(_ =>
    RabbitMqClient.CreateAsync(
        builder.Configuration["RabbitMq:Host"] ?? "localhost",
        int.Parse(builder.Configuration["RabbitMq:Port"] ?? "5672"),
        builder.Configuration["RabbitMq:UserName"] ?? "guest",
        builder.Configuration["RabbitMq:Password"] ?? "guest").GetAwaiter().GetResult());

// --- Core database (isolated from every other service — Principle 4) ---
var platformData = new PlatformDataSettings();
builder.Configuration.GetSection("PlatformData").Bind(platformData);
builder.Services.AddSingleton(platformData);
builder.Services.AddSettingsProtection(platformData);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddPlatformEntity<CoreRunConfigurationRecord>(platformData);
builder.Services.AddPlatformEntity<CoreConnectorRecord>(platformData);
builder.Services.AddPlatformEntity<CoreRunRecord>(platformData);
builder.Services.AddPlatformEntity<CoreRunResolutionRecord>(platformData);
builder.Services.AddPlatformEntity<CoreWorkflowSchemaRecord>(platformData);
builder.Services.AddPlatformEntity<DelegatedUserTokenRecord>(platformData);
builder.Services.AddPlatformEntity<AuditRecord>(platformData);
// Provider catalog (Core-owned governance): the registered slot-handler plugins and their curation.
builder.Services.AddPlatformEntity<SlotProviderRecord>(platformData);
builder.Services.AddPlatformEntity<ProviderCatalogRecord>(platformData);
builder.Services.AddSingleton<AuditLog>();

// --- Governance: identity, RBAC, Policy Engine (the Core is the auth + audit authority) ---
var governanceSettings = new GovernanceSettings();
builder.Configuration.GetSection("Governance").Bind(governanceSettings);
builder.Services.AddGovernance(platformData, governanceSettings);
builder.Services.AddSingleton<CoreSecurityBootstrap>();
builder.Services.Configure<CoreSecuritySettings>(builder.Configuration.GetSection("CoreSecurity"));

// --- Authentication / authorization: API-key bearer (REST + MCP) + interactive OIDC/cookie ---
builder.Services.AddCoreAuthentication(builder.Configuration);

// --- Settings ---
builder.Services.Configure<CoreApiSettings>(builder.Configuration.GetSection("CoreApi"));

// --- Rate limiting: bound slot-credential resolution per run (defence against a compromised runner) ---
var coreApiRateSettings = builder.Configuration.GetSection("CoreApi").Get<CoreApiSettings>() ?? new CoreApiSettings();
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("resolve-slot", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Request.Headers["X-Resolution-Token"].ToString() is { Length: > 0 } token
                ? token : "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = coreApiRateSettings.ResolutionRateLimitPermitsPerMinute,
                Window = TimeSpan.FromMinutes(1)
            }));
    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        var runId = context.HttpContext.Request.RouteValues.TryGetValue("runId", out var r) ? r?.ToString() : null;
        await context.HttpContext.RequestServices.GetRequiredService<AuditLog>().AppendAsync(
            "core-api", "workflow.slot-credential.rate-limited", runId ?? "unknown", "rate-limit-exceeded", ct: ct);
    };
});

// --- Core services ---
builder.Services.AddSingleton<ConnectorService>();
builder.Services.AddSingleton<ConnectorAccessPolicy>();
builder.Services.AddSingleton<DelegatedTokenStore>();
builder.Services.AddSingleton<RunConfigurationService>();
builder.Services.AddSingleton<RunService>();
builder.Services.AddSingleton<RunReadService>();
builder.Services.AddSingleton<AuditReadService>();
builder.Services.AddSingleton<ProviderCatalogService>();
builder.Services.AddSingleton<WorkflowSchemaReadService>();
builder.Services.AddSingleton<PrincipalAdminService>();
builder.Services.AddSingleton<SlotCredentialResolver>();
builder.Services.AddSingleton<RunStreamBroker>();
builder.Services.AddSingleton<Auxilia.Workflows.Messaging.WorkflowStatusPublisher>();
builder.Services.AddSingleton<RunnerLivenessTracker>();
builder.Services.AddSingleton<FailoverMonitor>();
builder.Services.AddHostedService<RunTrackingService>();
builder.Services.AddHostedService<WorkflowSchemaTrackingService>();
builder.Services.AddHostedService<RunStreamPublisher>();
// Resolve the same FailoverMonitor instance for the hosted lifecycle (so tests can drive ScanOnceAsync).
builder.Services.AddHostedService(sp => sp.GetRequiredService<FailoverMonitor>());

// --- MCP: first-class, authenticated AI/service parity ---
builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<Auxilia.Core.Api.Mcp.CoreMcpTools>();

var app = builder.Build();

// Bootstrap governance (human admin from config, if any) and the Core API-key principal.
await app.Services.GetRequiredService<GovernanceSeeder>().SeedAsync(app.Lifetime.ApplicationStopping);
{
    var security = app.Services.GetRequiredService<IOptions<CoreSecuritySettings>>().Value;
    await app.Services.GetRequiredService<CoreSecurityBootstrap>()
        .EnsureAsync(security, app.Lifetime.ApplicationStopping);

    var configurations = app.Services.GetRequiredService<RunConfigurationService>();
    var coreSettings = app.Services.GetRequiredService<IOptions<CoreApiSettings>>().Value;
    foreach (var seed in coreSettings.StaticConfigurations)
        await configurations.EnsureAsync(seed, app.Lifetime.ApplicationStopping);
}

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// --- Interactive sign-in (Entra OIDC) + browser session; local password + API-key auth coexist ---
app.MapGet("/auth/login", (string? returnUrl, IOptions<OidcSettings> oidc) =>
    oidc.Value.Enabled
        ? Results.Challenge(
            new AuthenticationProperties
            {
                RedirectUri = "/auth/callback" +
                    (string.IsNullOrEmpty(returnUrl) ? "" : $"?returnUrl={Uri.EscapeDataString(returnUrl)}")
            },
            [AuthSchemes.Oidc])
        : Results.NotFound(new { error = "interactive sign-in is not configured" }));

// Exchanges the validated external identity (signed into the temporary external cookie by the OIDC
// handler) for a provisioned Core principal and issues the browser session cookie.
app.MapGet("/auth/callback", async (
        string? returnUrl, HttpContext http, IAuthenticationSchemeProvider schemes,
        ExternalIdentityProvisioner provisioner, IDirectoryGroupResolver directoryGroups,
        DelegatedTokenStore delegatedTokens, TimeProvider clock,
        IOptions<OidcSettings> oidc, CancellationToken ct) =>
{
    if (await schemes.GetSchemeAsync(AuthSchemes.ExternalCookie) is null)
        return Results.NotFound(new { error = "interactive sign-in is not configured" });

    var external = await http.AuthenticateAsync(AuthSchemes.ExternalCookie);
    if (!external.Succeeded || external.Principal is null)
        return Results.Unauthorized();

    var identity = CoreClaims.ExternalIdentityFromPrincipal(external.Principal, oidc.Value.ProviderName);
    if (CoreClaims.HasGroupOverage(external.Principal))
    {
        // Too many groups for the token to carry them — read the real set from the directory.
        var accessToken = external.Properties?.GetTokenValue("access_token");
        identity = identity with { Groups = await directoryGroups.GetGroupIdsAsync(identity.Subject, accessToken, ct) };
    }
    var session = await provisioner.ProvisionAsync(identity, ct);
    if (session is null)
        return Results.Json(new { error = "the account is not permitted to sign in" },
            statusCode: StatusCodes.Status403Forbidden);

    await http.SignInAsync(AuthSchemes.Cookie, CoreClaims.ToClaimsPrincipal(session, AuthSchemes.Cookie));

    // Session-lifetime OBO: retain the user's access token (encrypted) so a workflow can act
    // on-behalf-of them until it expires. Opt-in; no refresh token is ever kept.
    if (oidc.Value.EnableDelegation
        && external.Properties?.GetTokenValue("access_token") is { Length: > 0 } userAccessToken)
    {
        var expiresUtc = DateTimeOffset.TryParse(external.Properties.GetTokenValue("expires_at"), out var exp)
            ? exp
            : clock.GetUtcNow().AddHours(1);
        await delegatedTokens.RetainAsync(session.PrincipalId, userAccessToken, expiresUtc, ct);
    }

    await http.SignOutAsync(AuthSchemes.ExternalCookie);

    var target = returnUrl is { Length: > 0 } && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//")
        ? returnUrl : "/";
    return Results.LocalRedirect(target);
});

app.MapPost("/auth/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(AuthSchemes.Cookie);
    return Results.Ok();
}).RequireAuthorization();

// Mints a short-lived per-user bearer for a delegated console (Auxilia.AdminConsole) to call the Core
// AS the signed-in user. Requires a live interactive cookie session (never an API key / another bearer),
// so a service principal cannot self-issue a user-scoped token. The token binds only the principal id +
// an expiry; roles are re-resolved server-side per request.
app.MapPost("/auth/token", (HttpContext http, UserBearerTokenService tokens) =>
{
    if (CoreClaims.PrincipalIdOf(http.User) is not { } principalId)
        return Results.Unauthorized();
    var (token, expiresUtc) = tokens.Issue(principalId);
    return Results.Ok(new UserBearerToken(token, expiresUtc));
}).RequireAuthorization(CoreAuthExtensions.CookieSessionPolicy);

app.MapGet("/auth/me", (HttpContext http) =>
    CoreClaims.PrincipalIdOf(http.User) is { } principalId
        ? Results.Ok(new CurrentPrincipal(
            principalId,
            http.User.Identity?.Name,
            http.User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray()))
        : Results.Unauthorized()).RequireAuthorization();

// --- Runs ---
app.MapPost("/api/runs", async (
        RunRequest request, HttpContext http, IPolicyEngine policy, RunService runs, AuditLog audit,
        CancellationToken ct) =>
{
    if (CoreClaims.PrincipalIdOf(http.User) is not { } principalId)
        return Results.Unauthorized();

    // On-behalf-of: a service/automation caller may dispatch a run AS a target principal. The caller
    // must hold run.on-behalf-of, and the target must itself pass the normal workflow.trigger gate —
    // the delegation grants no capability the target lacks. When no distinct target is named the
    // caller is both subject and triggeringPrincipal, exactly as a direct manual run.
    var onBehalfOf = request.RequestedBy is { } requestedBy && requestedBy != principalId;
    var triggeringPrincipal = onBehalfOf ? request.RequestedBy!.Value : principalId;

    if (onBehalfOf)
    {
        var delegation = await policy.EvaluateAsync(
            new PolicyContext(principalId, PermissionActions.RunOnBehalfOf, triggeringPrincipal.ToString()), ct);
        if (!delegation.Allowed)
        {
            await audit.AppendAsync(principalId.ToString(), PermissionActions.RunOnBehalfOf,
                triggeringPrincipal.ToString(), "denied", delegation.Reason, ct);
            return Results.Json(new { error = delegation.Reason }, statusCode: StatusCodes.Status403Forbidden);
        }
    }

    var decision = await policy.EvaluateAsync(
        new PolicyContext(triggeringPrincipal, PermissionActions.WorkflowTrigger, request.WorkflowType)
        { WorkflowType = request.WorkflowType }, ct);
    if (!decision.Allowed)
        return Results.Json(new { error = decision.Reason }, statusCode: StatusCodes.Status403Forbidden);
    try
    {
        var accepted = await runs.RunInlineAsync(request, triggeringPrincipal, ct);
        if (onBehalfOf)
            await audit.AppendAsync(principalId.ToString(), PermissionActions.RunOnBehalfOf,
                triggeringPrincipal.ToString(), "granted",
                $"{{\"workflowType\":\"{request.WorkflowType}\",\"runId\":\"{accepted.RunId}\"}}", ct);
        return Results.Ok(accepted);
    }
    catch (ConnectorAccessDeniedException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
    }
}).RequireAuthorization();

app.MapGet("/api/runs", async (
        string? state, string? workflowType, Guid? configurationId,
        RunReadService svc, CancellationToken ct, int skip = 0, int take = 50) =>
    Results.Ok(await svc.QueryAsync(
        new RunQuery(state, workflowType, configurationId, skip, take == 0 ? 50 : take), ct)))
    .RequireAuthorization();

app.MapGet("/api/runs/{id:guid}", async (Guid id, RunReadService svc, CancellationToken ct) =>
        await svc.GetAsync(id, ct) is { } status ? Results.Ok(status) : Results.NotFound())
    .RequireAuthorization();

app.MapPost("/api/runs/{id:guid}/cancel", async (
        Guid id, HttpContext http, IPolicyEngine policy, RunService runs, RunReadService runView,
        CancellationToken ct) =>
{
    if (CoreClaims.PrincipalIdOf(http.User) is not { } principalId)
        return Results.Unauthorized();
    var run = await runView.GetAsync(id, ct);
    var decision = await policy.EvaluateAsync(
        new PolicyContext(principalId, PermissionActions.WorkflowCancel, id.ToString())
        { WorkflowType = run?.WorkflowType }, ct);
    if (!decision.Allowed)
        return Results.Json(new { error = decision.Reason }, statusCode: StatusCodes.Status403Forbidden);
    await runs.CancelAsync(id, ct);
    return Results.Accepted($"/api/runs/{id}");
}).RequireAuthorization();

// Live-view stream (SSE): status transitions + view items for a run, fanned from the bus via the
// RunStreamBroker, until the run reaches a terminal state or the client disconnects. Replaces the
// BackendService SignalR /hubs/views live push.
app.MapGet("/api/runs/{id:guid}/stream", async (
        Guid id, HttpContext http, IPolicyEngine policy, RunStreamBroker broker, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.RunObserve, ct) is { } fail)
    {
        await fail.ExecuteAsync(http);
        return;
    }

    http.Response.Headers.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";
    http.Response.Headers["X-Accel-Buffering"] = "no";

    using var subscription = broker.Subscribe(id);
    // Flush headers so the client's SendAsync completes with the subscription already registered —
    // no live event published after this point is lost.
    await http.Response.Body.FlushAsync(ct);

    try
    {
        await foreach (var evt in subscription.Reader.ReadAllAsync(ct))
        {
            await http.Response.WriteAsync(
                $"data: {System.Text.Json.JsonSerializer.Serialize(evt, System.Text.Json.JsonSerializerOptions.Web)}\n\n", ct);
            await http.Response.Body.FlushAsync(ct);
            if (evt.Kind == RunStreamEvent.StatusKind && CoreRunStates.IsTerminalStatus(evt.PayloadJson))
                break;
        }
    }
    catch (OperationCanceledException)
    {
        // Client disconnected — expected end of an SSE stream.
    }
}).RequireAuthorization();

// --- Internal: runner <-> Core just-in-time slot-credential resolution ---
// Authorized by the run-scoped resolution token (header), NOT a principal API key — the runner
// can resolve only slots of runs the Core dispatched to it. Secrets are resolved and encrypted
// here; only ciphertext is returned.
app.MapPost("/internal/runs/{runId:guid}/resolve-slot", async (
        Guid runId, ResolveSlotRequest request, HttpContext http,
        SlotCredentialResolver resolver, CancellationToken ct) =>
{
    var token = http.Request.Headers["X-Resolution-Token"].ToString();
    if (string.IsNullOrEmpty(token))
        return Results.Json(new { error = "missing resolution token" }, statusCode: StatusCodes.Status401Unauthorized);
    var (success, error, credential) = await resolver.ResolveAsync(runId, token, request.SlotName, request.PublicKey, ct);
    return success
        ? Results.Ok(credential)
        : Results.Json(new { error }, statusCode:
            error == "invalid resolution token" ? StatusCodes.Status403Forbidden : StatusCodes.Status404NotFound);
}).RequireRateLimiting("resolve-slot");

// --- Configurations ---
app.MapPost("/api/configurations", async (
        CreateRunConfiguration request, RunConfigurationService svc, CancellationToken ct) =>
    Results.Ok(await svc.CreateAsync(request, ct)))
    .RequireAuthorization();

app.MapGet("/api/configurations", async (
        string? workflowType, bool? enabled,
        RunConfigurationService svc, CancellationToken ct, int skip = 0, int take = 50) =>
    Results.Ok(await svc.QueryAsync(
        new ConfigurationQuery(workflowType, enabled, skip, take == 0 ? 50 : take), ct)))
    .RequireAuthorization();

app.MapGet("/api/configurations/{id:guid}", async (
        Guid id, RunConfigurationService svc, CancellationToken ct) =>
        await svc.GetAsync(id, ct) is { } config ? Results.Ok(config) : Results.NotFound())
    .RequireAuthorization();

app.MapPost("/api/configurations/{id:guid}/run", async (
        Guid id, Guid? onBehalfOf, [FromBody] IReadOnlyDictionary<string, string>? context,
        HttpContext http, IPolicyEngine policy, RunService runs,
        RunConfigurationService configurations, AuditLog audit, CancellationToken ct) =>
{
    if (CoreClaims.PrincipalIdOf(http.User) is not { } principalId)
        return Results.Unauthorized();
    var config = await configurations.GetAsync(id, ct);
    if (config is null)
        return Results.NotFound();

    // On-behalf-of: a service/automation caller (WorkflowStudio's triggers) may dispatch a stored
    // configuration AS a target principal. The caller must hold run.on-behalf-of, and the target must
    // itself pass workflow.trigger — the delegation grants no capability the target lacks. Absent a
    // distinct target the caller is both subject and triggering principal, exactly as a manual run.
    var delegated = onBehalfOf is { } requestedBy && requestedBy != principalId;
    var triggeringPrincipal = delegated ? onBehalfOf!.Value : principalId;

    if (delegated)
    {
        var delegation = await policy.EvaluateAsync(
            new PolicyContext(principalId, PermissionActions.RunOnBehalfOf, triggeringPrincipal.ToString()), ct);
        if (!delegation.Allowed)
        {
            await audit.AppendAsync(principalId.ToString(), PermissionActions.RunOnBehalfOf,
                triggeringPrincipal.ToString(), "denied", delegation.Reason, ct);
            return Results.Json(new { error = delegation.Reason }, statusCode: StatusCodes.Status403Forbidden);
        }
    }

    var decision = await policy.EvaluateAsync(
        new PolicyContext(triggeringPrincipal, PermissionActions.WorkflowTrigger, config.WorkflowType)
        { WorkflowType = config.WorkflowType }, ct);
    if (!decision.Allowed)
        return Results.Json(new { error = decision.Reason }, statusCode: StatusCodes.Status403Forbidden);
    try
    {
        var accepted = await runs.RunConfigurationAsync(id, triggeringPrincipal, context, ct);
        if (delegated)
            await audit.AppendAsync(principalId.ToString(), PermissionActions.RunOnBehalfOf,
                triggeringPrincipal.ToString(), "granted",
                $"{{\"configurationId\":\"{id}\",\"runId\":\"{accepted.RunId}\"}}", ct);
        return Results.Ok(accepted);
    }
    catch (ConnectorAccessDeniedException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization();

// --- Audit (read-only; the Core owns the centralized audit log) ---
app.MapGet("/api/audit", async (
        string? actor, string? action, string? subject,
        DateTimeOffset? fromUtc, DateTimeOffset? toUtc,
        HttpContext http, IPolicyEngine policy, AuditReadService svc,
        CancellationToken ct, int skip = 0, int take = 50) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.AuditRead, ct) is { } fail)
        return fail;
    return Results.Ok(await svc.QueryAsync(
        new AuditQuery(actor, action, subject, fromUtc, toUtc, skip, take == 0 ? 50 : take), ct));
}).RequireAuthorization();

// --- Provider catalog (Core-owned governance: which slot providers may be configured; deny-by-default) ---
// Availability gates whether a provider is OFFERED in configuration editors — it is a configuration-time
// allowlist, not a dispatch gate (a run is not rejected for using an unavailable provider), matching the
// BackendService semantics this was migrated from.
app.MapGet("/api/provider-catalog", async (
        bool? available, HttpContext http, IPolicyEngine policy, ProviderCatalogService svc,
        CancellationToken ct, int skip = 0, int take = 50) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.ProviderCatalogManage, ct) is { } fail)
        return fail;
    return Results.Ok(await svc.QueryAsync(
        new ProviderCatalogQuery(available, skip, take == 0 ? 50 : take), ct));
}).RequireAuthorization();

app.MapPost("/api/provider-catalog/{providerType}/availability", async (
        string providerType, SetProviderAvailability request, HttpContext http, IPolicyEngine policy,
        ProviderCatalogService svc, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.ProviderCatalogManage, ct) is { } fail)
        return fail;
    try
    {
        return Results.Ok(await svc.SetAvailabilityAsync(
            CoreClaims.PrincipalIdOf(http.User)!.Value.ToString("D"), providerType, request.Available, ct));
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
}).RequireAuthorization();

app.MapPost("/api/provider-catalog/{providerType}/settings", async (
        string providerType, SetProviderSetting request, HttpContext http, IPolicyEngine policy,
        ProviderCatalogService svc, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.ProviderCatalogManage, ct) is { } fail)
        return fail;
    try
    {
        return Results.Ok(await svc.SetSettingDisabledAsync(
            CoreClaims.PrincipalIdOf(http.User)!.Value.ToString("D"),
            providerType, request.SettingKey, request.Disabled, ct));
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization();

// --- Workflow types + schemas (Core-owned schema registry, mirrored from the runner over the bus) ---
// The config editor reads these to drive "pick a workflow → bind its slots to connectors". Gated by
// workflow-configuration.manage — the permission held by operators/admins who build configurations.
app.MapGet("/api/workflow-types", async (
        HttpContext http, IPolicyEngine policy, WorkflowSchemaReadService svc,
        CancellationToken ct, int skip = 0, int take = 50) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.WorkflowConfigurationManage, ct) is { } fail)
        return fail;
    return Results.Ok(await svc.QueryTypesAsync(new WorkflowTypeQuery(skip, take == 0 ? 50 : take), ct));
}).RequireAuthorization();

app.MapGet("/api/workflow-types/{type}/schema", async (
        string type, HttpContext http, IPolicyEngine policy, WorkflowSchemaReadService svc, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.WorkflowConfigurationManage, ct) is { } fail)
        return fail;
    return await svc.GetSchemaAsync(type, ct) is { } schema ? Results.Ok(schema) : Results.NotFound();
}).RequireAuthorization();

// --- Connectors ---
app.MapPost("/api/connectors", async (
        CreateConnector request, HttpContext http, IPolicyEngine policy, ConnectorService svc, CancellationToken ct) =>
{
    if (CoreClaims.PrincipalIdOf(http.User) is not { } principalId)
        return Results.Unauthorized();
    // Any authenticated principal may connect their own personal (identity-linked) account; a shared
    // company connector requires the connector-management permission.
    if (request.Scope != ConnectorScope.Personal
        && await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.SlotConfigWrite, ct) is { } fail)
        return fail;
    return Results.Ok(await svc.CreateAsync(request, principalId, ct));
}).RequireAuthorization();

app.MapGet("/api/connectors", async (
        string? providerType, ConnectorService svc, CancellationToken ct, int skip = 0, int take = 50) =>
    Results.Ok(await svc.QueryAsync(new ConnectorQuery(providerType, skip, take == 0 ? 50 : take), ct)))
    .RequireAuthorization();

app.MapGet("/api/connectors/{id:guid}", async (Guid id, ConnectorService svc, CancellationToken ct) =>
        await svc.GetAsync(id, ct) is { } connector ? Results.Ok(connector) : Results.NotFound())
    .RequireAuthorization();

// Grant a personal connector to principals / directory groups (owner, or a connector manager).
app.MapPost("/api/connectors/{id:guid}/grants", async (
        Guid id, SetConnectorGrants request, HttpContext http, IPolicyEngine policy,
        ConnectorService svc, CancellationToken ct) =>
{
    if (CoreClaims.PrincipalIdOf(http.User) is not { } principalId)
        return Results.Unauthorized();
    if (await svc.GetAsync(id, ct) is not { } connector)
        return Results.NotFound();
    if (connector.OwnerPrincipalId != principalId
        && await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.SlotConfigWrite, ct) is { } fail)
        return fail;
    return await svc.SetGrantsAsync(id, request.Grants, ct)
        ? Results.Accepted($"/api/connectors/{id}")
        : Results.NotFound();
}).RequireAuthorization();

// --- Groups (identity administration) ---
app.MapPost("/api/groups", async (
        CreateGroupRequest request, HttpContext http, IPolicyEngine policy,
        GroupDirectory groups, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.PrincipalAdminister, ct) is { } fail)
        return fail;
    var group = await groups.CreateAsync(request.Name, request.Description, ct);
    return Results.Ok(new GroupDto(group.Id, group.Name, group.Description, [], []));
}).RequireAuthorization();

app.MapGet("/api/groups", async (
        HttpContext http, IPolicyEngine policy, GroupDirectory groups, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.PrincipalAdminister, ct) is { } fail)
        return fail;
    var result = new List<GroupDto>();
    foreach (var g in await groups.ListAsync(ct))
        result.Add(new GroupDto(g.Id, g.Name, g.Description,
            await groups.MembersAsync(g.Id, ct), await groups.RolesAsync(g.Id, ct)));
    return Results.Ok(result);
}).RequireAuthorization();

app.MapPost("/api/groups/{id:guid}/members", async (
        Guid id, AddGroupMemberRequest request, HttpContext http, IPolicyEngine policy,
        GroupDirectory groups, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.PrincipalAdminister, ct) is { } fail)
        return fail;
    await groups.AddMemberAsync(id, request.PrincipalId, ct);
    return Results.Accepted($"/api/groups/{id}");
}).RequireAuthorization();

app.MapPost("/api/groups/{id:guid}/roles", async (
        Guid id, AssignGroupRoleRequest request, HttpContext http, IPolicyEngine policy,
        GroupDirectory groups, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.PrincipalAdminister, ct) is { } fail)
        return fail;
    try
    {
        await groups.AssignRoleAsync(id, request.RoleName, ct);
        return Results.Accepted($"/api/groups/{id}");
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization();

// --- Principals (identity administration; principal.administer, mirrors the groups admin) ---
app.MapGet("/api/principals", async (
        string? kind, bool? enabled, string? search,
        HttpContext http, IPolicyEngine policy, PrincipalAdminService svc,
        CancellationToken ct, int skip = 0, int take = 50) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.PrincipalAdminister, ct) is { } fail)
        return fail;
    return Results.Ok(await svc.QueryAsync(
        new PrincipalQuery(kind, enabled, search, skip, take == 0 ? 50 : take), ct));
}).RequireAuthorization();

app.MapGet("/api/principals/{id:guid}", async (
        Guid id, HttpContext http, IPolicyEngine policy, PrincipalAdminService svc, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.PrincipalAdminister, ct) is { } fail)
        return fail;
    return await svc.GetAsync(id, ct) is { } principal ? Results.Ok(principal) : Results.NotFound();
}).RequireAuthorization();

// Create a human principal (local username/password). New principals are deny-by-default: they hold
// only the roles explicitly assigned or derived from group membership.
app.MapPost("/api/principals", async (
        CreateHumanPrincipalRequest request, HttpContext http, IPolicyEngine policy,
        PrincipalDirectory directory, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.PrincipalAdminister, ct) is { } fail)
        return fail;
    var principal = await directory.CreateHumanAsync(request.DisplayName, request.Username, request.Password, ct);
    return Results.Ok(PrincipalAdminService.ToDto(principal, []));
}).RequireAuthorization();

// Create an AI/service principal; the generated API key is returned exactly once (write-only after).
app.MapPost("/api/principals/ai", async (
        CreateApiKeyPrincipalRequest request, HttpContext http, IPolicyEngine policy,
        PrincipalDirectory directory, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.PrincipalAdminister, ct) is { } fail)
        return fail;
    try
    {
        var (principal, apiKey) = await directory.CreateApiKeyPrincipalAsync(request.DisplayName, request.Kind, ct);
        return Results.Ok(new CreatedApiKeyPrincipal(PrincipalAdminService.ToDto(principal, []), apiKey));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization();

// Assign a Direct role (idempotent). Never touches Group-/GroupMapping-sourced roles.
app.MapPost("/api/principals/{id:guid}/roles", async (
        Guid id, AssignRoleRequest request, HttpContext http, IPolicyEngine policy,
        PrincipalDirectory directory, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.PrincipalAdminister, ct) is { } fail)
        return fail;
    try
    {
        await directory.AssignRoleAsync(id, request.RoleName, ct);
        return Results.Accepted($"/api/principals/{id}");
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization();

// Revoke a Direct role (idempotent — revoking an absent assignment is a no-op success). A role held
// only through a group is not a Direct assignment and is therefore left untouched.
app.MapDelete("/api/principals/{id:guid}/roles/{role}", async (
        Guid id, string role, HttpContext http, IPolicyEngine policy,
        PrincipalDirectory directory, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.PrincipalAdminister, ct) is { } fail)
        return fail;
    await directory.RevokeRoleAsync(id, role, ct);
    return Results.NoContent();
}).RequireAuthorization();

app.MapPost("/api/principals/{id:guid}/enabled", async (
        Guid id, SetPrincipalEnabledRequest request, HttpContext http, IPolicyEngine policy,
        PrincipalDirectory directory, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.PrincipalAdminister, ct) is { } fail)
        return fail;
    return await directory.SetEnabledAsync(id, request.Enabled, ct)
        ? Results.Accepted($"/api/principals/{id}")
        : Results.NotFound();
}).RequireAuthorization();

// --- Identity: directory group → role mappings (consumed at federated sign-in) ---
app.MapGet("/api/identity/group-mappings", async (
        HttpContext http, IPolicyEngine policy, GroupMappingDirectory mappings, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.IdentitySourceManage, ct) is { } fail)
        return fail;
    return Results.Ok((await mappings.ListAsync(ct))
        .Select(m => new GroupMappingDto(m.Id, m.IdentityProvider, m.GroupClaim, m.RoleName)));
}).RequireAuthorization();

app.MapPost("/api/identity/group-mappings", async (
        CreateGroupMappingRequest request, HttpContext http, IPolicyEngine policy,
        GroupMappingDirectory mappings, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.IdentitySourceManage, ct) is { } fail)
        return fail;
    try
    {
        var mapping = await mappings.CreateAsync(request.IdentityProvider, request.GroupClaim, request.RoleName, ct);
        return Results.Ok(new GroupMappingDto(mapping.Id, mapping.IdentityProvider, mapping.GroupClaim, mapping.RoleName));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization();

app.MapDelete("/api/identity/group-mappings/{id:guid}", async (
        Guid id, HttpContext http, IPolicyEngine policy, GroupMappingDirectory mappings, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.IdentitySourceManage, ct) is { } fail)
        return fail;
    return await mappings.RemoveAsync(id, ct) ? Results.NoContent() : Results.NotFound();
}).RequireAuthorization();

// --- Identity: bulk/offline principal provisioning from a directory (LDAP/AD) or CSV ---
// The Core owns identity: sources are administered here and imports upsert principals into the
// Core identity store. Deny-by-default roles — a provisioned user is granted roles only via the
// source's default role and group→role mappings, never with local credentials (an administrator
// sets those before the user can sign in). Interactive OIDC/Entra sign-in is a separate feature.
app.MapGet("/api/identity/connectors", async (
        HttpContext http, IPolicyEngine policy, IdentityImportService import, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.IdentitySourceManage, ct) is { } fail)
        return fail;
    return Results.Ok(import.Connectors.Select(c => c.ToDescriptorDto()));
}).RequireAuthorization();

app.MapGet("/api/identity/sources", async (
        HttpContext http, IPolicyEngine policy, IdentityImportService import, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.IdentitySourceManage, ct) is { } fail)
        return fail;
    return Results.Ok((await import.ListAsync(ct)).Select(s => s.ToDto()));
}).RequireAuthorization();

app.MapGet("/api/identity/sources/{id:guid}", async (
        Guid id, HttpContext http, IPolicyEngine policy, IdentityImportService import, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.IdentitySourceManage, ct) is { } fail)
        return fail;
    return await import.GetAsync(id, ct) is { } view ? Results.Ok(view.ToDto()) : Results.NotFound();
}).RequireAuthorization();

app.MapPost("/api/identity/sources", async (
        SaveIdentitySourceRequest request, HttpContext http, IPolicyEngine policy,
        IdentityImportService import, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.IdentitySourceManage, ct) is { } fail)
        return fail;
    try
    {
        var saved = await import.SaveAsync(
            CoreClaims.PrincipalIdOf(http.User)!.Value.ToString("D"), request.ToDraft(), ct);
        return Results.Ok(saved.ToDto());
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization();

app.MapDelete("/api/identity/sources/{id:guid}", async (
        Guid id, HttpContext http, IPolicyEngine policy, IdentityImportService import, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.IdentitySourceManage, ct) is { } fail)
        return fail;
    return await import.DeleteAsync(CoreClaims.PrincipalIdOf(http.User)!.Value.ToString("D"), id, ct)
        ? Results.NoContent()
        : Results.NotFound();
}).RequireAuthorization();

app.MapPost("/api/identity/sources/{id:guid}/test", async (
        Guid id, HttpContext http, IPolicyEngine policy, IdentityImportService import, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.IdentitySourceManage, ct) is { } fail)
        return fail;
    try
    {
        return Results.Ok((await import.TestConnectionAsync(id, ct)).ToDto());
    }
    catch (InvalidOperationException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
}).RequireAuthorization();

app.MapPost("/api/identity/sources/{id:guid}/import", async (
        Guid id, HttpContext http, IPolicyEngine policy, IdentityImportService import, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.IdentitySourceManage, ct) is { } fail)
        return fail;
    try
    {
        var summary = await import.ImportAsync(
            CoreClaims.PrincipalIdOf(http.User)!.Value.ToString("D"), id, ct);
        return Results.Ok(summary.ToDto());
    }
    catch (InvalidOperationException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
}).RequireAuthorization();

// --- MCP (authenticated) + health ---
app.MapMcp("/mcp").RequireAuthorization();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();

// Exposed for WebApplicationFactory-based component tests.
public partial class Program;
