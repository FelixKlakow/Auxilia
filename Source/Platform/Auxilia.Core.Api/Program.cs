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
builder.Services.AddPlatformEntity<CoreRunViewRecord>(platformData);
builder.Services.AddPlatformEntity<CoreDashboardPinRecord>(platformData);
builder.Services.AddPlatformEntity<CoreArtifactRecord>(platformData);
builder.Services.AddPlatformEntity<CoreRunResolutionRecord>(platformData);
builder.Services.AddPlatformEntity<CoreWorkflowTypeRecord>(platformData);
builder.Services.AddPlatformEntity<DelegatedUserTokenRecord>(platformData);
builder.Services.AddPlatformEntity<AuditRecord>(platformData);
// Provider catalog (Core-owned governance): the registered slot-handler plugins and their curation.
builder.Services.AddPlatformEntity<SlotProviderRecord>(platformData);
builder.Services.AddPlatformEntity<ProviderCatalogRecord>(platformData);
// Admin-managed environment layers (Dockerfile fragments the runner composes onto workflow images).
builder.Services.AddPlatformEntity<EnvironmentLayerRecord>(platformData);
// Batched audit writes are an opt-in for high request rates (Audit:BatchedWrites); the
// default keeps every append a completed store write.
var auditSettings = new Auxilia.PlatformData.AuditLogSettings();
builder.Configuration.GetSection("Audit").Bind(auditSettings);
builder.Services.AddSingleton(auditSettings);
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
builder.Services.AddHttpClient();
builder.Services.AddSingleton<WorkflowTypeRegistryService>();
builder.Services.AddSingleton<WorkflowTypeApprovalPipeline>();
builder.Services.AddSingleton<IWorkflowTypeApprovalHandler, EmailApprovalNotificationHandler>();
builder.Services.AddSingleton<ConnectorService>();
builder.Services.AddSingleton<ConnectorBrowseService>();
builder.Services.AddSingleton<ConnectorTokenRefresher>();
builder.Services.AddSingleton<AccessGrantEvaluator>();
builder.Services.AddSingleton<ConnectorAccessPolicy>();
builder.Services.AddSingleton<DelegatedTokenStore>();
builder.Services.AddSingleton<RunConfigurationService>();
builder.Services.AddSingleton<RunService>();
builder.Services.AddSingleton<RunReadService>();
builder.Services.AddSingleton<DashboardPinService>();
builder.Services.AddSingleton<AuditReadService>();
builder.Services.AddSingleton<ProviderCatalogService>();
builder.Services.AddSingleton<EnvironmentLayerService>();
builder.Services.AddSingleton<WorkflowSchemaReadService>();
builder.Services.AddSingleton<PrincipalAdminService>();
builder.Services.AddSingleton<SlotCredentialResolver>();
builder.Services.AddSingleton<RunStreamBroker>();
builder.Services.AddSingleton<ArtifactStreamBroker>();
// Artifact payloads come from the shared payload backend (ArtifactStore:PayloadRoot points at
// the same location the runner writes); metadata is mirrored from the bus, never read from
// the runner's index.
var artifactStoreSettings = new Auxilia.PlatformData.Artifacts.ArtifactStoreSettings();
builder.Configuration.GetSection("ArtifactStore").Bind(artifactStoreSettings);
builder.Services.AddSingleton(artifactStoreSettings);
builder.Services.AddSingleton<Auxilia.PlatformData.Artifacts.IArtifactPayloadReader,
    Auxilia.PlatformData.Artifacts.FileSystemArtifactPayloadReader>();
builder.Services.AddSingleton<Auxilia.Workflows.Messaging.WorkflowStatusPublisher>();
builder.Services.AddSingleton<RunnerLivenessTracker>();
builder.Services.AddSingleton<TerminalTicketService>();
builder.Services.AddSingleton<TerminalProxyService>();
builder.Services.AddSingleton<FailoverMonitor>();
builder.Services.AddHostedService<RunTrackingService>();
builder.Services.AddHostedService<RunViewTrackingService>();
builder.Services.AddHostedService<WorkflowSchemaTrackingService>();
// Resolvable singleton: the run-stream SSE endpoint primes command-id aliases on it.
builder.Services.AddSingleton<RunStreamPublisher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RunStreamPublisher>());
builder.Services.AddHostedService<ArtifactTrackingService>();
builder.Services.AddHostedService<ArtifactStreamPublisher>();
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

    // Statically configured workflow types register as Active: the host configuration IS the
    // operator's trust decision (mirrors the static-configuration seeding above).
    var workflowRegistry = app.Services.GetRequiredService<WorkflowTypeRegistryService>();
    foreach (var seed in coreSettings.StaticWorkflowTypes)
        await workflowRegistry.EnsureSeededAsync(seed, app.Lifetime.ApplicationStopping);
}

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseWebSockets();

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
{
    if (CoreClaims.PrincipalIdOf(http.User) is not { } principalId)
        return Results.Unauthorized();
    var roles = http.User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();
    return Results.Ok(new CurrentPrincipal(
        principalId,
        http.User.Identity?.Name,
        roles,
        roles.SelectMany(BuiltInRoles.PermissionsOf).Distinct().Order().ToArray()));
}).RequireAuthorization();

// --- Roles & sharing directory: read-only vocabulary for every authenticated client ---
app.MapGet("/api/roles", () => Results.Ok(
        BuiltInRoles.AllRoleNames
            .Select(r => new RoleDto(r, BuiltInRoles.PermissionsOf(r).Order().ToList()))
            .ToList()))
    .RequireAuthorization();

