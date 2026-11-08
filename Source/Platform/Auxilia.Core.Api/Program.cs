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
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// --- Messaging ---
builder.Services.AddSingleton<IMessageBusClient>(sp =>
    RabbitMqClient.CreateAsync(
        builder.Configuration["RabbitMq:Host"] ?? "localhost",
        int.Parse(builder.Configuration["RabbitMq:Port"] ?? "5672"),
        builder.Configuration["RabbitMq:UserName"] ?? "guest",
        builder.Configuration["RabbitMq:Password"] ?? "guest",
        sp.GetRequiredService<ILogger<RabbitMqClient>>()).GetAwaiter().GetResult());

// --- Core database (isolated from every other service — Principle 4) ---
var platformData = new PlatformDataSettings();
builder.Configuration.GetSection("PlatformData").Bind(platformData);
builder.Services.AddSingleton(platformData);
builder.Services.AddSettingsProtection(platformData);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddPlatformEntity<CoreRunConfigurationRecord>(platformData);
builder.Services.AddPlatformEntity<CoreConnectorRecord>(platformData);
// First-class repository/workspace resources (non-secret settings + connector reference).
builder.Services.AddPlatformEntity<CoreWorkspaceRecord>(platformData);
// Runtime platform settings (admin-changeable security knobs; unset keys fall back to config).
builder.Services.AddPlatformEntity<PlatformSettingRecord>(platformData);
builder.Services.AddPlatformEntity<CoreRunRecord>(platformData);
builder.Services.AddPlatformEntity<CoreRunViewRecord>(platformData);
builder.Services.AddPlatformEntity<CoreRunInputFileRecord>(platformData);
builder.Services.AddPlatformEntity<CoreDashboardPinRecord>(platformData);
builder.Services.AddPlatformEntity<CoreArtifactRecord>(platformData);
builder.Services.AddPlatformEntity<CoreEventRecord>(platformData);
builder.Services.AddPlatformEntity<CoreRunResolutionRecord>(platformData);
builder.Services.AddPlatformEntity<CoreWorkflowTypeRecord>(platformData);
builder.Services.AddPlatformEntity<DelegatedUserTokenRecord>(platformData);
builder.Services.AddPlatformEntity<AuditRecord>(platformData);
// Provider catalog (Core-owned governance): the registered slot-handler plugins and their curation.
builder.Services.AddPlatformEntity<SlotProviderRecord>(platformData);
builder.Services.AddPlatformEntity<ProviderCatalogRecord>(platformData);
// Admin-managed environment layers (Dockerfile fragments the runner composes onto workflow images).
builder.Services.AddPlatformEntity<EnvironmentLayerRecord>(platformData);
// The environment-base catalog: configurable (name, version) base pairs layers build on.
builder.Services.AddPlatformEntity<EnvironmentBaseRecord>(platformData);
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

// --- Rate limiting: bound slot-credential resolution per run (defence against a compromised runner)
// and password sign-in per client IP (defence against brute-forcing) ---
var coreApiRateSettings = builder.Configuration.GetSection("CoreApi").Get<CoreApiSettings>() ?? new CoreApiSettings();
var coreSecurityRateSettings =
    builder.Configuration.GetSection("CoreSecurity").Get<CoreSecuritySettings>() ?? new CoreSecuritySettings();
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
    // Checkpoint saves get their own budget: a workflow saving after every turn must never
    // starve its own credential re-resolution.
    options.AddPolicy("checkpoint-save", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Request.Headers["X-Resolution-Token"].ToString() is { Length: > 0 } token
                ? token : "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = coreApiRateSettings.CheckpointSavesPerMinute,
                Window = TimeSpan.FromMinutes(1)
            }));
    options.AddPolicy("auth-login", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = coreSecurityRateSettings.LoginRateLimitPermitsPerMinute,
                Window = TimeSpan.FromMinutes(1)
            }));
    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        var audit = context.HttpContext.RequestServices.GetRequiredService<AuditLog>();
        if (context.HttpContext.Request.Path.StartsWithSegments("/auth/login"))
        {
            var ip = context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            await audit.AppendAsync(ip, "auth.login", ip, "rate-limited", ct: ct);
            return;
        }
        if (context.HttpContext.Request.Path.StartsWithSegments("/auth/step-up"))
        {
            // Authenticated by now (the limiter runs after authentication): attribute to the principal.
            var actor = CoreClaims.PrincipalIdOf(context.HttpContext.User)?.ToString()
                        ?? context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            await audit.AppendAsync(actor, "auth.step-up", actor, "rate-limited", ct: ct);
            return;
        }
        var runId = context.HttpContext.Request.RouteValues.TryGetValue("runId", out var r) ? r?.ToString() : null;
        await audit.AppendAsync(
            "core-api", "workflow.slot-credential.rate-limited", runId ?? "unknown", "rate-limit-exceeded", ct: ct);
    };
});
builder.Services.AddSingleton<LoginAttemptThrottle>();

// --- Core services ---
builder.Services.AddHttpClient();
builder.Services.AddSingleton<WorkflowTypeRegistryService>();
builder.Services.AddSingleton<WorkflowTypeApprovalPipeline>();
builder.Services.AddSingleton<IWorkflowTypeApprovalHandler, EmailApprovalNotificationHandler>();
builder.Services.AddSingleton<IVerdictRunDispatcher, RunServiceVerdictRunDispatcher>();
builder.Services.AddSingleton<IWorkflowTypeApprovalHandler, VerdictWorkflowApprovalHandler>();
builder.Services.AddSingleton<ConnectorService>();
builder.Services.AddSingleton<ConnectorBrowseService>();
builder.Services.AddSingleton<ConnectorTokenRefresher>();
builder.Services.AddSingleton<AccessGrantEvaluator>();
builder.Services.AddSingleton<ConnectorAccessPolicy>();
builder.Services.AddSingleton<WorkspaceResourceService>();
builder.Services.AddSingleton<PlatformSettingsService>();
// Binds the governance default-access posture (used by PolicyEngine and the dispatch gates)
// to the runtime security.default-resource-access setting.
builder.Services.AddSingleton<Auxilia.Governance.Policy.IDefaultResourceAccessPolicy,
    PlatformDefaultResourceAccessPolicy>();
builder.Services.AddSingleton<DelegatedTokenStore>();
builder.Services.AddSingleton<SlotBindingSecrets>();
builder.Services.AddSingleton<RunConfigurationService>();
builder.Services.AddSingleton<RunQuotaService>();
builder.Services.AddSingleton<RunService>();
builder.Services.AddSingleton<RunReadService>();
builder.Services.AddSingleton<RunAccessService>();
builder.Services.AddSingleton<DashboardPinService>();
builder.Services.AddSingleton<AuditReadService>();
builder.Services.AddSingleton<ProviderCatalogService>();
builder.Services.AddSingleton<EnvironmentLayerService>();
builder.Services.AddSingleton<EnvironmentBaseService>();
builder.Services.AddSingleton<WorkflowSchemaReadService>();
builder.Services.AddSingleton<PrincipalAdminService>();
builder.Services.AddSingleton<SlotCredentialResolver>();
builder.Services.AddSingleton<RunStreamBroker>();
builder.Services.AddSingleton<ArtifactStreamBroker>();
builder.Services.AddSingleton<EventStreamBroker>();
// Artifact payloads come from the shared payload backend (ArtifactStore:PayloadRoot points at
// the same location the runner writes); metadata is mirrored from the bus, never read from
// the runner's index.
var artifactStoreSettings = new Auxilia.PlatformData.Artifacts.ArtifactStoreSettings();
builder.Configuration.GetSection("ArtifactStore").Bind(artifactStoreSettings);
builder.Services.AddSingleton(artifactStoreSettings);
builder.Services.AddSingleton<Auxilia.PlatformData.Artifacts.IArtifactPayloadReader,
    Auxilia.PlatformData.Artifacts.FileSystemArtifactPayloadReader>();
// Run input files live encrypted beside the artifact payloads — storage every Core node shares.
builder.Services.AddSingleton(new RunInputFileStorage(
    Path.Combine(artifactStoreSettings.ResolveRoot(platformData), "run-input-files")));
builder.Services.AddSingleton<RunInputFileService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RunInputFileService>());
// Run checkpoints too: Core-written and Core-encrypted, in their own folder of that storage.
builder.Services.AddSingleton(new RunCheckpointStorage(
    Path.Combine(artifactStoreSettings.ResolveRoot(platformData), "run-checkpoints")));
builder.Services.AddSingleton<CheckpointLifetimePolicy>();
builder.Services.AddSingleton<RunCheckpointService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RunCheckpointService>());
builder.Services.AddSingleton<Auxilia.Workflows.Messaging.WorkflowStatusPublisher>();
builder.Services.AddSingleton<RunnerLivenessTracker>();
builder.Services.AddSingleton<TerminalTicketService>();
builder.Services.AddSingleton<PendingPackageDownloadTokenService>();
builder.Services.AddSingleton<ElevationTicketService>();
builder.Services.AddSingleton<TerminalProxyService>();
builder.Services.AddSingleton<FailoverMonitor>();
builder.Services.AddHostedService<RunTrackingService>();
builder.Services.AddHostedService<RunViewTrackingService>();
builder.Services.AddHostedService<WorkflowSchemaTrackingService>();
// Resolvable singleton: the run-stream SSE endpoint primes command-id aliases on it.
builder.Services.AddSingleton<RunStreamPublisher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RunStreamPublisher>());
builder.Services.AddHostedService<ArtifactTrackingService>();
// Runner audit records join the Core's central, chained trail.
builder.Services.AddHostedService<AuditIngestionService>();
// The audit chain's external anchor: every stored record's head, signed, outside the audit store.
builder.Services.AddSingleton(sp => new Auxilia.PlatformData.AuditCheckpointFile(
    sp.GetRequiredService<IOptions<CoreApiSettings>>().Value.AuditCheckpointPath
    ?? Path.Combine(platformData.JsonDirectory, "audit-checkpoints.jsonl"),
    sp.GetRequiredService<Auxilia.PlatformData.AuditIntegrity>(), TimeProvider.System));
