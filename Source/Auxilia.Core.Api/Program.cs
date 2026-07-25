using Auxilia.Core.Api;
using Auxilia.Core.Api.Auth;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;
using Auxilia.Governance;
using Auxilia.Governance.Policy;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
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
builder.Services.AddPlatformEntity<AuditRecord>(platformData);
builder.Services.AddSingleton<AuditLog>();

// --- Governance: identity, RBAC, Policy Engine (the Core is the auth + audit authority) ---
var governanceSettings = new GovernanceSettings();
builder.Configuration.GetSection("Governance").Bind(governanceSettings);
builder.Services.AddGovernance(platformData, governanceSettings);
builder.Services.AddSingleton<CoreSecurityBootstrap>();
builder.Services.Configure<CoreSecuritySettings>(builder.Configuration.GetSection("CoreSecurity"));

// --- Authentication / authorization (API-key bearer for REST and MCP alike) ---
builder.Services.AddAuthentication(CoreApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, CoreApiKeyAuthenticationHandler>(
        CoreApiKeyAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization();

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
builder.Services.AddSingleton<RunConfigurationService>();
builder.Services.AddSingleton<RunService>();
builder.Services.AddSingleton<RunReadService>();
builder.Services.AddSingleton<SlotCredentialResolver>();
builder.Services.AddHostedService<RunTrackingService>();

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

// --- Runs ---
app.MapPost("/api/runs", async (
        RunRequest request, HttpContext http, IPolicyEngine policy, RunService runs, CancellationToken ct) =>
{
    if (CoreClaims.PrincipalIdOf(http.User) is not { } principalId)
        return Results.Unauthorized();
    var decision = await policy.EvaluateAsync(
        new PolicyContext(principalId, PermissionActions.WorkflowTrigger, request.WorkflowType)
        { WorkflowType = request.WorkflowType }, ct);
    if (!decision.Allowed)
        return Results.Json(new { error = decision.Reason }, statusCode: StatusCodes.Status403Forbidden);
    return Results.Ok(await runs.RunInlineAsync(request, ct));
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
        Guid id, HttpContext http, IPolicyEngine policy, RunService runs,
        RunConfigurationService configurations, CancellationToken ct) =>
{
    if (CoreClaims.PrincipalIdOf(http.User) is not { } principalId)
        return Results.Unauthorized();
    var config = await configurations.GetAsync(id, ct);
    if (config is null)
        return Results.NotFound();
    var decision = await policy.EvaluateAsync(
        new PolicyContext(principalId, PermissionActions.WorkflowTrigger, config.WorkflowType)
        { WorkflowType = config.WorkflowType }, ct);
    if (!decision.Allowed)
        return Results.Json(new { error = decision.Reason }, statusCode: StatusCodes.Status403Forbidden);
    try
    {
        return Results.Ok(await runs.RunConfigurationAsync(id, null, ct));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization();

// --- Connectors ---
app.MapPost("/api/connectors", async (
        CreateConnector request, ConnectorService svc, CancellationToken ct) =>
    Results.Ok(await svc.CreateAsync(request, ct)))
    .RequireAuthorization();

app.MapGet("/api/connectors", async (
        string? providerType, ConnectorService svc, CancellationToken ct, int skip = 0, int take = 50) =>
    Results.Ok(await svc.QueryAsync(new ConnectorQuery(providerType, skip, take == 0 ? 50 : take), ct)))
    .RequireAuthorization();

app.MapGet("/api/connectors/{id:guid}", async (Guid id, ConnectorService svc, CancellationToken ct) =>
        await svc.GetAsync(id, ct) is { } connector ? Results.Ok(connector) : Results.NotFound())
    .RequireAuthorization();

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

// --- MCP (authenticated) + health ---
app.MapMcp("/mcp").RequireAuthorization();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();

// Exposed for WebApplicationFactory-based component tests.
public partial class Program;