// Sharing needs a people/group PICKER, not principal administration — any authenticated
// principal may list ids + display names (nothing else) to address a grant.
app.MapGet("/api/directory/subjects", async (
        Auxilia.UniversalDataAccess.IDataAccess<PrincipalRecord> principals,
        GroupDirectory groups, CancellationToken ct) =>
{
    var principalList = (await principals.ReadAsync(ct))
        .Where(p => p.Status == "Active")
        .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
        .Select(p => new SharingPrincipal(p.Id, p.DisplayName, p.Kind))
        .ToList();
    var groupList = (await groups.ListAsync(ct))
        .Select(g => new SharingGroup(g.Id, g.Name))
        .ToList();
    return Results.Ok(new SharingSubjects(principalList, groupList));
}).RequireAuthorization();

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
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization();

app.MapGet("/api/runs", async (
        string? state, string? workflowType, Guid? configurationId,
        RunReadService svc, CancellationToken ct, int skip = 0, int take = 50) =>
    Results.Ok(await svc.QueryAsync(
        new RunQuery(state, workflowType, configurationId, skip, take == 0 ? 50 : take), ct)))
    .RequireAuthorization();

// Server-side aggregate counts — the dashboard's stat tiles, no page scraping. (Must be mapped
// before the {id:guid} route only by convention; the guid constraint already disambiguates.)
app.MapGet("/api/runs/stats", async (RunReadService svc, CancellationToken ct) =>
        Results.Ok(await svc.GetStatsAsync(ct)))
    .RequireAuthorization();

app.MapGet("/api/runs/{id:guid}", async (Guid id, RunReadService svc, CancellationToken ct) =>
        await svc.GetAsync(id, ct) is { } status ? Results.Ok(status) : Results.NotFound())
    .RequireAuthorization();

// --- Dashboard pins (personal): a principal keeps chosen run views on their dashboard ---

app.MapGet("/api/dashboard/pins", async (
        HttpContext http, DashboardPinService svc, CancellationToken ct) =>
    CoreClaims.PrincipalIdOf(http.User) is { } principalId
        ? Results.Ok(await svc.ListAsync(principalId, ct))
        : Results.Unauthorized())
    .RequireAuthorization();

app.MapPost("/api/dashboard/pins", async (
        CreateDashboardPin request, HttpContext http, DashboardPinService svc, CancellationToken ct) =>
{
    if (CoreClaims.PrincipalIdOf(http.User) is not { } principalId)
        return Results.Unauthorized();
    return await svc.PinAsync(principalId, request, ct) is { } pin
        ? Results.Ok(pin)
        : Results.NotFound();
}).RequireAuthorization();

app.MapDelete("/api/dashboard/pins/{id:guid}", async (
        Guid id, HttpContext http, DashboardPinService svc, CancellationToken ct) =>
{
    if (CoreClaims.PrincipalIdOf(http.User) is not { } principalId)
        return Results.Unauthorized();
    return await svc.UnpinAsync(principalId, id, ct) ? Results.NoContent() : Results.NotFound();
}).RequireAuthorization();

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
    // Callers may hold the DISPATCH id; the runner stops containers by INSTANCE id — cancel
    // with the resolved run's id (the read service already de-aliased it).
    await runs.CancelAsync(run?.RunId ?? id, ct);
    return Results.Accepted($"/api/runs/{id}");
}).RequireAuthorization();