builder.Services.AddSingleton<Auxilia.PlatformData.IAuditAnchor>(
    sp => sp.GetRequiredService<Auxilia.PlatformData.AuditCheckpointFile>());
builder.Services.AddHostedService<ArtifactStreamPublisher>();
builder.Services.AddHostedService<EventTrackingService>();
builder.Services.AddHostedService<EventStreamPublisher>();
builder.Services.AddHostedService<RunLifecycleEventPublisher>();
// Resolve the same FailoverMonitor instance for the hosted lifecycle (so tests can drive ScanOnceAsync).
builder.Services.AddHostedService(sp => sp.GetRequiredService<FailoverMonitor>());

// --- MCP: first-class, authenticated AI/service parity ---
builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<Auxilia.Core.Api.Mcp.CoreMcpTools>();

// --- OpenTelemetry (tracing + metrics) ---
// A caller's W3C trace context continues through the Core: a request joins the caller's trace,
// the bus messages it sends carry it on to the runner (whose own messages and calls back carry
// it here again), and its outgoing HTTP calls pass it along. The SDK is what activates the
// propagation at all; spans and metrics are exported over OTLP only when a collector is named
// (Otlp:Endpoint, or the standard OTEL_EXPORTER_OTLP_ENDPOINT).
var otlpEndpoint = builder.Configuration["Otlp:Endpoint"];
var exportsOtlp = !string.IsNullOrWhiteSpace(otlpEndpoint)
                  || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"));
void ConfigureOtlp(OtlpExporterOptions options)
{
    if (string.IsNullOrWhiteSpace(otlpEndpoint))
        return; // the exporter reads the standard OTEL_EXPORTER_OTLP_* variables itself
    options.Endpoint = new Uri(otlpEndpoint);
    options.Protocol = OtlpExportProtocol.Grpc;
}
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(
        serviceName: "Auxilia.Core.Api",
        serviceVersion: System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()
            ?.InformationalVersion ?? "0.0.0"))
    .WithTracing(tracing =>
    {
        tracing
            .AddSource(MessagingTelemetry.ActivitySourceName)
            .AddSource("System.Net.Http")
            .AddAspNetCoreInstrumentation();
        if (exportsOtlp)
            tracing.AddOtlpExporter(ConfigureOtlp);
    })
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter(MessagingTelemetry.MeterName)
            .AddAspNetCoreInstrumentation();
        if (exportsOtlp)
            metrics.AddOtlpExporter(ConfigureOtlp);
    });

var app = builder.Build();

// Without a protection key every secret the Core keeps — connector credentials, run input files,
// run checkpoints — is stored in the clear: a trusted-operator development posture only.
if (string.IsNullOrWhiteSpace(platformData.ProtectionKeyBase64))
    app.Logger.LogWarning(
        "PlatformData:ProtectionKeyBase64 is not configured — secrets, run input files and checkpoints are stored unprotected (dev only).");

// A keyed audit chain is the production posture: its anchor belongs on write-once storage the
// database writers cannot touch — the default path beside the data is a development choice.
if (!string.IsNullOrWhiteSpace(platformData.ProtectionKeyBase64)
    && string.IsNullOrWhiteSpace(app.Services.GetRequiredService<IOptions<CoreApiSettings>>().Value.AuditCheckpointPath))
    app.Logger.LogWarning(
        "The audit anchor uses its default path {Path}; production must point CoreApi:AuditCheckpointPath at write-once storage.",
        app.Services.GetRequiredService<Auxilia.PlatformData.AuditCheckpointFile>().Path);

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

// Non-browser (desktop/CLI) sign-in: username + password exchanged for the SAME per-user bearer
// the browser path mints — per-user audit/SoD applies to desktop clients too. Anonymous by
// design (it IS the sign-in); both outcomes are audited. The desktop lifetime is longer than the
// console token: there is no cookie session to silently re-mint from, and the client must not
// hold the password to renew.
app.MapPost("/auth/login", async (
        PasswordLoginRequest request, Auxilia.Governance.Identity.IIdentityProvider identity,
        UserBearerTokenService tokens, PlatformSettingsService platformSettings,
        LoginAttemptThrottle throttle, AuditLog audit, CancellationToken ct) =>
{
    var username = request.Username?.Trim() ?? "";
    // Per-username failure throttle (the per-IP fixed window rides the endpoint's rate-limit
    // policy): refused BEFORE the password is even checked, so a throttled attacker learns nothing.
    if (throttle.IsBlocked(username))
    {
        await audit.AppendAsync(username, "auth.login", username, "rate-limited", ct: ct);
        return Results.Json(new { error = "too many failed attempts; try again later" },
            statusCode: StatusCodes.Status429TooManyRequests);
    }
    var session = await identity.AuthenticatePasswordAsync(username, request.Password ?? "", ct);
    if (session is null)
    {
        throttle.RecordFailure(username);
        await audit.AppendAsync(username, "auth.login", username, "denied", ct: ct);
        return Results.Json(new { error = "invalid credentials" }, statusCode: StatusCodes.Status401Unauthorized);
    }
    throttle.RecordSuccess(username);
    var lifetime = await platformSettings.GetLoginTokenLifetimeAsync(ct);
    var (token, expiresUtc) = tokens.Issue(session.PrincipalId, lifetime);
    await audit.AppendAsync(
        session.PrincipalId.ToString(), "auth.login", session.PrincipalId.ToString(), "granted", ct: ct);
    return Results.Ok(new UserBearerToken(token, expiresUtc));
}).AllowAnonymous().RequireRateLimiting("auth-login");

// Step-up: re-prove the caller's OWN credential (password / API key) to obtain a short-lived
// elevation for security-sensitive administration. Throttled exactly like the login — the per-IP
// "auth-login" window plus the per-account failure throttle keyed by PRINCIPAL — so a leaked
// bearer cannot be brute-forced into an elevation. Every outcome is audited.
app.MapPost("/auth/step-up", async (
        StepUpRequest request, HttpContext http, PrincipalDirectory directory,
        ElevationTicketService elevation, LoginAttemptThrottle throttle, AuditLog audit, CancellationToken ct) =>
{
    if (CoreClaims.PrincipalIdOf(http.User) is not { } principalId)
        return Results.Unauthorized();
    var actor = principalId.ToString();
    if (throttle.IsBlocked(principalId))
    {
        await audit.AppendAsync(actor, "auth.step-up", actor, "rate-limited", ct: ct);
        return Results.Json(new { error = "too many failed attempts; try again later" },
            statusCode: StatusCodes.Status429TooManyRequests);
    }
    if (!await directory.VerifySecretAsync(principalId, request.Secret, ct))
    {
        throttle.RecordFailure(principalId);
        await audit.AppendAsync(actor, "auth.step-up", actor, "denied", ct: ct);
        return Results.Json(new { error = "the credential was not accepted" }, statusCode: StatusCodes.Status403Forbidden);
    }
    throttle.RecordSuccess(principalId);
    var (token, expiresUtc) = elevation.Issue(principalId);
    await audit.AppendAsync(actor, "auth.step-up", actor, "granted", ct: ct);
    return Results.Ok(new ElevationTicket(token, expiresUtc));
}).RequireAuthorization().RequireRateLimiting("auth-login");

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
        // A delegated run runs for the target; its starter keeps access to it (run access, §16).
        var accepted = await runs.RunInlineAsync(request, triggeringPrincipal, ct,
            startedBy: onBehalfOf ? principalId : null);
        if (onBehalfOf)
            await audit.AppendAsync(principalId.ToString(), PermissionActions.RunOnBehalfOf,
                triggeringPrincipal.ToString(), "granted",
                $"{{\"workflowType\":\"{request.WorkflowType}\",\"runId\":\"{accepted.RunId}\"}}", ct);
        return Results.Ok(accepted);
    }
    catch (RunQuotaExceededException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status429TooManyRequests);
    }
    catch (RunAccessDeniedException ex)
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

// Run reads are scoped per run (ARCHITECTURE §16 "Run access"): a caller sees the runs it runs
// for, started on someone's behalf, or was granted — administrators see all. Someone else's run is
// indistinguishable from a missing one.
app.MapGet("/api/runs", async (
        string? state, string? workflowType, Guid? configurationId, HttpContext http,
        RunAccessService access, RunReadService svc, CancellationToken ct, int skip = 0, int take = 50) =>
    await access.ViewerForAsync(http.User, ct) is { } viewer
        ? Results.Ok(await svc.QueryAsync(
            new RunQuery(state, workflowType, configurationId, skip, take == 0 ? 50 : take), viewer, ct))
        : Results.Unauthorized())
    .RequireAuthorization();

// Server-side aggregate counts over the caller's visible runs — the dashboard's stat tiles, no
// page scraping. (Must be mapped before the {id:guid} route only by convention; the guid
// constraint already disambiguates.)
app.MapGet("/api/runs/stats", async (
        HttpContext http, RunAccessService access, RunReadService svc, CancellationToken ct) =>
    await access.ViewerForAsync(http.User, ct) is { } viewer
        ? Results.Ok(await svc.GetStatsAsync(viewer, ct))
        : Results.Unauthorized())
    .RequireAuthorization();

app.MapGet("/api/runs/{id:guid}", async (
        Guid id, HttpContext http, RunAccessService access, CancellationToken ct) =>
{
    if (await access.ViewerForAsync(http.User, ct) is not { } viewer)
        return Results.Unauthorized();
    return await access.FindAsync(viewer, id, surface: null, ct) is { } run
        ? Results.Ok(RunReadService.ToStatus(run))
        : Results.NotFound();
}).RequireAuthorization();

