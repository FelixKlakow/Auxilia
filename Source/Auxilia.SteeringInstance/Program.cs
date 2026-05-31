using System.Reflection;
using Auxilia.Messaging;
using Auxilia.SteeringInstance.Workflows;
using Auxilia.SteeringInstance.Workflows.Storage;
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
var logPath = Path.Combine(logDir, $"SteeringInstance_{serviceId}.log");

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
    builder.Services.AddSingleton(new SteeringInstanceInfo(serviceId, startupTime));

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

    // --- Workflow services ---
    builder.Services.AddSingleton<WorkflowSchemaStore>();
    builder.Services.AddSingleton<PendingWorkflowPackageStore>();
    builder.Services.AddSingleton<SlotConfigurationStore>();
    builder.Services.AddSingleton<SignalHandlerStore>();
    builder.Services.AddSingleton<WorkflowInstanceRegistry>();
    builder.Services.AddSingleton<DirtyConfigurationDetector>();
    builder.Services.AddSingleton<EnvironmentValidator>();
    builder.Services.AddSingleton<ConfigurationResolver>();
    builder.Services.AddSingleton<WorkflowRegistrationHandler>();
    builder.Services.AddSingleton<SignalDispatcher>();
    builder.Services.AddSingleton<WorkflowAnnouncementHandler>();
    builder.Services.AddSingleton<WorkflowDispatcher>();
    builder.Services.AddSingleton<WorkflowCancelDispatcher>();
    builder.Services.AddSingleton<WorkflowStateHandler>();
    builder.Services.AddSingleton<IWorkflowLauncher, DockerWorkflowLauncher>();
    builder.Services.AddSingleton<IDockerClientFactory, DefaultDockerClientFactory>();
    builder.Services.AddSingleton<IDeveloperModeProvider, EnvironmentDeveloperModeProvider>();
    builder.Services.AddSingleton<IWorkflowPackageVerifier, WorkflowPackageVerifier>();
    builder.Services.AddHttpClient("workflow-packages");

    // --- Workflow launcher settings ---
    builder.Services.Configure<DockerWorkflowLauncherSettings>(
        builder.Configuration.GetSection("WorkflowLauncher"));

    // --- Workflow dispatcher settings ---
    builder.Services.Configure<WorkflowDispatcherSettings>(
        builder.Configuration.GetSection("WorkflowDispatcher"));

    // --- Slot configuration seeding ---
    builder.Services.Configure<SlotConfigurationsSettings>(
        builder.Configuration.GetSection("SlotConfigurations"));

    // --- OpenTelemetry (tracing + metrics) ---
    var otlpEndpoint = builder.Configuration["Otlp:Endpoint"];
    var serviceVersion = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion ?? "0.0.0";

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r
            .AddService(
                serviceName: "Auxilia.SteeringInstance",
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

    // --- Seed slot configurations from config ---
    var slotConfigSettings = app.Services
        .GetRequiredService<IOptions<SlotConfigurationsSettings>>().Value;
    var slotStore = app.Services.GetRequiredService<SlotConfigurationStore>();
    foreach (var (workflowType, entries) in slotConfigSettings.Workflows)
        foreach (var entry in entries)
            slotStore.UpsertConfiguration(workflowType,
                new StoredSlotConfiguration(
                    entry.SlotName,
                    entry.ProviderType,
                    entry.Settings,
                    ConfigurationStatus.Valid));

    var handler = app.Services.GetRequiredService<WorkflowRegistrationHandler>();
    await handler.StartAsync(app.Lifetime.ApplicationStopping);

    var signalDispatcher = app.Services.GetRequiredService<SignalDispatcher>();
    await signalDispatcher.StartAsync(app.Lifetime.ApplicationStopping);

    var announcementHandler = app.Services.GetRequiredService<WorkflowAnnouncementHandler>();
    await announcementHandler.StartAsync(app.Lifetime.ApplicationStopping);

    var dispatcher = app.Services.GetRequiredService<WorkflowDispatcher>();
    await dispatcher.StartAsync(app.Lifetime.ApplicationStopping);

    var cancelDispatcher = app.Services.GetRequiredService<WorkflowCancelDispatcher>();
    await cancelDispatcher.StartAsync(app.Lifetime.ApplicationStopping);

    var stateHandler = app.Services.GetRequiredService<WorkflowStateHandler>();
    await stateHandler.StartAsync(app.Lifetime.ApplicationStopping);

    app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
    app.MapPrometheusScrapingEndpoint(); // GET /metrics

    app.Run();
}
finally
{
    await Log.CloseAndFlushAsync();
}

public partial class Program
{
}

public sealed record SteeringInstanceInfo(Guid ServiceId, DateTime StartupTimeUtc);