// Rerun: re-dispatch a past run from its stored dispatch command (fresh id + token, bindings
// re-stashed, connector eligibility re-checked). Authorized like any trigger. Audited.
app.MapPost("/api/runs/{id:guid}/rerun", async (
        Guid id, HttpContext http, IPolicyEngine policy, RunService runs, RunReadService runView,
        AuditLog audit, CancellationToken ct) =>
{
    if (CoreClaims.PrincipalIdOf(http.User) is not { } principalId)
        return Results.Unauthorized();
    if (await runView.GetRecordAsync(id, ct) is not { } record)
        return Results.NotFound();
    var decision = await policy.EvaluateAsync(
        new PolicyContext(principalId, PermissionActions.WorkflowTrigger, id.ToString())
        { WorkflowType = record.WorkflowType }, ct);
    if (!decision.Allowed)
        return Results.Json(new { error = decision.Reason }, statusCode: StatusCodes.Status403Forbidden);
    try
    {
        var accepted = await runs.RerunAsync(record, principalId, ct);
        await audit.AppendAsync(principalId.ToString(), "workflow.rerun",
            record.Id.ToString(), accepted.RunId.ToString(), ct: ct);
        return Results.Ok(accepted);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (ConnectorAccessDeniedException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
    }
}).RequireAuthorization();

// Live-view stream (SSE): status transitions + view items for a run, fanned from the bus via the
// RunStreamBroker, until the run reaches a terminal state or the client disconnects. Replaces the
// BackendService SignalR /hubs/views live push.
app.MapGet("/api/runs/{id:guid}/stream", async (
        Guid id, HttpContext http, IPolicyEngine policy, RunStreamBroker broker,
        RunStreamPublisher streamPublisher, Auxilia.UniversalDataAccess.IDataAccess<CoreRunRecord> runRecords,
        CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.RunObserve, ct) is { } fail)
    {
        await fail.ExecuteAsync(http);
        return;
    }

    http.Response.Headers.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";
    http.Response.Headers["X-Accel-Buffering"] = "no";

    // The id may be the dispatch COMMAND id rather than the runner's instance id. Selective
    // routing binds by key, so resolve the pairing from the tracked runs and prime the alias —
    // a late subscriber cannot rely on observing the claim transition on the bus.
    if (await runRecords.ReadAsync(id, ct) is null)
    {
        var byCommand = (await runRecords.ReadAsync(ct)).FirstOrDefault(r => r.CommandId == id);
        if (byCommand is not null)
            await streamPublisher.RegisterAliasAsync(id, byCommand.Id, ct);
    }

    using var subscription = await broker.SubscribeAsync(id, ct);
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

// Deliver-input (the steer-back half of the loop): an authorized caller posts an OPAQUE payload
// into a running workflow. The Core authorizes + audits and publishes it to the instance's
// dedicated INPUT queue — it never interprets the payload (no decision semantics here).
app.MapPost("/api/runs/{id:guid}/inputs", async (
        Guid id, ProvideRunInput request, HttpContext http, IPolicyEngine policy,
        Auxilia.UniversalDataAccess.IDataAccess<CoreRunRecord> runStore,
        IMessageBusClient bus, AuditLog audit, CancellationToken ct) =>
{
    if (CoreClaims.PrincipalIdOf(http.User) is not { } principalId)
        return Results.Unauthorized();
    var decision = await policy.EvaluateAsync(
        new PolicyContext(principalId, PermissionActions.RunProvideInput, id.ToString()), ct);
    if (!decision.Allowed)
        return Results.Json(new { error = decision.Reason }, statusCode: StatusCodes.Status403Forbidden);

    // The caller may hold the dispatch CommandId (what run-accept returned) or the instance id.
    var run = await runStore.ReadAsync(id, ct)
              ?? (await runStore.ReadAsync(ct)).FirstOrDefault(r => r.CommandId == id);
    if (run is null)
        return Results.NotFound(new { error = "run not found" });
    if (CoreRunStates.IsTerminal(run.State))
        return Results.BadRequest(new { error = $"the run has ended ({run.State}) — it accepts no input" });

    await bus.PublishAsync(
        Auxilia.Workflows.Messaging.WorkflowQueues.InputQueueFor(run.Id),
        new Auxilia.Workflows.Messaging.Messages.WorkflowInputMessage(
            run.Id, request.PayloadJson, DateTimeOffset.UtcNow), ct);
    await audit.AppendAsync(
        principalId.ToString(), PermissionActions.RunProvideInput, run.Id.ToString(), "delivered", ct: ct);
    return Results.Accepted($"/api/runs/{run.Id}");
}).RequireAuthorization();

// Terminal access, step 1: an AUTHORIZED caller mints a short-lived ticket for one run's
// interactive web terminal. The ticket (not the API bearer) then authenticates the proxy's
// page/asset/websocket requests — a browser surface cannot attach bearer headers to those.
app.MapPost("/api/runs/{id:guid}/terminal-ticket", async (
        Guid id, HttpContext http, IPolicyEngine policy,
        Auxilia.UniversalDataAccess.IDataAccess<CoreRunRecord> runStore,
        TerminalTicketService tickets, AuditLog audit, CancellationToken ct) =>
{
    if (CoreClaims.PrincipalIdOf(http.User) is not { } principalId)
        return Results.Unauthorized();
    var decision = await policy.EvaluateAsync(
        new PolicyContext(principalId, PermissionActions.RunOpenTerminal, id.ToString()), ct);
    if (!decision.Allowed)
        return Results.Json(new { error = decision.Reason }, statusCode: StatusCodes.Status403Forbidden);

    var run = await runStore.ReadAsync(id, ct)
              ?? (await runStore.ReadAsync(ct)).FirstOrDefault(r => r.CommandId == id);
    if (run is null)
        return Results.NotFound(new { error = "run not found" });
    if (CoreRunStates.IsTerminal(run.State))
        return Results.BadRequest(new { error = $"the run has ended ({run.State}) — its terminal is gone" });
    if (run.TerminalEndpoint is not { Length: > 0 })
        return Results.NotFound(new { error = "the run hosts no interactive terminal" });

    var (ticket, expires) = tickets.Issue(run.Id);
    await audit.AppendAsync(
        principalId.ToString(), PermissionActions.RunOpenTerminal, run.Id.ToString(), "ticket-issued", ct: ct);
    return Results.Ok(new Auxilia.Core.Contracts.TerminalTicket(
        run.Id, $"/api/runs/{run.Id}/terminal/?ticket={ticket}", expires));
}).RequireAuthorization();

// Terminal access, step 2: the ticketed proxy. Serves ttyd's page and assets over HTTP and
// pumps its websocket — the only path from outside to a run's terminal; the container's
// address never leaves the Core. The first page response plants a path-scoped cookie so
// ttyd's follow-up requests (which carry no query ticket) stay authenticated.
app.Map("/api/runs/{id:guid}/terminal/{**path}", async (
        Guid id, string? path, HttpContext http,
        Auxilia.UniversalDataAccess.IDataAccess<CoreRunRecord> runStore,
        TerminalTicketService tickets, TerminalProxyService proxy, CancellationToken ct) =>
{
    var run = await runStore.ReadAsync(id, ct)
              ?? (await runStore.ReadAsync(ct)).FirstOrDefault(r => r.CommandId == id);
    if (run is null)
    {
        await Results.NotFound(new { error = "run not found" }).ExecuteAsync(http);
        return;
    }

    var ticket = http.Request.Query["ticket"].FirstOrDefault()
                 ?? http.Request.Cookies["auxilia-terminal-ticket"];
    if (!tickets.Validate(ticket, run.Id))
    {
        await Results.Json(new { error = "missing or expired terminal ticket" },
            statusCode: StatusCodes.Status401Unauthorized).ExecuteAsync(http);
        return;
    }

    if (run.TerminalEndpoint is not { Length: > 0 } endpoint || CoreRunStates.IsTerminal(run.State))
    {
        await Results.Json(new { error = "the run's terminal is not available" },
            statusCode: StatusCodes.Status409Conflict).ExecuteAsync(http);
        return;
    }

    if (http.WebSockets.IsWebSocketRequest)
    {
        await proxy.ForwardWebSocketAsync(http, endpoint, path ?? "", ct);
        return;
    }

    if (string.IsNullOrEmpty(path) && ticket == http.Request.Query["ticket"].FirstOrDefault())
        http.Response.Cookies.Append("auxilia-terminal-ticket", ticket!, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Path = $"/api/runs/{id}/terminal",
            MaxAge = TerminalTicketService.TimeToLive
        });
    await proxy.ForwardHttpAsync(http, endpoint, path ?? "", ct);
});