// Share a run (or stop sharing it): the principal it runs for, the one that started it on their
// behalf, or an administrator replaces its grants — the connector sharing model. Audited.
app.MapPut("/api/runs/{id:guid}/grants", async (
        Guid id, SetRunGrants request, HttpContext http, RunAccessService access, AuditLog audit,
        CancellationToken ct) =>
{
    if (await access.ViewerForAsync(http.User, ct) is not { } viewer)
        return Results.Unauthorized();
    if (await access.FindAsync(viewer, id, "grants", ct) is not { } run)
        return Results.NotFound();
    if (!viewer.MayShare(run))
        return Results.Json(new { error = "only the principal the run runs for, its starter, or an administrator may share it" },
            statusCode: StatusCodes.Status403Forbidden);
    try
    {
        if (await access.SetGrantsAsync(run.Id, request.Grants ?? [], ct) is not { } updated)
            return Results.NotFound();
        await audit.AppendAsync(viewer.PrincipalId.ToString("D"), "run.grants-set", run.Id.ToString(),
            System.Text.Json.JsonSerializer.Serialize(request.Grants ?? []), ct: ct);
        return Results.Ok(RunReadService.ToStatus(updated));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization();

// --- Dashboard pins (personal): a principal keeps chosen run views on their dashboard ---

app.MapGet("/api/dashboard/pins", async (
        HttpContext http, DashboardPinService svc, CancellationToken ct) =>
    CoreClaims.PrincipalIdOf(http.User) is { } principalId
        ? Results.Ok(await svc.ListAsync(principalId, ct))
        : Results.Unauthorized())
    .RequireAuthorization();

app.MapPost("/api/dashboard/pins", async (
        CreateDashboardPin request, HttpContext http, RunAccessService access, DashboardPinService svc,
        CancellationToken ct) =>
{
    if (await access.ViewerForAsync(http.User, ct) is not { } viewer)
        return Results.Unauthorized();
    // Only a run the caller may see can be pinned.
    if (await access.FindAsync(viewer, request.RunId, surface: null, ct) is null)
        return Results.NotFound();
    return await svc.PinAsync(viewer.PrincipalId, request, ct) is { } pin
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
        Guid id, HttpContext http, IPolicyEngine policy, RunService runs, RunAccessService access,
        CancellationToken ct) =>
{
    if (await access.ViewerForAsync(http.User, ct) is not { } viewer)
        return Results.Unauthorized();
    if (await access.FindAsync(viewer, id, "cancel", ct) is not { } run)
        return Results.NotFound();
    var decision = await policy.EvaluateAsync(
        new PolicyContext(viewer.PrincipalId, PermissionActions.WorkflowCancel, id.ToString())
        { WorkflowType = run.WorkflowType }, ct);
    if (!decision.Allowed)
        return Results.Json(new { error = decision.Reason }, statusCode: StatusCodes.Status403Forbidden);
    // Callers may hold the DISPATCH id; the runner stops containers by INSTANCE id — cancel
    // with the resolved run's id. A still-Dispatched run is cancelled by the Core itself inside
    // CancelAsync (no runner owns it yet).
    await runs.CancelAsync(run.Id, ct);
    return Results.Accepted($"/api/runs/{id}");
}).RequireAuthorization();

// Rerun: re-dispatch a past run from its stored dispatch command (fresh id + token, bindings
// re-stashed, connector eligibility re-checked). Only a run the caller may reach; authorized like
// any trigger. Audited.
app.MapPost("/api/runs/{id:guid}/rerun", async (
        Guid id, HttpContext http, IPolicyEngine policy, RunService runs, RunAccessService access,
        AuditLog audit, CancellationToken ct) =>
{
    if (await access.ViewerForAsync(http.User, ct) is not { } viewer)
        return Results.Unauthorized();
    var principalId = viewer.PrincipalId;
    if (await access.FindAsync(viewer, id, "rerun", ct) is not { } record)
        return Results.NotFound();
    var decision = await policy.EvaluateAsync(
        new PolicyContext(principalId, PermissionActions.WorkflowTrigger, id.ToString())
        { WorkflowType = record.WorkflowType }, ct);
    if (!decision.Allowed)
        return Results.Json(new { error = decision.Reason }, statusCode: StatusCodes.Status403Forbidden);
    try
    {
        // A rerun of a personal configuration's run is gated by the configuration's visibility
        // exactly like POST /api/configurations/{id}/run — managers see every configuration.
        var accepted = await runs.RerunAsync(record, principalId, ct,
            seesAllConfigurations: CoreClaims.HasRolePermission(http.User, PermissionActions.WorkflowConfigurationManage));
        await audit.AppendAsync(principalId.ToString(), "workflow.rerun",
            record.Id.ToString(), accepted.RunId.ToString(), ct: ct);
        return Results.Ok(accepted);
    }
    catch (RunQuotaExceededException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status429TooManyRequests);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (RunAccessDeniedException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
    }
}).RequireAuthorization();

// Live-view stream (SSE): the run's status and view items until the run reaches a terminal state
// or the client disconnects — RESUMABLE: every frame's id is the position after it, and a client
// sending one back (Last-Event-ID, or ?after= on a first connect) gets everything after it exactly
// once, in order: stored items first, then the live feed from the bus (RunStreamSession).
app.MapGet("/api/runs/{id:guid}/stream", async (
        Guid id, string? after, HttpContext http, IPolicyEngine policy, RunAccessService access,
        RunStreamBroker broker, RunStreamPublisher streamPublisher,
        Auxilia.UniversalDataAccess.IDataAccess<CoreRunRecord> runRecords,
        Auxilia.UniversalDataAccess.IDataAccess<CoreRunViewRecord> viewRecords,
        Microsoft.Extensions.Options.IOptions<CoreApiSettings> apiSettings, TimeProvider clock,
        ILoggerFactory loggers, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.RunObserve, ct) is { } fail)
    {
        await fail.ExecuteAsync(http);
        return;
    }
    if (await access.ViewerForAsync(http.User, ct) is not { } viewer)
    {
        await Results.Unauthorized().ExecuteAsync(http);
        return;
    }

    // Where the client stands: a browser's EventSource sends Last-Event-ID on every reconnect,
    // while ?after= carries a position the client kept from an earlier stream — the header is
    // the newer of the two.
    RunStreamCursor? resumeAfter = null;
    var position = http.Request.Headers["Last-Event-ID"].ToString() is { Length: > 0 } lastEventId
        ? lastEventId
        : after;
    if (position is { Length: > 0 })
    {
        if (!RunStreamCursor.TryParse(position, out var parsed))
        {
            await Results.BadRequest(new { error = "not a stream position this Core issued" }).ExecuteAsync(http);
            return;
        }
        resumeAfter = parsed;
    }

    // SUBSCRIBE FIRST, then read the record and the store: whatever lands in between arrives live
    // as well and is dropped by sequence — never lost. Nothing is written to the response before
    // the run is authorized.
    using var subscription = await broker.SubscribeAsync(id, ct);

    // The id may be the dispatch COMMAND id rather than the runner's instance id. Selective
    // routing binds by key, so resolve the pairing from the tracked runs and prime the alias —
    // a late subscriber cannot rely on observing the claim transition on the bus.
    var record = await runRecords.ReadAsync(id, ct);
    var aliasOf = (Guid?)null;
    if (record is null
        && (await runRecords.ReadAsync(ct)).FirstOrDefault(r => r.CommandId == id) is { } byCommand)
    {
        aliasOf = byCommand.Id;
        record = byCommand;
    }

    // Per-run authorization before a single frame: the stream carries the run's content
    // (conversations, repository excerpts). A run exists from its dispatch on, so an id without a
    // record is unknown — and someone else's run is indistinguishable from one.
    if (record is null || !await access.AdmitAsync(viewer, record, "stream", ct))
    {
        await Results.NotFound().ExecuteAsync(http);
        return;
    }
    if (aliasOf is { } instanceId)
        await streamPublisher.RegisterAliasAsync(id, instanceId, ct);

    // A client that already holds everything a finished run will ever stream is told so: 204 is
    // also what stops a browser's EventSource from reconnecting.
    if (await RunStreamSession.IsCompleteAsync(record, resumeAfter, viewRecords, ct))
    {
        await Results.NoContent().ExecuteAsync(http);
        return;
    }

    http.Response.Headers.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";
    http.Response.Headers["X-Accel-Buffering"] = "no";

    // Flush headers so the client's SendAsync completes with the subscription already registered —
    // no live event published after this point is lost.
    await http.Response.Body.FlushAsync(ct);

    try
    {
        var session = new RunStreamSession(
            id, record, resumeAfter, subscription.Reader, runRecords, viewRecords,
            async (text, token) =>
            {
                await http.Response.WriteAsync(text, token);
                await http.Response.Body.FlushAsync(token);
            },
            clock,
            TimeSpan.FromSeconds(apiSettings.Value.SseKeepaliveSeconds),
            TimeSpan.FromMilliseconds(apiSettings.Value.StreamGapWaitMilliseconds),
            loggers.CreateLogger<RunStreamSession>());
        await session.RunAsync(ct);
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
        Guid id, ProvideRunInput request, HttpContext http, IPolicyEngine policy, RunAccessService access,
        IMessageBusClient bus, AuditLog audit, CancellationToken ct) =>
{
    if (CoreClaims.PrincipalIdOf(http.User) is not { } principalId)
        return Results.Unauthorized();
    var decision = await policy.EvaluateAsync(
        new PolicyContext(principalId, PermissionActions.RunProvideInput, id.ToString()), ct);
    if (!decision.Allowed)
        return Results.Json(new { error = decision.Reason }, statusCode: StatusCodes.Status403Forbidden);

    // The caller may hold the dispatch CommandId (what run-accept returned) or the instance id —
    // and may steer only a run it may reach.
    var viewer = await access.ViewerForAsync(principalId, ct);
    if (await access.FindAsync(viewer, id, "inputs", ct) is not { } run)
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
        Guid id, HttpContext http, IPolicyEngine policy, RunAccessService access,
        TerminalTicketService tickets, AuditLog audit, CancellationToken ct) =>
{
    if (CoreClaims.PrincipalIdOf(http.User) is not { } principalId)
        return Results.Unauthorized();
    var decision = await policy.EvaluateAsync(
        new PolicyContext(principalId, PermissionActions.RunOpenTerminal, id.ToString()), ct);
    if (!decision.Allowed)
        return Results.Json(new { error = decision.Reason }, statusCode: StatusCodes.Status403Forbidden);

    // A ticket is the terminal's only key: issued for a run the caller may reach, nothing else.
    var viewer = await access.ViewerForAsync(principalId, ct);
    if (await access.FindAsync(viewer, id, "terminal", ct) is not { } run)
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
        HttpContext http, IPolicyEngine policy, RunAccessService access,
        Auxilia.UniversalDataAccess.IDataAccess<CoreRunRecord> runStore,
        Auxilia.UniversalDataAccess.IDataAccess<CoreRunViewRecord> viewStore,
        Auxilia.UniversalDataAccess.IDataAccess<CoreArtifactRecord> artifactStore,
        AuditLog audit, TimeProvider clock, CancellationToken ct,
        bool includeStale = false, int staleMinutes = 30) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.WorkflowConfigurationManage, ct) is { } fail)
        return fail;
    // Only runs the caller may see are cleared — administrators clear everyone's.
    var viewer = await access.ViewerForAsync(CoreClaims.PrincipalIdOf(http.User)!.Value, ct);
    var staleCutoff = clock.GetUtcNow() - TimeSpan.FromMinutes(Math.Max(1, staleMinutes));
    // A run that still holds checkpoints stays until they are deleted or expire: its principals
    // reach them through the run, so clearing it would strand what it hands back.
    var holding = (await artifactStore.ReadAsync(ct))
        .Where(a => a.Kind == ArtifactKinds.Checkpoint)
        .Select(a => a.RunInstanceId)
        .ToHashSet();
    var clearable = (await runStore.ReadAsync(ct))
        .Where(viewer.CanSee)
        .Where(r => CoreRunStates.IsTerminal(r.State)
                    || (includeStale && r.UpdatedUtc < staleCutoff))
        .ToList();
    var doomed = clearable.Where(r => !holding.Contains(r.Id)).ToList();
    var retained = clearable.Count - doomed.Count;
    foreach (var run in doomed)
    {
        foreach (var view in (await viewStore.ReadAsync(ct)).Where(v => v.RunId == run.Id).ToList())
            await viewStore.RemoveAsync(view.Id, ct);
        await runStore.RemoveAsync(run.Id, ct);
    }
    await audit.AppendAsync(
        CoreClaims.PrincipalIdOf(http.User)!.Value.ToString("D"), "run.clear-history",
        "runs", "cleared",
        $"{{\"deleted\":{doomed.Count},\"retainedForCheckpoints\":{retained},\"includeStale\":{includeStale.ToString().ToLowerInvariant()}}}", ct);
    return Results.Ok(new { deleted = doomed.Count, retainedForCheckpoints = retained });
}).RequireAuthorization();

