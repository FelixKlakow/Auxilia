using System.Reflection;
using Auxilia.Governance;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.Core.Runner.Workflows;
using Auxilia.Core.Runner.Workflows.Storage;
using Auxilia.Workflows;
using Auxilia.Workflows.Crypto;
using Microsoft.Extensions.Options;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

// Pre-generate instance ID for log file name and service identity
var serviceId = Guid.NewGuid();
var startupTime = DateTime.UtcNow;

var logDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "Auxilia", "Logs");
Directory.CreateDirectory(logDir);
var logPath = Path.Combine(logDir, $"CoreRunner_{serviceId}.log");

// Bootstrap logger (used before the DI host is built)
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 31)
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // --- Serilog ---
    builder.Host.UseSerilog((ctx, services, lc) => lc
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 31));

    // --- Service identity ---
    builder.Services.AddSingleton(new CoreRunnerInfo(serviceId, startupTime));

    // --- Messaging ---
    builder.Services.AddSingleton<IMessageBusClient>(_ =>
        RabbitMqClient.CreateAsync(
            builder.Configuration["RabbitMq:Host"] ?? "localhost",
            int.Parse(builder.Configuration["RabbitMq:Port"] ?? "5672"),
            builder.Configuration["RabbitMq:UserName"] ?? "guest",
            builder.Configuration["RabbitMq:Password"] ?? "guest"
        ).GetAwaiter().GetResult());

    // --- Runner profile ---
    builder.Services.Configure<RunnerProfile>(builder.Configuration.GetSection("RunnerProfile"));

    // --- Durable platform data ---
    var platformDataSettings = new PlatformDataSettings();
    builder.Configuration.GetSection("PlatformData").Bind(platformDataSettings);
    builder.Services.AddSingleton(platformDataSettings);
    builder.Services.AddSettingsProtection(platformDataSettings);
    builder.Services.AddPlatformEntity<WorkflowSchemaRecord>(platformDataSettings);
    builder.Services.AddPlatformEntity<WorkflowPackageRecord>(platformDataSettings);
    builder.Services.AddPlatformEntity<SlotProviderRecord>(platformDataSettings);
    builder.Services.AddPlatformEntity<SignalHandlerRecord>(platformDataSettings);
    builder.Services.AddPlatformEntity<WorkflowInstanceRecord>(platformDataSettings);
    builder.Services.AddPlatformEntity<AuditRecord>(platformDataSettings);
    builder.Services.AddPlatformEntity<ServiceHeartbeatRecord>(platformDataSettings);
    builder.Services.AddPlatformEntity<ArtifactRecord>(platformDataSettings);
    builder.Services.AddPlatformEntity<ViewDataRecord>(platformDataSettings);
    builder.Services.AddSingleton<ViewDataHandler>();
    // Payload storage is a shared deployment location (ArtifactStore:PayloadRoot) so Core.Api
    // can serve client downloads of what this runner persists; unset = local-only default.
    var artifactStoreSettings = new Auxilia.PlatformData.Artifacts.ArtifactStoreSettings();
    builder.Configuration.GetSection("ArtifactStore").Bind(artifactStoreSettings);
    builder.Services.AddSingleton(artifactStoreSettings);
    builder.Services.AddSingleton<Auxilia.PlatformData.Artifacts.IArtifactStore,
        Auxilia.PlatformData.Artifacts.FileSystemArtifactStore>();
    builder.Services.AddSingleton<ArtifactPersister>();
    builder.Services.AddSingleton<AuditLog>();
    if (string.IsNullOrWhiteSpace(platformDataSettings.ProtectionKeyBase64))
        Log.Warning("PlatformData:ProtectionKeyBase64 is not configured — slot settings are stored unprotected (dev only).");

    // --- Governance (identity, accounts, Policy Engine) ---
    var governanceSettings = new GovernanceSettings();
    builder.Configuration.GetSection("Governance").Bind(governanceSettings);
    builder.Services.AddGovernance(platformDataSettings, governanceSettings);

    // --- Workflow services ---
    builder.Services.AddSingleton<WorkflowSchemaStore>();
    builder.Services.AddSingleton<WorkflowPackageStore>();
    builder.Services.AddSingleton<PendingWorkflowPackageStore>();
    builder.Services.AddSingleton<SignalHandlerStore>();
    builder.Services.AddSingleton<WorkflowInstanceRegistry>();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<WorkflowInstanceTokenRegistry>();
    builder.Services.AddSingleton<EnvironmentValidator>();
    builder.Services.AddSingleton<WorkflowRegistrationHandler>();
    builder.Services.AddSingleton<SlotActivationHandler>();
    builder.Services.AddSingleton<ICoreCredentialClient, CoreCredentialClient>();
    builder.Services.AddSingleton<IRepositoryAuthResolver, RepositoryAuthResolver>();
    builder.Services.AddSingleton<ResourceProxyHandler>();
    builder.Services.AddSingleton<NetworkPolicyResolver>();
    builder.Services.AddSingleton<WorkspaceManager>();
    builder.Services.AddSingleton<SignalDispatcher>();
    builder.Services.AddSingleton<WorkflowAnnouncementHandler>();
    builder.Services.AddSingleton<WorkflowDispatcher>();
    builder.Services.AddSingleton<WorkflowCancelDispatcher>();
    builder.Services.AddSingleton<WorkflowStateHandler>();
    builder.Services.AddSingleton<Auxilia.Workflows.Messaging.WorkflowStatusPublisher>();
    builder.Services.AddSingleton<LongLivingDrainCoordinator>();
    builder.Services.AddHostedService<RunnerHeartbeatService>();
    builder.Services.AddSingleton<IWorkflowLauncher, DockerWorkflowLauncher>();
    builder.Services.AddSingleton<IDockerClientFactory, DefaultDockerClientFactory>();
    builder.Services.AddSingleton<IDeveloperModeProvider, EnvironmentDeveloperModeProvider>();
    builder.Services.AddSingleton<IWorkflowPackageVerifier, WorkflowPackageVerifier>();
    builder.Services.AddHttpClient("workflow-packages");
    builder.Services.AddHttpClient("core-api");

    // --- Workflow launcher settings ---
    builder.Services.Configure<DockerWorkflowLauncherSettings>(
        builder.Configuration.GetSection("WorkflowLauncher"));

    // --- Workflow dispatcher settings ---
    builder.Services.Configure<WorkflowDispatcherSettings>(
        builder.Configuration.GetSection("WorkflowDispatcher"));

    // --- Slot configuration seeding ---
    builder.Services.AddSingleton<SlotProviderRegistry>();

    // --- OpenTelemetry(tracing + metrics) ---
    var otlpEndpoint = builder.Configuration["Otlp:Endpoint"];
    var serviceVersion = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion ?? "0.0.0";

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r
            .AddService(
                serviceName: "Auxilia.Core.Runner",
                serviceVersion: serviceVersion,
                serviceInstanceId: serviceId.ToString()))
        .WithTracing(tracing =>
        {
            tracing
                .AddSource(MessagingTelemetry.ActivitySourceName)
                .AddAspNetCoreInstrumentation();
            if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                tracing.AddOtlpExporter(o =>
                {
                    o.Endpoint = new Uri(otlpEndpoint);
                    o.Protocol = OtlpExportProtocol.Grpc;
                });
        })
        .WithMetrics(metrics =>
        {
            metrics
                .AddMeter(MessagingTelemetry.MeterName)
                .AddAspNetCoreInstrumentation()
                .AddPrometheusExporter();
            if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                metrics.AddOtlpExporter(o =>
                {
                    o.Endpoint = new Uri(otlpEndpoint);
                    o.Protocol = OtlpExportProtocol.Grpc;
                });
        });

    var app = builder.Build();

    await app.Services.GetRequiredService<GovernanceSeeder>().SeedAsync(app.Lifetime.ApplicationStopping);

    // Seed slot-handler providers (plugin DLLs) from startup config (env vars / appsettings).
    var launcherSettings = app.Services.GetRequiredService<IOptions<DockerWorkflowLauncherSettings>>().Value;
    var providerRegistry = app.Services.GetRequiredService<SlotProviderRegistry>();
    foreach (var (providerType, dllPath) in launcherSettings.SlotPackages)
    {
        // The plugin's manifest sidecar is the source of truth for its setting descriptors,
        // capability contracts, and category.
        PluginManifest? sidecar = null;
        var manifestPath = Path.ChangeExtension(dllPath, null) + ".manifest.json";
        if (File.Exists(manifestPath))
        {
            try
            {
                sidecar = System.Text.Json.JsonSerializer.Deserialize<PluginManifest>(
                    await File.ReadAllTextAsync(manifestPath));
            }
            catch (System.Text.Json.JsonException)
            {
                // Malformed sidecar — register the provider without descriptors.
            }
        }
        await providerRegistry.UpsertAsync(
            providerType, dllPath, sidecar?.Settings, sidecar?.Contracts, sidecar?.Category,
            sidecar?.Description);
    }

    var handler = app.Services.GetRequiredService<WorkflowRegistrationHandler>();
    await handler.StartAsync(app.Lifetime.ApplicationStopping);

    var slotActivationHandler = app.Services.GetRequiredService<SlotActivationHandler>();
    await slotActivationHandler.StartAsync(app.Lifetime.ApplicationStopping);

    var resourceProxyHandler = app.Services.GetRequiredService<ResourceProxyHandler>();
    await resourceProxyHandler.StartAsync(app.Lifetime.ApplicationStopping);

    var viewDataHandler = app.Services.GetRequiredService<ViewDataHandler>();
    await viewDataHandler.StartAsync(app.Lifetime.ApplicationStopping);

    var signalDispatcher = app.Services.GetRequiredService<SignalDispatcher>();
    await signalDispatcher.StartAsync(app.Lifetime.ApplicationStopping);

    var announcementHandler = app.Services.GetRequiredService<WorkflowAnnouncementHandler>();
    await announcementHandler.StartAsync(app.Lifetime.ApplicationStopping);

    // A previous runner's containers are unmanageable (exit watchers died with it; the fresh
    // ServiceId never re-adopts) — reap them before accepting work so no container lingers.
    if (launcherSettings.ReapWorkflowContainersOnStart
        && app.Services.GetRequiredService<IWorkflowLauncher>() is DockerWorkflowLauncher dockerLauncher)
    {
        try
        {
            var reaped = await dockerLauncher.ReapOrphanedContainersAsync(app.Lifetime.ApplicationStopping);
            if (reaped > 0)
                Log.Warning("Reaped {Count} orphaned workflow container(s) at startup.", reaped);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Startup container reaping failed — continuing; the Core's zombie sweep still covers the run records.");
        }
    }

    var dispatcher = app.Services.GetRequiredService<WorkflowDispatcher>();
    await dispatcher.StartAsync(app.Lifetime.ApplicationStopping);

    var cancelDispatcher = app.Services.GetRequiredService<WorkflowCancelDispatcher>();
    await cancelDispatcher.StartAsync(app.Lifetime.ApplicationStopping);

    var stateHandler = app.Services.GetRequiredService<WorkflowStateHandler>();
    await stateHandler.StartAsync(app.Lifetime.ApplicationStopping);

    app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
    app.MapPrometheusScrapingEndpoint(); // GET /metrics

    Log.Information("CoreRunner ServiceId={ServiceId}", serviceId);

    app.Run();
}
finally
{
    await Log.CloseAndFlushAsync();
}

public partial class Program
{
}

public sealed record CoreRunnerInfo(Guid ServiceId, DateTime StartupTimeUtc);