// Clear run history: deletes TERMINAL run records and their persisted view items. With
// includeStale=true it also removes non-terminal ZOMBIES — records whose terminal event was
// missed (e.g. Core downtime) and that have not updated for staleMinutes; genuinely live runs
// keep receiving status events and therefore never look stale.
app.MapDelete("/api/runs", async (
        HttpContext http, IPolicyEngine policy,
        Auxilia.UniversalDataAccess.IDataAccess<CoreRunRecord> runStore,
        Auxilia.UniversalDataAccess.IDataAccess<CoreRunViewRecord> viewStore,
        AuditLog audit, TimeProvider clock, CancellationToken ct,
        bool includeStale = false, int staleMinutes = 30) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.WorkflowConfigurationManage, ct) is { } fail)
        return fail;
    var staleCutoff = clock.GetUtcNow() - TimeSpan.FromMinutes(Math.Max(1, staleMinutes));
    var doomed = (await runStore.ReadAsync(ct))
        .Where(r => CoreRunStates.IsTerminal(r.State)
                    || (includeStale && r.UpdatedUtc < staleCutoff))
        .ToList();
    foreach (var run in doomed)
    {
        foreach (var view in (await viewStore.ReadAsync(ct)).Where(v => v.RunId == run.Id).ToList())
            await viewStore.RemoveAsync(view.Id, ct);
        await runStore.RemoveAsync(run.Id, ct);
    }
    await audit.AppendAsync(
        CoreClaims.PrincipalIdOf(http.User)!.Value.ToString("D"), "run.clear-history",
        "runs", "cleared", $"{{\"deleted\":{doomed.Count},\"includeStale\":{includeStale.ToString().ToLowerInvariant()}}}", ct);
    return Results.Ok(new { deleted = doomed.Count });
}).RequireAuthorization();

// The read-later counterpart of the live stream: a run's persisted view items (outputs), so a
// client can inspect results after the run finished. Gated like observing the live stream.
app.MapGet("/api/runs/{id:guid}/views", async (
        Guid id, string? view, HttpContext http, IPolicyEngine policy,
        Auxilia.UniversalDataAccess.IDataAccess<CoreRunViewRecord> views,
        Auxilia.UniversalDataAccess.IDataAccess<CoreRunRecord> runStore,
        CancellationToken ct, int skip = 0, int take = 200) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.RunObserve, ct) is { } fail)
        return fail;
    // Callers may hold the DISPATCH id (RunAccepted.RunId) — resolve to the instance id like
    // every other run read (the runner records views under its own instance id).
    var runId = id;
    if (await runStore.ReadAsync(id, ct) is null
        && (await runStore.ReadAsync(ct)).FirstOrDefault(r => r.CommandId == id) is { } aliased)
        runId = aliased.Id;
    var all = (await views.ReadAsync(ct)).Where(v => v.RunId == runId);
    if (!string.IsNullOrWhiteSpace(view))
        all = all.Where(v => string.Equals(v.ViewName, view, StringComparison.Ordinal));
    var ordered = all.OrderBy(v => v.TimestampUtc).ThenBy(v => v.Sequence).ToList();
    var effectiveTake = take <= 0 ? 200 : take;
    var page = ordered.Skip(skip).Take(effectiveTake)
        .Select(v => new RunViewItem(v.ViewName, v.Sequence, v.PayloadJson, v.TimestampUtc))
        .ToList();
    return Results.Ok(new PagedResult<RunViewItem>(page, ordered.Count, skip, effectiveTake));
}).RequireAuthorization();

// --- Artifacts (client surface; metadata mirrored from the bus, payloads from the shared backend) ---