// The read-later counterpart of the live stream: a run's persisted view items (outputs), so a
// client can inspect results after the run finished. Gated like observing the live stream.
app.MapGet("/api/runs/{id:guid}/views", async (
        Guid id, string? view, long? after, HttpContext http, IPolicyEngine policy, RunAccessService access,
        Auxilia.UniversalDataAccess.IDataAccess<CoreRunViewRecord> views,
        CancellationToken ct, int skip = 0, int take = 200) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.RunObserve, ct) is { } fail)
        return fail;
    // Callers may hold the DISPATCH id (RunAccepted.RunId) — resolved to the instance id like
    // every other run read (the runner records views under its own instance id) — and read only
    // a run they may reach.
    if (await access.ViewerForAsync(http.User, ct) is not { } viewer
        || await access.FindAsync(viewer, id, "views", ct) is not { } run)
        return Results.NotFound();
    var runId = run.Id;
    var all = (await views.ReadAsync(ct)).Where(v => v.RunId == runId);
    if (!string.IsNullOrWhiteSpace(view))
        all = all.Where(v => string.Equals(v.ViewName, view, StringComparison.Ordinal));
    // Sequences are run-wide: their order IS the publish order, and ?after= continues from a
    // stream position (or a previous page) without overlap.
    if (after is { } from)
        all = all.Where(v => v.Sequence > from);
    var ordered = all.OrderBy(v => v.Sequence).ToList();
    var effectiveTake = take <= 0 ? 200 : take;
    var page = ordered.Skip(skip).Take(effectiveTake)
        .Select(v => new RunViewItem(v.ViewName, v.Sequence, v.PayloadJson, v.TimestampUtc))
        .ToList();
    return Results.Ok(new PagedResult<RunViewItem>(page, ordered.Count, skip, effectiveTake));
}).RequireAuthorization();

// --- Artifacts (client surface; metadata mirrored from the bus, payloads from the shared backend) ---

// Artifacts are run output (incl. the platform-captured container and egress logs): every
// artifact surface shows only artifacts of runs the caller may reach (§16 "Run access").
app.MapGet("/api/artifacts", async (
        string? artifactType, string? workItemId, Guid? runId, DateTimeOffset? createdAfterUtc,
        HttpContext http, IPolicyEngine policy, RunAccessService access,
        Auxilia.UniversalDataAccess.IDataAccess<CoreArtifactRecord> artifacts,
        Auxilia.UniversalDataAccess.IDataAccess<CoreRunRecord> runStore,
        RunReadService runReads,
        CancellationToken ct, int skip = 0, int take = 50) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.ArtifactConsume, ct) is { } fail)
        return fail;
    var viewer = await access.ViewerForAsync(CoreClaims.PrincipalIdOf(http.User)!.Value, ct);
    var visibleRuns = (await runStore.ReadAsync(ct)).Where(viewer.CanSee).Select(r => r.Id).ToHashSet();
    var all = (await artifacts.ReadAsync(ct)).AsEnumerable()
        .Where(a => viewer.IsAdministrator || visibleRuns.Contains(a.RunInstanceId));
    if (!string.IsNullOrWhiteSpace(artifactType))
        all = all.Where(a => string.Equals(a.ArtifactType, artifactType, StringComparison.Ordinal));
    if (!string.IsNullOrWhiteSpace(workItemId))
        all = all.Where(a => string.Equals(a.WorkItemId, workItemId, StringComparison.Ordinal));
    if (runId is { } run)
    {
        // Callers hold the DISPATCH id (RunAccepted.RunId) while artifacts are recorded under
        // the runner's instance id — resolve through the run record like every other run read.
        var effectiveRunId = (await runReads.GetRecordAsync(run, ct))?.Id ?? run;
        all = all.Where(a => a.RunInstanceId == effectiveRunId);
    }
    if (createdAfterUtc is { } after)
        all = all.Where(a => a.CreatedUtc > after);
    // The catch-up shape (createdAfterUtc) pages oldest-first so a reconnecting consumer drains
    // the gap deterministically; the browse shape stays newest-first.
    var ordered = createdAfterUtc is null
        ? all.OrderByDescending(a => a.CreatedUtc).ToList()
        : all.OrderBy(a => a.CreatedUtc).ToList();
    var effectiveTake = take <= 0 ? 50 : take;
    var page = ordered.Skip(skip).Take(effectiveTake).Select(a => a.ToDto()).ToList();
    return Results.Ok(new PagedResult<ArtifactDto>(page, ordered.Count, skip, effectiveTake));
}).RequireAuthorization();

app.MapGet("/api/artifacts/{id:guid}", async (
        Guid id, HttpContext http, IPolicyEngine policy, RunAccessService access,
        Auxilia.UniversalDataAccess.IDataAccess<CoreArtifactRecord> artifacts, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.ArtifactConsume, ct) is { } fail)
        return fail;
    var viewer = await access.ViewerForAsync(CoreClaims.PrincipalIdOf(http.User)!.Value, ct);
    return await artifacts.ReadAsync(id, ct) is { } a && await access.CanSeeRunAsync(viewer, a.RunInstanceId, ct)
        ? Results.Ok(a.ToDto())
        : Results.NotFound();
}).RequireAuthorization();

// Payload download: metadata must be mirrored AND the payload present in the shared backend
// (ArtifactStore:PayloadRoot); a missing payload is a deployment wiring gap, logged as such.
app.MapGet("/api/artifacts/{id:guid}/content", async (
        Guid id, HttpContext http, IPolicyEngine policy, RunAccessService access,
        Auxilia.UniversalDataAccess.IDataAccess<CoreArtifactRecord> artifacts,
        Auxilia.PlatformData.Artifacts.IArtifactPayloadReader payloads, RunCheckpointService checkpoints,
        AuditLog audit, ILogger<Program> logger, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.ArtifactConsume, ct) is { } fail)
        return fail;
    if (await artifacts.ReadAsync(id, ct) is not { } artifact)
        return Results.NotFound();
    // The payload is the run's content: only for a caller who may reach that run (an
    // administrator reading someone else's is audited).
    var viewer = await access.ViewerForAsync(CoreClaims.PrincipalIdOf(http.User)!.Value, ct);
    if (await access.ResolveAsync(artifact.RunInstanceId, ct) is { } producingRun
            ? !await access.AdmitAsync(viewer, producingRun, "artifact", ct)
            : !viewer.IsAdministrator)
        return Results.NotFound();
    if (artifact.Kind == ArtifactKinds.Checkpoint)
    {
        // A checkpoint lives encrypted in the Core's own storage — decrypted for this download only.
        if (await checkpoints.ReadContentAsync(artifact, ct) is not { } content)
            return Results.NotFound(new { error = "the checkpoint is gone, past its lifetime, or unreadable" });
        await audit.AppendAsync(CoreClaims.PrincipalIdOf(http.User)?.ToString() ?? "unknown",
            PermissionActions.ArtifactConsume, id.ToString(),
            $"checkpoint {artifact.ArtifactType} v{artifact.Version}", ct: ct);
        var archive = artifact.MediaType == RunCheckpointService.ArchiveMediaType;
        return Results.File(content, artifact.MediaType ?? "application/octet-stream",
            $"{artifact.ArtifactType}-v{artifact.Version}{(archive ? ".tar.gz" : "")}");
    }
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

