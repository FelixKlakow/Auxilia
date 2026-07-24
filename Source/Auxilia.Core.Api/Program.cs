using Auxilia.Core.Api;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;
using Auxilia.Messaging;
using Auxilia.PlatformData;
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
builder.Services.AddPlatformEntity<CoreRunConfigurationRecord>(platformData);
builder.Services.AddPlatformEntity<CoreConnectorRecord>(platformData);
builder.Services.AddPlatformEntity<CoreRunRecord>(platformData);
builder.Services.AddSingleton(TimeProvider.System);

// --- Settings ---
builder.Services.Configure<CoreApiSettings>(builder.Configuration.GetSection("CoreApi"));

// --- Core services ---
builder.Services.AddSingleton<ConnectorService>();
builder.Services.AddSingleton<RunConfigurationService>();
builder.Services.AddSingleton<RunService>();
builder.Services.AddSingleton<RunReadService>();
builder.Services.AddHostedService<RunTrackingService>();

var app = builder.Build();

// Seed static configurations (the "statically configured" path).
{
    var configurations = app.Services.GetRequiredService<RunConfigurationService>();
    var coreSettings = app.Services.GetRequiredService<IOptions<CoreApiSettings>>().Value;
    foreach (var seed in coreSettings.StaticConfigurations)
        await configurations.EnsureAsync(seed, app.Lifetime.ApplicationStopping);
}

// --- Runs ---
app.MapPost("/api/runs", async (RunRequest request, RunService runs, CancellationToken ct) =>
    Results.Ok(await runs.RunInlineAsync(request, ct)));

app.MapGet("/api/runs", async (
        string? state, string? workflowType, Guid? configurationId,
        RunReadService svc, CancellationToken ct, int skip = 0, int take = 50) =>
    Results.Ok(await svc.QueryAsync(
        new RunQuery(state, workflowType, configurationId, skip, take == 0 ? 50 : take), ct)));

app.MapGet("/api/runs/{id:guid}", async (Guid id, RunReadService svc, CancellationToken ct) =>
    await svc.GetAsync(id, ct) is { } status ? Results.Ok(status) : Results.NotFound());

// --- Configurations ---
app.MapPost("/api/configurations", async (
        CreateRunConfiguration request, RunConfigurationService svc, CancellationToken ct) =>
    Results.Ok(await svc.CreateAsync(request, ct)));

app.MapGet("/api/configurations", async (
        string? workflowType, bool? enabled,
        RunConfigurationService svc, CancellationToken ct, int skip = 0, int take = 50) =>
    Results.Ok(await svc.QueryAsync(
        new ConfigurationQuery(workflowType, enabled, skip, take == 0 ? 50 : take), ct)));

app.MapGet("/api/configurations/{id:guid}", async (
        Guid id, RunConfigurationService svc, CancellationToken ct) =>
    await svc.GetAsync(id, ct) is { } config ? Results.Ok(config) : Results.NotFound());

app.MapPost("/api/configurations/{id:guid}/run", async (
        Guid id, RunService runs, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await runs.RunConfigurationAsync(id, null, ct));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// --- Connectors ---
app.MapPost("/api/connectors", async (
        CreateConnector request, ConnectorService svc, CancellationToken ct) =>
    Results.Ok(await svc.CreateAsync(request, ct)));

app.MapGet("/api/connectors", async (
        string? providerType, ConnectorService svc, CancellationToken ct, int skip = 0, int take = 50) =>
    Results.Ok(await svc.QueryAsync(new ConnectorQuery(providerType, skip, take == 0 ? 50 : take), ct)));

app.MapGet("/api/connectors/{id:guid}", async (Guid id, ConnectorService svc, CancellationToken ct) =>
    await svc.GetAsync(id, ct) is { } connector ? Results.Ok(connector) : Results.NotFound());

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();

// Exposed for WebApplicationFactory-based component tests.
public partial class Program;