app.MapGet("/api/artifacts", async (
        string? artifactType, string? workItemId, Guid? runId, HttpContext http, IPolicyEngine policy,
        Auxilia.UniversalDataAccess.IDataAccess<CoreArtifactRecord> artifacts,
        CancellationToken ct, int skip = 0, int take = 50) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.ArtifactConsume, ct) is { } fail)
        return fail;
    var all = (await artifacts.ReadAsync(ct)).AsEnumerable();
    if (!string.IsNullOrWhiteSpace(artifactType))
        all = all.Where(a => string.Equals(a.ArtifactType, artifactType, StringComparison.Ordinal));
    if (!string.IsNullOrWhiteSpace(workItemId))
        all = all.Where(a => string.Equals(a.WorkItemId, workItemId, StringComparison.Ordinal));
    if (runId is { } run)
        all = all.Where(a => a.RunInstanceId == run);
    var ordered = all.OrderByDescending(a => a.CreatedUtc).ToList();
    var effectiveTake = take <= 0 ? 50 : take;
    var page = ordered.Skip(skip).Take(effectiveTake).Select(a => new ArtifactDto(
        a.Id, a.ArtifactType, a.WorkflowType, a.WorkItemId, a.RunInstanceId,
        a.Version, a.ContentHash, a.SizeBytes, a.CreatedUtc)).ToList();
    return Results.Ok(new PagedResult<ArtifactDto>(page, ordered.Count, skip, effectiveTake));
}).RequireAuthorization();

app.MapGet("/api/artifacts/{id:guid}", async (
        Guid id, HttpContext http, IPolicyEngine policy,
        Auxilia.UniversalDataAccess.IDataAccess<CoreArtifactRecord> artifacts, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.ArtifactConsume, ct) is { } fail)
        return fail;
    return await artifacts.ReadAsync(id, ct) is { } a
        ? Results.Ok(new ArtifactDto(
            a.Id, a.ArtifactType, a.WorkflowType, a.WorkItemId, a.RunInstanceId,
            a.Version, a.ContentHash, a.SizeBytes, a.CreatedUtc))
        : Results.NotFound();
}).RequireAuthorization();

// Payload download: metadata must be mirrored AND the payload present in the shared backend
// (ArtifactStore:PayloadRoot); a missing payload is a deployment wiring gap, logged as such.
app.MapGet("/api/artifacts/{id:guid}/content", async (
        Guid id, HttpContext http, IPolicyEngine policy,
        Auxilia.UniversalDataAccess.IDataAccess<CoreArtifactRecord> artifacts,
        Auxilia.PlatformData.Artifacts.IArtifactPayloadReader payloads,
        AuditLog audit, ILogger<Program> logger, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.ArtifactConsume, ct) is { } fail)
        return fail;
    if (await artifacts.ReadAsync(id, ct) is not { } artifact)
        return Results.NotFound();
    if (await payloads.OpenReadAsync(id, ct) is not { } payload)
    {
        logger.LogWarning(
            "Artifact {ArtifactId} is indexed but its payload is missing — is ArtifactStore:PayloadRoot shared with the runner?",
            id);
        return Results.NotFound(new { error = "the artifact payload is not available on this node" });
    }
    await audit.AppendAsync(CoreClaims.PrincipalIdOf(http.User)?.ToString() ?? "unknown",
        PermissionActions.ArtifactConsume, id.ToString(), $"{artifact.ArtifactType} v{artifact.Version}", ct: ct);
    return Results.Stream(payload, "application/octet-stream",
        $"{artifact.ArtifactType}-v{artifact.Version}");
}).RequireAuthorization();

// Artifact SSE stream, SERVER-SIDE FILTERED (artifact type / work item): the client-surface
// replacement for a bus subscription — chaining libraries react to artifacts through this.
app.MapGet("/api/artifacts/stream", async (
        string? artifactType, string? workItemId, HttpContext http, IPolicyEngine policy,
        ArtifactStreamBroker broker, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.ArtifactConsume, ct) is { } fail)
    {
        await fail.ExecuteAsync(http);
        return;
    }

    http.Response.Headers.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";
    http.Response.Headers["X-Accel-Buffering"] = "no";

    using var subscription = await broker.SubscribeAsync(artifactType, workItemId, ct);
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
// A Personal configuration (the default) is self-owned; sharing platform-wide (Company) is the
// deliberate opt-in and needs the configuration-management permission — mirroring connectors.
app.MapPost("/api/configurations", async (
        CreateRunConfiguration request, HttpContext http, IPolicyEngine policy,
        RunConfigurationService svc, CancellationToken ct) =>
{
    if (CoreClaims.PrincipalIdOf(http.User) is not { } principalId)
        return Results.Unauthorized();
    if (request.Scope != ResourceScope.Personal
        && await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.WorkflowConfigurationManage, ct) is { } fail)
        return fail;
    return Results.Ok(await svc.CreateAsync(request, principalId, ct));
}).RequireAuthorization();

// Reads are visibility-filtered: managers see everything; everyone else sees company
// configurations plus personal ones they own or were granted (principal/group/AD-group).
app.MapGet("/api/configurations", async (
        string? workflowType, bool? enabled, HttpContext http,
        RunConfigurationService svc, CancellationToken ct, int skip = 0, int take = 50) =>
    Results.Ok(await svc.QueryAsync(
        new ConfigurationQuery(workflowType, enabled, skip, take == 0 ? 50 : take),
        ViewerOf(http.User), ct)))
    .RequireAuthorization();

app.MapGet("/api/configurations/{id:guid}", async (
        Guid id, HttpContext http, RunConfigurationService svc, CancellationToken ct) =>
        await svc.GetAsync(id, ViewerOf(http.User), ct) is { } config
            ? Results.Ok(config) : Results.NotFound())
    .RequireAuthorization();