// Deleting a run checkpoint (ARCHITECTURE §9) — e.g. once its principals downloaded it: the
// principal the run runs for, its starter, and administrators; grantees only read. Every other
// artifact is the platform's record and stays.
app.MapDelete("/api/artifacts/{id:guid}", async (
        Guid id, HttpContext http, IPolicyEngine policy, RunAccessService access,
        Auxilia.UniversalDataAccess.IDataAccess<CoreArtifactRecord> artifacts,
        RunCheckpointService checkpoints, AuditLog audit, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.ArtifactConsume, ct) is { } fail)
        return fail;
    if (CoreClaims.PrincipalIdOf(http.User) is not { } principalId)
        return Results.Unauthorized();
    var artifact = await artifacts.ReadAsync(id, ct);
    var viewer = await access.ViewerForAsync(principalId, ct);
    switch (await access.MayDeleteAsync(viewer, artifact, ct))
    {
        case ArtifactDeletion.Hidden:
            return Results.NotFound();
        case ArtifactDeletion.Forbidden:
            return Results.Json(new { error = "only the run's own principals and administrators delete its checkpoints" },
                statusCode: StatusCodes.Status403Forbidden);
        case ArtifactDeletion.Kept:
            return Results.Json(new { error = "only run checkpoints can be deleted — the platform keeps every other artifact" },
                statusCode: StatusCodes.Status409Conflict);
    }
    var checkpoint = artifact!;
    if (!await checkpoints.DeleteAsync(checkpoint, ct))
        return Results.NotFound();
    await audit.AppendAsync(principalId.ToString(), "artifact.checkpoint-deleted", id.ToString(),
        $"{checkpoint.ArtifactType} v{checkpoint.Version} of run {checkpoint.RunInstanceId}", ct: ct);
    return Results.NoContent();
}).RequireAuthorization();

// Artifact SSE stream, SERVER-SIDE FILTERED (artifact type / work item): the client-surface
// replacement for a bus subscription — chaining libraries react to artifacts through this.
app.MapGet("/api/artifacts/stream", async (
        string? artifactType, string? workItemId, HttpContext http, IPolicyEngine policy,
        RunAccessService access, ArtifactStreamBroker broker,
        Microsoft.Extensions.Options.IOptions<CoreApiSettings> apiSettings, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.ArtifactConsume, ct) is { } fail)
    {
        await fail.ExecuteAsync(http);
        return;
    }
    var viewer = await access.ViewerForAsync(CoreClaims.PrincipalIdOf(http.User)!.Value, ct);

    http.Response.Headers.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";
    http.Response.Headers["X-Accel-Buffering"] = "no";

    using var subscription = await broker.SubscribeAsync(artifactType, workItemId, ct);
    // Flush headers so the client's SendAsync completes with the subscription already registered —
    // no live event published after this point is lost.
    await http.Response.Body.FlushAsync(ct);

    try
    {
        // The feed is shared; each subscriber receives only artifacts of runs it may reach.
        await SseWriter.PumpAsync(
            http.Response, subscription.Reader,
            TimeSpan.FromSeconds(apiSettings.Value.SseKeepaliveSeconds),
            isTerminal: _ => false, ct,
            include: (evt, token) => access.CanSeeRunAsync(viewer, evt.Artifact.RunInstanceId, token));
    }
    catch (OperationCanceledException)
    {
        // Client disconnected — expected end of an SSE stream.
    }
}).RequireAuthorization();

// --- Platform events: queryable mirror + server-side-filtered SSE (event type / work item).
// Published by workflows (SDK) and the platform itself (run lifecycle) — never via REST.
// Events a run published (and its run.* lifecycle events) belong to that run: only callers who
// may reach the run see them (§16 "Run access"); events without a source run stay role-gated.
app.MapGet("/api/events", async (
        string? eventType, string? workItemId, Guid? sourceRunId, DateTimeOffset? createdAfterUtc,
        DateTimeOffset? createdBeforeUtc,
        HttpContext http, IPolicyEngine policy, RunAccessService access,
        Auxilia.UniversalDataAccess.IDataAccess<CoreEventRecord> events,
        Auxilia.UniversalDataAccess.IDataAccess<CoreRunRecord> runStore,
        CancellationToken ct, int skip = 0, int take = 50) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.EventConsume, ct) is { } fail)
        return fail;
    var viewer = await access.ViewerForAsync(CoreClaims.PrincipalIdOf(http.User)!.Value, ct);
    var visibleRuns = (await runStore.ReadAsync(ct)).Where(viewer.CanSee).Select(r => r.Id).ToHashSet();
    var all = (await events.ReadAsync(ct)).AsEnumerable()
        .Where(e => e.SourceRunId is not { } source || viewer.IsAdministrator || visibleRuns.Contains(source));
    if (!string.IsNullOrWhiteSpace(eventType))
        all = all.Where(e => string.Equals(e.EventType, eventType, StringComparison.Ordinal));
    if (!string.IsNullOrWhiteSpace(workItemId))
        all = all.Where(e => string.Equals(e.WorkItemId, workItemId, StringComparison.Ordinal));
    if (sourceRunId is { } run)
        all = all.Where(e => e.SourceRunId == run);
    if (createdAfterUtc is { } after)
        all = all.Where(e => e.CreatedUtc > after);
    if (createdBeforeUtc is { } before)
        all = all.Where(e => e.CreatedUtc < before);
    // The catch-up shape (createdAfterUtc) pages oldest-first so a reconnecting consumer drains
    // the gap deterministically; the browse shape (incl. createdBeforeUtc) stays newest-first.
    var ordered = createdAfterUtc is null
        ? all.OrderByDescending(e => e.CreatedUtc).ToList()
        : all.OrderBy(e => e.CreatedUtc).ToList();
    var effectiveTake = take <= 0 ? 50 : take;
    var page = ordered.Skip(skip).Take(effectiveTake).Select(e => new EventDto(
        e.Id, e.EventType, e.WorkflowType, e.WorkItemId, e.SourceRunId, e.PayloadJson, e.CreatedUtc)).ToList();
    return Results.Ok(new PagedResult<EventDto>(page, ordered.Count, skip, effectiveTake));
}).RequireAuthorization();

app.MapGet("/api/events/{id:guid}", async (
        Guid id, HttpContext http, IPolicyEngine policy, RunAccessService access,
        Auxilia.UniversalDataAccess.IDataAccess<CoreEventRecord> events, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.EventConsume, ct) is { } fail)
        return fail;
    var viewer = await access.ViewerForAsync(CoreClaims.PrincipalIdOf(http.User)!.Value, ct);
    return await events.ReadAsync(id, ct) is { } e
           && (e.SourceRunId is not { } source || await access.CanSeeRunAsync(viewer, source, ct))
        ? Results.Ok(new EventDto(
            e.Id, e.EventType, e.WorkflowType, e.WorkItemId, e.SourceRunId, e.PayloadJson, e.CreatedUtc))
        : Results.NotFound();
}).RequireAuthorization();

// Event SSE stream, SERVER-SIDE FILTERED (event type / work item): the client-surface
// replacement for a bus subscription — event-trigger libraries react to events through this.
app.MapGet("/api/events/stream", async (
        string? eventType, string? workItemId, HttpContext http, IPolicyEngine policy,
        RunAccessService access, EventStreamBroker broker,
        Microsoft.Extensions.Options.IOptions<CoreApiSettings> apiSettings, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.EventConsume, ct) is { } fail)
    {
        await fail.ExecuteAsync(http);
        return;
    }
    var viewer = await access.ViewerForAsync(CoreClaims.PrincipalIdOf(http.User)!.Value, ct);

    http.Response.Headers.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";
    http.Response.Headers["X-Accel-Buffering"] = "no";

    using var subscription = await broker.SubscribeAsync(eventType, workItemId, ct);
    // Flush headers so the client's SendAsync completes with the subscription already registered —
    // no live event published after this point is lost.
    await http.Response.Body.FlushAsync(ct);

    try
    {
        // The feed is shared; each subscriber receives only events of runs it may reach.
        await SseWriter.PumpAsync(
            http.Response, subscription.Reader,
            TimeSpan.FromSeconds(apiSettings.Value.SseKeepaliveSeconds),
            isTerminal: _ => false, ct,
            include: async (evt, token) => evt.Event.SourceRunId is not { } source
                                           || await access.CanSeeRunAsync(viewer, source, token));
    }
    catch (OperationCanceledException)
    {
        // Client disconnected — expected end of an SSE stream.
    }
}).RequireAuthorization();

// Run input files (ARCHITECTURE §9): the caller uploads a file for a run it is about to start —
// the body IS the file. Held encrypted and the caller's own until a run request of its names it
// (RunRequest.InputFiles), deleted unused at its expiry or once that run has ended.
app.MapPost("/api/run-input-files", async (
        HttpContext http, IPolicyEngine policy, RunInputFileService inputFiles, AuditLog audit,
        Microsoft.Extensions.Options.IOptions<CoreApiSettings> apiSettings, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.WorkflowTrigger, ct) is { } fail)
        return fail;
    if (CoreClaims.PrincipalIdOf(http.User) is not { } principalId)
        return Results.Unauthorized();
    // The server's own body cap would cut a large file off before the configured limit applies.
    if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
        bodyLimit.MaxRequestBodySize = (long)apiSettings.Value.MaxRunInputFileBytes + 1;
    try
    {
        var file = await inputFiles.UploadAsync(principalId, http.Request.Body, ct);
        await audit.AppendAsync(principalId.ToString(), "run.input-file.uploaded", file.Id.ToString(),
            $"{file.Length} bytes", System.Text.Json.JsonSerializer.Serialize(new { sha256 = file.Sha256 }), ct);
        return Results.Ok(file);
    }
    catch (RunInputFileTooLargeException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status413PayloadTooLarge);
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
            error is SlotCredentialResolver.InvalidTokenError or SlotCredentialResolver.RunEndedError
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status404NotFound);
}).RequireRateLimiting("resolve-slot");