// Update a stored configuration (its owner, or the config-management permission). Audited.
app.MapPut("/api/configurations/{id:guid}", async (
        Guid id, UpdateRunConfiguration request, HttpContext http, IPolicyEngine policy,
        RunConfigurationService svc, AuditLog audit, CancellationToken ct) =>
{
    if (!await svc.IsOwnerAsync(id, CoreClaims.PrincipalIdOf(http.User), ct)
        && await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.WorkflowConfigurationManage, ct) is { } fail)
        return fail;
    if (await svc.UpdateAsync(id, request, ct) is not { } updated)
        return Results.NotFound();
    await audit.AppendAsync(
        CoreClaims.PrincipalIdOf(http.User)!.Value.ToString("D"),
        "workflow-configuration.updated", id.ToString(), updated.Name, ct: ct);
    return Results.Ok(updated);
}).RequireAuthorization();

// Replace a personal configuration's access grants (its owner, or a configuration manager). Audited.
app.MapPut("/api/configurations/{id:guid}/grants", async (
        Guid id, SetConfigurationGrants request, HttpContext http, IPolicyEngine policy,
        RunConfigurationService svc, AuditLog audit, CancellationToken ct) =>
{
    if (!await svc.IsOwnerAsync(id, CoreClaims.PrincipalIdOf(http.User), ct)
        && await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.WorkflowConfigurationManage, ct) is { } fail)
        return fail;
    try
    {
        if (await svc.SetGrantsAsync(id, request.Grants, ct) is not { } updated)
            return Results.NotFound();
        await audit.AppendAsync(
            CoreClaims.PrincipalIdOf(http.User)!.Value.ToString("D"),
            "workflow-configuration.grants-set", id.ToString(),
            System.Text.Json.JsonSerializer.Serialize(request.Grants), ct: ct);
        return Results.Ok(updated);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization();

// Delete a stored configuration permanently (its owner, or the config-management permission). Audited.
app.MapDelete("/api/configurations/{id:guid}", async (
        Guid id, HttpContext http, IPolicyEngine policy, RunConfigurationService svc, AuditLog audit,
        CancellationToken ct) =>
{
    if (!await svc.IsOwnerAsync(id, CoreClaims.PrincipalIdOf(http.User), ct)
        && await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.WorkflowConfigurationManage, ct) is { } fail)
        return fail;
    if (await svc.GetAsync(id, ct) is not { } config)
        return Results.NotFound();
    await svc.DeleteAsync(id, ct);
    await audit.AppendAsync(
        CoreClaims.PrincipalIdOf(http.User)!.Value.ToString("D"),
        "workflow-configuration.deleted", id.ToString(), config.Name, ct: ct);
    return Results.NoContent();
}).RequireAuthorization();

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

    // A personal configuration is runnable only by whoever may SEE it: the triggering principal
    // (owner/granted), or a caller holding the configuration-management permission.
    var runViewer = new ConfigurationViewer(
        onBehalfOf ?? principalId,
        CoreClaims.HasRolePermission(http.User, PermissionActions.WorkflowConfigurationManage));
    if (!await configurations.IsVisibleAsync(id, runViewer, ct))
        return Results.NotFound();

    // On-behalf-of: a service/automation caller (a trigger host's engines) may dispatch a stored
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
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
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

app.MapPost("/api/provider-catalog", async (
        RegisterSlotProvider request, HttpContext http, IPolicyEngine policy,
        ProviderCatalogService svc, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.ProviderCatalogManage, ct) is { } fail)
        return fail;
    try
    {
        return Results.Ok(await svc.RegisterAsync(
            CoreClaims.PrincipalIdOf(http.User)!.Value.ToString("D"), request, ct));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization();

app.MapDelete("/api/provider-catalog/{providerType}", async (
        string providerType, HttpContext http, IPolicyEngine policy,
        ProviderCatalogService svc, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.ProviderCatalogManage, ct) is { } fail)
        return fail;
    return await svc.DeleteAsync(
        CoreClaims.PrincipalIdOf(http.User)!.Value.ToString("D"), providerType, ct)
        ? Results.NoContent()
        : Results.NotFound();
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

// --- Environment layers (admin-managed session software; composes onto workflow images) ---
// Managing layers rides the provider-catalog permission: an environment IS a catalog entry.
app.MapGet("/api/environment-layers", async (
        HttpContext http, IPolicyEngine policy, EnvironmentLayerService svc, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.ProviderCatalogManage, ct) is { } fail)
        return fail;
    return Results.Ok(await svc.ListAsync(ct));
}).RequireAuthorization();

app.MapGet("/api/environment-layers/{providerType}", async (
        string providerType, HttpContext http, IPolicyEngine policy, EnvironmentLayerService svc,
        CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.ProviderCatalogManage, ct) is { } fail)
        return fail;
    return await svc.FindAsync(providerType, ct) is { } layer ? Results.Ok(layer) : Results.NotFound();
}).RequireAuthorization();

app.MapPost("/api/environment-layers", async (
        UpsertEnvironmentLayer request, HttpContext http, IPolicyEngine policy,
        EnvironmentLayerService svc, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.ProviderCatalogManage, ct) is { } fail)
        return fail;
    try
    {
        return Results.Ok(await svc.UpsertAsync(CoreClaims.PrincipalIdOf(http.User), request, ct));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization();

app.MapDelete("/api/environment-layers/{providerType}", async (
        string providerType, HttpContext http, IPolicyEngine policy, EnvironmentLayerService svc,
        CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.ProviderCatalogManage, ct) is { } fail)
        return fail;
    return await svc.DeleteAsync(CoreClaims.PrincipalIdOf(http.User), providerType, ct)
        ? Results.NoContent()
        : Results.NotFound();
}).RequireAuthorization();

// Fragment download for the RUNNER, authorized by the run's resolution token (same trust as the
// package download): only runs the Core dispatched can read layer content.
app.MapGet("/api/environment-layers/{providerType}/content", async (
        string providerType, Guid runId, string token, HttpContext http, EnvironmentLayerService svc,
        Auxilia.UniversalDataAccess.IDataAccess<CoreRunResolutionRecord> resolutions,
        CancellationToken ct) =>
{
    var resolution = await resolutions.ReadAsync(runId, ct);
    if (resolution is null || resolution.ResolutionToken != token)
        return Results.Json(new { error = "invalid resolution token" }, statusCode: StatusCodes.Status403Forbidden);
    var signed = await svc.ReadFragmentAsync(providerType, ct);
    if (signed is null)
        return Results.NotFound();
    if (signed.SignatureBase64 is { Length: > 0 } signature)
    {
        http.Response.Headers["X-Auxilia-Signature"] = signature;
        http.Response.Headers["X-Auxilia-Publisher-Key"] = signed.PublisherKeyBase64;
    }
    return Results.Text(signed.Fragment, "text/plain");
});

// --- Workflow-type registry (ARCHITECTURE §7: the deploy-time trust gate) ---
// Types are registered permanently with their signed package coordinate; only Active types run.
// Reading is gated by workflow-configuration.manage (the config editor's permission); registering
// by workflow-type.manage; approving/denying by workflow-type.sign (the signing authority).
app.MapGet("/api/workflow-types", async (
        string? status, HttpContext http, IPolicyEngine policy, WorkflowSchemaReadService svc,
        CancellationToken ct, int skip = 0, int take = 50) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.WorkflowConfigurationManage, ct) is { } fail)
        return fail;
    return Results.Ok(await svc.QueryTypesAsync(new WorkflowTypeQuery(skip, take == 0 ? 50 : take, status), ct));
}).RequireAuthorization();

app.MapGet("/api/workflow-types/{type}/schema", async (
        string type, HttpContext http, IPolicyEngine policy, WorkflowSchemaReadService svc, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.WorkflowConfigurationManage, ct) is { } fail)
        return fail;
    return await svc.GetSchemaAsync(type, ct) is { } schema ? Results.Ok(schema) : Results.NotFound();
}).RequireAuthorization();

app.MapPost("/api/workflow-types", async (
        RegisterWorkflowTypeRequest request, HttpContext http, IPolicyEngine policy,
        WorkflowTypeRegistryService registry, WorkflowTypeApprovalPipeline approvalPipeline,
        CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.WorkflowTypeManage, ct) is { } fail)
        return fail;
    var outcome = await registry.RegisterAsync(request, CoreClaims.PrincipalIdOf(http.User), ct);
    if (outcome.Registration is not { } registration)
        return Results.BadRequest(new { error = outcome.Error });
    // A pending registration enters the async approval pipeline (notify / auto-check); the
    // response is immediate — the decision lands later, from a handler or a human signer.
    if (registration.Status == WorkflowTypeStatus.Pending)
        approvalPipeline.KickOff(registration.WorkflowType);
    return Results.Ok(registration);
}).RequireAuthorization();

app.MapGet("/api/workflow-types/{type}/registration", async (
        string type, HttpContext http, IPolicyEngine policy, WorkflowTypeRegistryService registry,
        CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.WorkflowTypeManage, ct) is { } fail)
        return fail;
    return await registry.GetRegistrationAsync(type, ct) is { } registration
        ? Results.Ok(registration)
        : Results.NotFound();
}).RequireAuthorization();

app.MapDelete("/api/workflow-types/{type}", async (
        string type, HttpContext http, IPolicyEngine policy, WorkflowTypeRegistryService registry,
        CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.WorkflowTypeManage, ct) is { } fail)
        return fail;
    return await registry.UnregisterAsync(type, CoreClaims.PrincipalIdOf(http.User), ct)
        ? Results.NoContent()
        : Results.NotFound();
}).RequireAuthorization();

app.MapPost("/api/workflow-types/{type}/enabled", async (
        string type, SetWorkflowTypeEnabledRequest request, HttpContext http, IPolicyEngine policy,
        WorkflowTypeRegistryService registry, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.WorkflowTypeManage, ct) is { } fail)
        return fail;
    var outcome = await registry.SetEnabledAsync(
        type, request.Enabled, CoreClaims.PrincipalIdOf(http.User)!.Value.ToString("D"), ct);
    return outcome.Registration is { } registration
        ? Results.Ok(registration)
        : Results.BadRequest(new { error = outcome.Error });
}).RequireAuthorization();

app.MapPost("/api/workflow-types/{type}/approve", async (
        string type, HttpContext http, IPolicyEngine policy, WorkflowTypeRegistryService registry,
        CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.WorkflowTypeSign, ct) is { } fail)
        return fail;
    var outcome = await registry.ApproveAsync(
        type, CoreClaims.PrincipalIdOf(http.User)!.Value.ToString("D"), ct);
    return outcome.Registration is { } registration
        ? Results.Ok(registration)
        : Results.NotFound(new { error = outcome.Error });
}).RequireAuthorization();