// A run's input file for the runner to place into the container before launch — the same gate as
// resolve-slot: the run's resolution token, refused once the run has ended (audited by the resolver).
app.MapGet("/internal/runs/{runId:guid}/input-files/{name}", async (
        Guid runId, string name, HttpContext http, SlotCredentialResolver resolver,
        RunInputFileService inputFiles, AuditLog audit, CancellationToken ct) =>
{
    var token = http.Request.Headers["X-Resolution-Token"].ToString();
    if (string.IsNullOrEmpty(token))
        return Results.Json(new { error = "missing resolution token" }, statusCode: StatusCodes.Status401Unauthorized);
    if (await resolver.AuthorizeAsync(runId, token, ct) is { Ok: false } refused)
        return Results.Json(new { error = refused.Error }, statusCode: StatusCodes.Status403Forbidden);
    if (await inputFiles.OpenAsync(runId, name, ct) is not { } file)
        return Results.NotFound();
    await audit.AppendAsync("core-runner", "run.input-file.delivered", runId.ToString(), name,
        System.Text.Json.JsonSerializer.Serialize(new { sha256 = file.Record.Sha256, bytes = file.Record.Length }), ct);
    return Results.File(file.Content, "application/octet-stream");
}).RequireRateLimiting("resolve-slot");

// A run checkpoint the runner read from the run's output directory (ARCHITECTURE §9) — the same
// gate: the run's resolution token, refused once the run has ended. The body IS the content; the
// Core encrypts it and keeps it as the newest copy of the run's checkpoint of that name.
app.MapPost("/internal/runs/{runId:guid}/checkpoints/{name}", async (
        Guid runId, string name, HttpContext http, SlotCredentialResolver resolver, RunAccessService access,
        WorkflowSchemaReadService schemas, RunCheckpointService checkpoints, AuditLog audit,
        Microsoft.Extensions.Options.IOptions<CoreApiSettings> apiSettings, CancellationToken ct) =>
{
    var token = http.Request.Headers["X-Resolution-Token"].ToString();
    if (string.IsNullOrEmpty(token))
        return Results.Json(new { error = "missing resolution token" }, statusCode: StatusCodes.Status401Unauthorized);
    if (await resolver.AuthorizeAsync(runId, token, ct) is { Ok: false } refused)
        return Results.Json(new { error = refused.Error }, statusCode: StatusCodes.Status403Forbidden);
    if (await access.ResolveAsync(runId, ct) is not { } run)
        return Results.NotFound(new { error = "the run is not recorded" });
    // Only for the instance the run's claim recorded: before the Core holds the claim it cannot
    // tell which instance runs the dispatch, and a guess could attach the copy to no run at all.
    if (!Guid.TryParse(http.Request.Headers["X-Workflow-Instance"].ToString(), out var instanceId))
        return Results.BadRequest(new { error = "the checkpoint names no instance" });
    if (run.Id != instanceId)
        return run.Id == runId
            ? Results.Conflict(new { error = "the run's claim is not recorded yet — save again shortly" })
            : Results.BadRequest(new { error = "the checkpoint names no instance of this run" });
    var declared = (await schemas.GetSchemaAsync(run.WorkflowType, ct))?.Checkpoints.FirstOrDefault(c => c.Name == name);
    if (declared is null)
        return Results.BadRequest(new { error = $"'{run.WorkflowType}' declares no checkpoint '{name}'" });
    if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
        bodyLimit.MaxRequestBodySize = (long)apiSettings.Value.MaxCheckpointBytes + 1;
    try
    {
        var saved = await checkpoints.SaveAsync(
            run, instanceId, name, http.Request.ContentType, http.Request.Body, http.Request.ContentLength,
            declared.LifetimeHours, ct);
        // Stored — recorded even when the runner stops waiting right now.
        await audit.AppendAsync("core-runner", "run.checkpoint.saved", saved.Id.ToString(),
            $"{name} v{saved.Version} of run {instanceId}",
            System.Text.Json.JsonSerializer.Serialize(new { sha256 = saved.ContentHash, bytes = saved.SizeBytes, expiresUtc = saved.ExpiresUtc }),
            CancellationToken.None);
        return Results.Ok(saved);
    }
    catch (RunCheckpointTooLargeException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status413PayloadTooLarge);
    }
}).RequireRateLimiting("checkpoint-save");

// The NON-SECRET setting values a runner needs to interpret a plugin's declared templates (its
// egress endpoints, e.g. tcp://{Host}:{Port}) — same token authorization as slot resolution;
// Secret-kind settings are never returned.
app.MapPost("/internal/runs/{runId:guid}/slot-settings", async (
        Guid runId, ResolveSlotSettingsRequest request, HttpContext http,
        SlotCredentialResolver resolver, CancellationToken ct) =>
{
    var token = http.Request.Headers["X-Resolution-Token"].ToString();
    if (string.IsNullOrEmpty(token))
        return Results.Json(new { error = "missing resolution token" }, statusCode: StatusCodes.Status401Unauthorized);
    var (success, error, values) = await resolver.ResolveSettingValuesAsync(
        runId, token, request.ProviderType, request.Keys ?? [], ct);
    return success
        ? Results.Ok(values)
        : Results.Json(new { error }, statusCode: StatusCodes.Status403Forbidden);
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

    // On-behalf-of: a service/automation caller (a trigger host's engines) may dispatch a stored
    // configuration AS a target principal. The caller must hold run.on-behalf-of, and the target must
    // itself pass workflow.trigger — the delegation grants no capability the target lacks. Absent a
    // distinct target the caller is both subject and triggering principal, exactly as a manual run.
    // The delegation gate runs BEFORE the visibility check: a caller who may not delegate must not
    // learn (via 404 vs. the later 403) whether the target can see the configuration.
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

    // A personal configuration is runnable only by whoever may SEE it: the triggering principal
    // (owner/granted), or a caller holding the configuration-management permission.
    var runViewer = new ConfigurationViewer(
        triggeringPrincipal,
        CoreClaims.HasRolePermission(http.User, PermissionActions.WorkflowConfigurationManage));
    if (!await configurations.IsVisibleAsync(id, runViewer, ct))
        return Results.NotFound();

    var decision = await policy.EvaluateAsync(
        new PolicyContext(triggeringPrincipal, PermissionActions.WorkflowTrigger, config.WorkflowType)
        { WorkflowType = config.WorkflowType }, ct);
    if (!decision.Allowed)
        return Results.Json(new { error = decision.Reason }, statusCode: StatusCodes.Status403Forbidden);
    try
    {
        var accepted = await runs.RunConfigurationAsync(id, triggeringPrincipal, context, ct,
            startedBy: delegated ? principalId : null);
        if (delegated)
            await audit.AppendAsync(principalId.ToString(), PermissionActions.RunOnBehalfOf,
                triggeringPrincipal.ToString(), "granted",
                $"{{\"configurationId\":\"{id}\",\"runId\":\"{accepted.RunId}\"}}", ct);
        return Results.Ok(accepted);
    }
    catch (RunQuotaExceededException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status429TooManyRequests);
    }
    catch (RunAccessDeniedException ex)
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

app.MapGet("/api/audit/integrity", async (
        HttpContext http, IPolicyEngine policy, AuditReadService svc, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.AuditRead, ct) is { } fail)
        return fail;
    return Results.Ok(await svc.VerifyAsync(ct));
}).RequireAuthorization();

// --- Provider catalog (Core-owned governance: which slot providers may be configured; deny-by-default) ---
// Availability gates whether a provider is OFFERED in configuration editors — it is a configuration-time
// allowlist, not a dispatch gate (a run is not rejected for using an unavailable provider), matching the
// BackendService semantics this was migrated from.
app.MapGet("/api/provider-catalog", async (
        bool? available, HttpContext http, IPolicyEngine policy, ProviderCatalogService svc,
        CancellationToken ct, int skip = 0, int take = 50) =>
{
    // The AVAILABLE slice is what configuration editors render their forms from — readable by
    // every authenticated principal (availability IS the admin's curation act). The full view,
    // unavailable entries included, stays a provider-catalog-manage surface.
    if (available != true
        && await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.ProviderCatalogManage, ct) is { } fail)
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
        string? search, HttpContext http, IPolicyEngine policy, EnvironmentLayerService svc, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.ProviderCatalogManage, ct) is { } fail)
        return fail;
    return Results.Ok(await svc.ListAsync(search, ct));
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

// Who may BIND the layer into a run: empty grants = open (the default); non-empty grants are
// enforced at dispatch against the triggering principal, like connector access.
app.MapPut("/api/environment-layers/{providerType}/grants", async (
        string providerType, SetEnvironmentLayerGrants request, HttpContext http, IPolicyEngine policy,
        EnvironmentLayerService svc, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.ProviderCatalogManage, ct) is { } fail)
        return fail;
    return await svc.SetGrantsAsync(CoreClaims.PrincipalIdOf(http.User), providerType, request.Grants, ct)
        is { } layer
        ? Results.Ok(layer)
        : Results.NotFound();
}).RequireAuthorization();

// Same grant mechanism for plain slot providers (stored on the same curation record).
app.MapPut("/api/provider-catalog/{providerType}/grants", async (
        string providerType, SetProviderGrants request, HttpContext http, IPolicyEngine policy,
        ProviderCatalogService svc, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.ProviderCatalogManage, ct) is { } fail)
        return fail;
    try
    {
        return Results.Ok(await svc.SetGrantsAsync(
            CoreClaims.PrincipalIdOf(http.User)?.ToString("D") ?? "core-api", providerType, request.Grants, ct));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
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

// Removes one base variant; removing the last variant removes the layer itself.
app.MapDelete("/api/environment-layers/{providerType}/variants/{baseEnvironment}", async (
        string providerType, string baseEnvironment, HttpContext http, IPolicyEngine policy,
        EnvironmentLayerService svc, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.ProviderCatalogManage, ct) is { } fail)
        return fail;
    return await svc.RemoveVariantAsync(CoreClaims.PrincipalIdOf(http.User), providerType, baseEnvironment, ct)
        is { } layer
        ? Results.Ok(layer)
        : Results.NotFound();
}).RequireAuthorization();