app.MapPost("/api/workflow-types/{type}/deny", async (
        string type, DenyWorkflowTypeRequest request, HttpContext http, IPolicyEngine policy,
        WorkflowTypeRegistryService registry, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.WorkflowTypeSign, ct) is { } fail)
        return fail;
    var outcome = await registry.DenyAsync(
        type, request.Reason, CoreClaims.PrincipalIdOf(http.User)!.Value.ToString("D"), ct);
    return outcome.Registration is { } registration
        ? Results.Ok(registration)
        : Results.NotFound(new { error = outcome.Error });
}).RequireAuthorization();

// Core-stored package download for the runner. Authorized by the run-scoped resolution token
// (same trust as resolve-slot), NOT a principal — the runner can fetch only packages of runs the
// Core dispatched to it, and the URL is minted per dispatch by the registry.
app.MapGet("/api/workflow-types/{type}/package", async (
        string type, Guid runId, string token, WorkflowTypeRegistryService registry,
        Auxilia.UniversalDataAccess.IDataAccess<CoreRunResolutionRecord> resolutions,
        CancellationToken ct) =>
{
    var resolution = await resolutions.ReadAsync(runId, ct);
    if (resolution is null || resolution.ResolutionToken != token)
        return Results.Json(new { error = "invalid resolution token" }, statusCode: StatusCodes.Status403Forbidden);
    var package = await registry.ReadStoredPackageAsync(type, ct);
    return package is null
        ? Results.NotFound()
        : Results.File(package, "application/zip", $"{type}.workflow.zip");
});

// --- Connectors ---
app.MapPost("/api/connectors", async (
        CreateConnector request, HttpContext http, IPolicyEngine policy, ConnectorService svc, CancellationToken ct) =>
{
    if (CoreClaims.PrincipalIdOf(http.User) is not { } principalId)
        return Results.Unauthorized();
    // Any authenticated principal may connect their own personal (identity-linked) account; a shared
    // company connector requires the connector-management permission.
    if (request.Scope != ResourceScope.Personal
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

// Update a connector in place (owner, or a connector manager): rename and/or refresh settings —
// provided values are upserted key-by-key, so a rotated credential is fixed without re-creating
// the connector. Audited; values are never echoed back.
app.MapPut("/api/connectors/{id:guid}", async (
        Guid id, UpdateConnector request, HttpContext http, IPolicyEngine policy,
        ConnectorService svc, AuditLog audit, CancellationToken ct) =>
{
    if (CoreClaims.PrincipalIdOf(http.User) is not { } principalId)
        return Results.Unauthorized();
    if (await svc.GetAsync(id, ct) is not { } connector)
        return Results.NotFound();
    if (connector.OwnerPrincipalId != principalId
        && await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.SlotConfigWrite, ct) is { } fail)
        return fail;
    var updated = await svc.UpdateAsync(id, request, ct);
    await audit.AppendAsync(
        principalId.ToString(), "connector.updated", id.ToString(),
        string.Join(", ", (request.Settings ?? new Dictionary<string, string>()).Keys), ct: ct);
    return Results.Ok(updated);
}).RequireAuthorization();

// Delete a connector permanently (owner, or a connector manager). Audited — configurations that
// bind it will fail to dispatch afterwards, which the caller is warned about client-side.
app.MapDelete("/api/connectors/{id:guid}", async (
        Guid id, HttpContext http, IPolicyEngine policy, ConnectorService svc, AuditLog audit,
        CancellationToken ct) =>
{
    if (CoreClaims.PrincipalIdOf(http.User) is not { } principalId)
        return Results.Unauthorized();
    if (await svc.GetAsync(id, ct) is not { } connector)
        return Results.NotFound();
    if (connector.OwnerPrincipalId != principalId
        && await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.SlotConfigWrite, ct) is { } fail)
        return fail;
    await svc.DeleteAsync(id, ct);
    await audit.AppendAsync(principalId.ToString(), "connector.deleted", id.ToString(), connector.Name, ct: ct);
    return Results.NoContent();
}).RequireAuthorization();

// Browse live data with a connector's credential — the secret stays Core-side; the caller only
// gets names. Gated by the SAME eligibility check as binding the connector into a run.
app.MapPost("/api/connectors/{id:guid}/browse", async (
        Guid id, BrowseConnector request, HttpContext http, ConnectorAccessPolicy access,
        ConnectorBrowseService browse, CancellationToken ct) =>
{
    if (CoreClaims.PrincipalIdOf(http.User) is not { } principalId)
        return Results.Unauthorized();
    if (!await access.CanUseAsync(id, principalId, ct))
        return Results.Json(new { error = "you are not eligible to use this connector" },
            statusCode: StatusCodes.Status403Forbidden);
    try
    {
        return Results.Ok(await browse.BrowseAsync(id, request, ct));
    }
    catch (NotSupportedException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
}).RequireAuthorization();

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

// The caller a configuration read is filtered for: configuration managers see everything;
// a pure claims check so list/get paths stay audit-quiet (mutations go through the Policy Engine).
static ConfigurationViewer ViewerOf(System.Security.Claims.ClaimsPrincipal user) => new(
    CoreClaims.PrincipalIdOf(user),
    CoreClaims.HasRolePermission(user, PermissionActions.WorkflowConfigurationManage));

// Exposed for WebApplicationFactory-based component tests.
public partial class Program;