// --- Environment bases (the configurable (name, version) vocabulary layers build on) ---
app.MapGet("/api/environment-bases", async (
        string? search, HttpContext http, IPolicyEngine policy, EnvironmentBaseService svc, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.ProviderCatalogManage, ct) is { } fail)
        return fail;
    return Results.Ok(await svc.ListAsync(search, ct));
}).RequireAuthorization();

app.MapPost("/api/environment-bases", async (
        UpsertEnvironmentBase request, HttpContext http, IPolicyEngine policy,
        EnvironmentBaseService svc, CancellationToken ct) =>
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

app.MapDelete("/api/environment-bases/{name}/{version}", async (
        string name, string version, HttpContext http, IPolicyEngine policy,
        EnvironmentBaseService svc, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.ProviderCatalogManage, ct) is { } fail)
        return fail;
    return await svc.DeleteAsync(CoreClaims.PrincipalIdOf(http.User), name, version, ct)
        ? Results.NoContent()
        : Results.NotFound();
}).RequireAuthorization();

// --- Runner fleet (what the Core knows from bus heartbeats; feeds environment editors) ---
app.MapGet("/api/runners", async (
        HttpContext http, IPolicyEngine policy, Auxilia.Core.Api.Services.RunnerLivenessTracker liveness,
        IOptions<CoreApiSettings> apiSettings, TimeProvider clock, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.ProviderCatalogManage, ct) is { } fail)
        return fail;
    var cutoff = clock.GetUtcNow()
                 - TimeSpan.FromSeconds(apiSettings.Value.HeartbeatTimeoutSeconds);
    return Results.Ok(liveness.Snapshot()
        .Select(r => new RunnerDto(
            r.ServiceId, r.ServiceName, r.LastSeen, r.LastSeen >= cutoff,
            r.HostPlatform, r.HostArchitecture))
        .ToList());
}).RequireAuthorization();

// Fragment download for the RUNNER, authorized by the run's resolution token (same trust as the
// package download): only runs the Core dispatched can read layer content.
app.MapGet("/api/environment-layers/{providerType}/content", async (
        string providerType, Guid runId, string token, HttpContext http, EnvironmentLayerService svc,
        SlotCredentialResolver resolver, CancellationToken ct, string? @base = null) =>
{
    // Same gate as resolve-slot: digest match AND a run that has not ended (audited on refusal).
    if (await resolver.AuthorizeAsync(runId, token, ct) is { Ok: false } refused)
        return Results.Json(new { error = refused.Error }, statusCode: StatusCodes.Status403Forbidden);
    // The runner states the base it hosts; an omitted base keeps the linux default.
    var signed = await svc.ReadFragmentAsync(providerType, @base ?? EnvironmentBases.Linux, ct);
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

// --- Workflow-type access list: the Policy Engine's per-(type, action) entries. The FIRST
// entry for an action makes the list the EXCLUSIVE grant source for that (type, action) —
// enforced wherever the policy evaluates with a workflow type (dispatch included). This is
// the REST twin of the MCP access tools; all gated policy.administer.
app.MapGet("/api/workflow-types/{type}/access", async (
        string type, HttpContext http, IPolicyEngine policy,
        Auxilia.Governance.WorkflowTypeAccessStore accessStore, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.PolicyAdminister, ct) is { } fail)
        return fail;
    return Results.Ok((await accessStore.ListAsync(type, ct))
        .Select(e => new WorkflowTypeAccessEntryDto(e.Action, e.RoleName, e.PrincipalId, e.GroupId))
        .ToList());
}).RequireAuthorization();

static IResult? ValidateAccessChange(WorkflowTypeAccessChange request)
{
    var subjects = new object?[] { request.RoleName, request.PrincipalId, request.GroupId }
        .Count(s => s is string { Length: > 0 } or Guid);
    if (subjects != 1)
        return Results.BadRequest(new { error = "provide exactly one subject: roleName, principalId, or groupId." });
    if (request.RoleName is { Length: > 0 } role && !Auxilia.Governance.BuiltInRoles.Exists(role))
        return Results.BadRequest(new { error = $"unknown role '{role}'." });
    return null;
}

app.MapPost("/api/workflow-types/{type}/access/grant", async (
        string type, WorkflowTypeAccessChange request, HttpContext http, IPolicyEngine policy,
        Auxilia.Governance.WorkflowTypeAccessStore accessStore, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.PolicyAdminister, ct) is { } fail)
        return fail;
    if (ValidateAccessChange(request) is { } invalid)
        return invalid;
    if (request.RoleName is { Length: > 0 } role)
        await accessStore.GrantRoleAsync(type, request.Action, role, ct);
    else if (request.PrincipalId is { } principal)
        await accessStore.GrantPrincipalAsync(type, request.Action, principal, ct);
    else
        await accessStore.GrantGroupAsync(type, request.Action, request.GroupId!.Value, ct);
    return Results.Ok(new { granted = true });
}).RequireAuthorization();

app.MapPost("/api/workflow-types/{type}/access/revoke", async (
        string type, WorkflowTypeAccessChange request, HttpContext http, IPolicyEngine policy,
        Auxilia.Governance.WorkflowTypeAccessStore accessStore, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.PolicyAdminister, ct) is { } fail)
        return fail;
    if (ValidateAccessChange(request) is { } invalid)
        return invalid;
    if (request.RoleName is { Length: > 0 } role)
        await accessStore.RevokeRoleAsync(type, request.Action, role, ct);
    else if (request.PrincipalId is { } principal)
        await accessStore.RevokePrincipalAsync(type, request.Action, principal, ct);
    else
        await accessStore.RevokeGroupAsync(type, request.Action, request.GroupId!.Value, ct);
    return Results.Ok(new { revoked = true });
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
        : outcome.NotFound
            ? Results.NotFound(new { error = outcome.Error })
            : Results.Conflict(new { error = outcome.Error });
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
        : outcome.NotFound
            ? Results.NotFound(new { error = outcome.Error })
            : Results.Conflict(new { error = outcome.Error });
}).RequireAuthorization();

// Core-stored package download for the runner. Authorized by the run-scoped resolution token
// (same trust as resolve-slot), NOT a principal — the runner can fetch only packages of runs the
// Core dispatched to it, and the URL is minted per dispatch by the registry. Alternatively, an
// approval-scoped token (minted by the approval pipeline, passed into the verdict run's context)
// admits ONLY that PENDING package — bound to the reviewed submission's package hash, so a
// re-registration kills it — for the evaluation window: the review can fetch what it reviews.
app.MapGet("/api/workflow-types/{type}/package", async (
        string type, WorkflowTypeRegistryService registry,
        PendingPackageDownloadTokenService approvalTokens, SlotCredentialResolver resolver,
        CancellationToken ct, Guid? runId = null, string? token = null, string? approvalToken = null) =>
{
    if (approvalToken is { Length: > 0 })
    {
        var record = await registry.GetRecordAsync(type, ct);
        if (record?.Status != WorkflowTypeStatus.Pending
            || !approvalTokens.Validate(approvalToken, type, record.PackageHashBase64))
            return Results.Json(new { error = "invalid approval token" }, statusCode: StatusCodes.Status403Forbidden);
    }
    else
    {
        // Same gate as resolve-slot: digest match AND a run that has not ended (audited on refusal).
        if (runId is not { } run || string.IsNullOrEmpty(token))
            return Results.Json(new { error = SlotCredentialResolver.InvalidTokenError }, statusCode: StatusCodes.Status403Forbidden);
        if (await resolver.AuthorizeAsync(run, token, ct) is { Ok: false } refused)
            return Results.Json(new { error = refused.Error }, statusCode: StatusCodes.Status403Forbidden);
    }
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

// Reads are visibility-filtered like configurations: connector managers see everything; everyone
// else sees company connectors plus personal ones they own or were granted (principal/group/AD-group).
app.MapGet("/api/connectors", async (
        string? providerType, HttpContext http, ConnectorService svc, CancellationToken ct,
        int skip = 0, int take = 50) =>
    Results.Ok(await svc.QueryAsync(
        new ConnectorQuery(providerType, skip, take == 0 ? 50 : take),
        ConnectorViewerOf(http.User), ct)))
    .RequireAuthorization();

app.MapGet("/api/connectors/{id:guid}", async (
        Guid id, HttpContext http, ConnectorService svc, CancellationToken ct) =>
        await svc.GetAsync(id, ConnectorViewerOf(http.User), ct) is { } connector
            ? Results.Ok(connector) : Results.NotFound())
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

// --- Platform settings: runtime security knobs (policy.administer; writes step-up-gated) ---
app.MapGet("/api/platform-settings", async (
        HttpContext http, IPolicyEngine policy, PlatformSettingsService svc, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.PolicyAdminister, ct) is { } fail)
        return fail;
    return Results.Ok(await svc.ListAsync(ct));
}).RequireAuthorization();

app.MapPut("/api/platform-settings/{key}", async (
        string key, SetPlatformSetting request, HttpContext http, IPolicyEngine policy,
        ElevationTicketService elevation, PlatformSettingsService svc, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.PolicyAdminister, ct) is { } fail)
        return fail;
    // Settings shape the security posture (session lifetimes) — a fresh step-up is demanded,
    // exactly like admin-role grants and principal disables.
    if (RequireElevation(http, elevation) is { } denied)
        return denied;
    try
    {
        return Results.Ok(await svc.SetAsync(CoreClaims.PrincipalIdOf(http.User), key, request.Value, ct));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization();

// --- Repositories: first-class workspace resources (settings are NON-secret, so reads return
//     them; the credential stays a connector reference). Personal = self-serve, Company gated
//     like company connectors; edits apply live to every configuration referencing the id. ---
app.MapPost("/api/workspaces", async (
        CreateWorkspaceResource request, HttpContext http, IPolicyEngine policy,
        WorkspaceResourceService svc, AuditLog audit, CancellationToken ct) =>
{
    if (CoreClaims.PrincipalIdOf(http.User) is not { } principalId)
        return Results.Unauthorized();
    if (request.Scope != ResourceScope.Personal
        && await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.SlotConfigWrite, ct) is { } fail)
        return fail;
    try
    {
        var created = await svc.CreateAsync(request, principalId, ct);
        await audit.AppendAsync(principalId.ToString(), "workspace.created", created.Id.ToString(), created.Name, ct: ct);
        return Results.Ok(created);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization();

app.MapGet("/api/workspaces", async (
        HttpContext http, WorkspaceResourceService svc, CancellationToken ct) =>
    Results.Ok(await svc.ListVisibleAsync(CoreClaims.PrincipalIdOf(http.User), ct)))
    .RequireAuthorization();

app.MapGet("/api/workspaces/{id:guid}", async (
        Guid id, HttpContext http, WorkspaceResourceService svc, CancellationToken ct) =>
    await svc.GetAsync(id, ct) is { } repository
    && await svc.CanUseAsync(id, CoreClaims.PrincipalIdOf(http.User), ct)
        ? Results.Ok(repository)
        : Results.NotFound())
    .RequireAuthorization();

app.MapPut("/api/workspaces/{id:guid}", async (
        Guid id, UpdateWorkspaceResource request, HttpContext http, IPolicyEngine policy,
        WorkspaceResourceService svc, AuditLog audit, CancellationToken ct) =>
{
    if (CoreClaims.PrincipalIdOf(http.User) is not { } principalId)
        return Results.Unauthorized();
    if (await svc.GetAsync(id, ct) is not { } repository)
        return Results.NotFound();
    if (repository.OwnerPrincipalId != principalId
        && await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.SlotConfigWrite, ct) is { } fail)
        return fail;
    try
    {
        var updated = await svc.UpdateAsync(id, request, ct);
        await audit.AppendAsync(principalId.ToString(), "workspace.updated", id.ToString(), request.Name, ct: ct);
        return Results.Ok(updated);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization();

app.MapDelete("/api/workspaces/{id:guid}", async (
        Guid id, HttpContext http, IPolicyEngine policy, WorkspaceResourceService svc,
        AuditLog audit, CancellationToken ct) =>
{
    if (CoreClaims.PrincipalIdOf(http.User) is not { } principalId)
        return Results.Unauthorized();
    if (await svc.GetAsync(id, ct) is not { } repository)
        return Results.NotFound();
    if (repository.OwnerPrincipalId != principalId
        && await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.SlotConfigWrite, ct) is { } fail)
        return fail;
    await svc.DeleteAsync(id, ct);
    await audit.AppendAsync(principalId.ToString(), "workspace.deleted", id.ToString(), repository.Name, ct: ct);
    return Results.NoContent();
}).RequireAuthorization();

app.MapPost("/api/workspaces/{id:guid}/grants", async (
        Guid id, SetWorkspaceGrants request, HttpContext http, IPolicyEngine policy,
        WorkspaceResourceService svc, CancellationToken ct) =>
{
    if (CoreClaims.PrincipalIdOf(http.User) is not { } principalId)
        return Results.Unauthorized();
    if (await svc.GetAsync(id, ct) is not { } repository)
        return Results.NotFound();
    if (repository.OwnerPrincipalId != principalId
        && await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.SlotConfigWrite, ct) is { } fail)
        return fail;
    return await svc.SetGrantsAsync(id, request.Grants, ct)
        ? Results.Accepted($"/api/workspaces/{id}")
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

// Group roles fan out to every member (the Policy Engine unions them into effective roles), so the
// group path to Administrator is elevation-gated exactly like the direct grant: admitting a member
// into an Administrator-holding group is an admin grant to that member.
app.MapPost("/api/groups/{id:guid}/members", async (
        Guid id, AddGroupMemberRequest request, HttpContext http, IPolicyEngine policy,
        GroupDirectory groups, ElevationTicketService elevation, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.PrincipalAdminister, ct) is { } fail)
        return fail;
    if ((await groups.RolesAsync(id, ct)).Contains(BuiltInRoles.Administrator)
        && RequireElevation(http, elevation) is { } denied)
        return denied;
    await groups.AddMemberAsync(id, request.PrincipalId, ct);
    return Results.Accepted($"/api/groups/{id}");
}).RequireAuthorization();

app.MapPost("/api/groups/{id:guid}/roles", async (
        Guid id, AssignGroupRoleRequest request, HttpContext http, IPolicyEngine policy,
        GroupDirectory groups, ElevationTicketService elevation, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.PrincipalAdminister, ct) is { } fail)
        return fail;
    if (request.RoleName == BuiltInRoles.Administrator && RequireElevation(http, elevation) is { } denied)
        return denied;
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
        string? kind, bool? enabled, string? search, string? tag,
        HttpContext http, IPolicyEngine policy, PrincipalAdminService svc,
        CancellationToken ct, int skip = 0, int take = 50) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.PrincipalAdminister, ct) is { } fail)
        return fail;
    return Results.Ok(await svc.QueryAsync(
        new PrincipalQuery(kind, enabled, search, skip, take == 0 ? 50 : take, tag), ct));
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
    try
    {
        var principal = await directory.CreateHumanAsync(request.DisplayName, request.Username, request.Password, ct);
        return Results.Ok(PrincipalAdminService.ToDto(principal, []));
    }
    catch (PrincipalConflictException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
}).RequireAuthorization();

// Create a service principal; the generated API key is returned exactly once (write-only after).
app.MapPost("/api/principals/ai", async (
        CreateApiKeyPrincipalRequest request, HttpContext http, IPolicyEngine policy,
        PrincipalDirectory directory, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.PrincipalAdminister, ct) is { } fail)
        return fail;
    var (principal, apiKey) = await directory.CreateApiKeyPrincipalAsync(request.DisplayName, ct);
    return Results.Ok(new CreatedApiKeyPrincipal(PrincipalAdminService.ToDto(principal, []), apiKey));
}).RequireAuthorization();

// Replace a principal's free-form tags — the admin-managed classification axis.
app.MapPost("/api/principals/{id:guid}/tags", async (
        Guid id, SetPrincipalTagsRequest request, HttpContext http, IPolicyEngine policy,
        PrincipalDirectory directory, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.PrincipalAdminister, ct) is { } fail)
        return fail;
    return await directory.SetTagsAsync(id, request.Tags, ct)
        ? Results.Accepted($"/api/principals/{id}")
        : Results.NotFound();
}).RequireAuthorization();

// A caller may hold principal.administer all day; GRANTING administrator rights — directly, or via
// the group path (assigning Administrator to a group, admitting a member into an Administrator-
// holding group) — revoking them, or disabling a principal additionally demands a fresh step-up
// elevation (re-typed credential).
static IResult? RequireElevation(HttpContext http, ElevationTicketService elevation)
{
    var token = http.Request.Headers[ElevationTicketService.HeaderName].FirstOrDefault();
    return CoreClaims.PrincipalIdOf(http.User) is { } caller && elevation.Validate(token, caller)
        ? null
        : Results.Json(new { error = ElevationTicketService.RequiredError },
            statusCode: StatusCodes.Status403Forbidden);
}

// Assign a Direct role (idempotent). Never touches Group-/GroupMapping-sourced roles.
app.MapPost("/api/principals/{id:guid}/roles", async (
        Guid id, AssignRoleRequest request, HttpContext http, IPolicyEngine policy,
        PrincipalDirectory directory, ElevationTicketService elevation, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.PrincipalAdminister, ct) is { } fail)
        return fail;
    if (request.RoleName == BuiltInRoles.Administrator && RequireElevation(http, elevation) is { } denied)
        return denied;
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
        PrincipalDirectory directory, ElevationTicketService elevation, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.PrincipalAdminister, ct) is { } fail)
        return fail;
    if (role == BuiltInRoles.Administrator && RequireElevation(http, elevation) is { } denied)
        return denied;
    try
    {
        await directory.RevokeRoleAsync(id, role, ct);
        return Results.NoContent();
    }
    catch (InvalidOperationException ex)
    {
        // The last-administrator lock-out guard.
        return Results.Conflict(new { error = ex.Message });
    }
}).RequireAuthorization();

app.MapPost("/api/principals/{id:guid}/enabled", async (
        Guid id, SetPrincipalEnabledRequest request, HttpContext http, IPolicyEngine policy,
        PrincipalDirectory directory, ElevationTicketService elevation, CancellationToken ct) =>
{
    if (await CoreAuthorization.AuthorizeAsync(http.User, policy, PermissionActions.PrincipalAdminister, ct) is { } fail)
        return fail;
    // Disabling is the deletion-equivalent — it demands the step-up; re-enabling does not.
    if (!request.Enabled && RequireElevation(http, elevation) is { } denied)
        return denied;
    try
    {
        return await directory.SetEnabledAsync(id, request.Enabled, ct)
            ? Results.Accepted($"/api/principals/{id}")
            : Results.NotFound();
    }
    catch (InvalidOperationException ex)
    {
        // The last-administrator lock-out guard.
        return Results.Conflict(new { error = ex.Message });
    }
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

// The connector twin of ViewerOf: connector managers (slot-config.write) see every connector.
static ConnectorViewer ConnectorViewerOf(System.Security.Claims.ClaimsPrincipal user) => new(
    CoreClaims.PrincipalIdOf(user),
    CoreClaims.HasRolePermission(user, PermissionActions.SlotConfigWrite));

// Exposed for WebApplicationFactory-based component tests.
public partial class Program;
