using System.Reflection;
using Auxilia.BackendService;
using Auxilia.BackendService.PlatformHost;
using Auxilia.Governance;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.Workflows.Messaging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

// Pre-generate the instance ID so the log file name matches ServiceInfo.ServiceId
var instanceId = Guid.NewGuid();
var logDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "Auxilia", "Logs");
Directory.CreateDirectory(logDir);
var logPath = Path.Combine(logDir, $"Backend_{instanceId}.log");

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

    // --- Service identity (singleton, captured once at startup) ---
    builder.Services.AddSingleton(new ServiceInfo(instanceId));

    // --- Messaging ---
    builder.Services.AddSingleton<IMessageBusClient>(_ =>
        RabbitMqClient.CreateAsync(
            builder.Configuration["RabbitMq:Host"] ?? "localhost",
            int.Parse(builder.Configuration["RabbitMq:Port"] ?? "5672"),
            builder.Configuration["RabbitMq:UserName"] ?? "guest",
            builder.Configuration["RabbitMq:Password"] ?? "guest"
        ).GetAwaiter().GetResult());

    // --- OpenTelemetry (tracing + metrics) ---
    var otlpEndpoint = builder.Configuration["Otlp:Endpoint"];
    var serviceVersion = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion ?? "0.0.0";

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r
            .AddService(
                serviceName: "Auxilia.BackendService",
                serviceVersion: serviceVersion,
                serviceInstanceId: instanceId.ToString()))
        .WithTracing(tracing =>
        {
            tracing
                .AddSource(BackendServiceTelemetry.ActivitySourceName)
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
                .AddMeter(BackendServiceTelemetry.MeterName)
                .AddMeter(MessagingTelemetry.MeterName)
                .AddAspNetCoreInstrumentation()
                .AddPrometheusExporter();
            if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                metrics.AddOtlpExporter((o, r) =>
                {
                    o.Endpoint = new Uri(otlpEndpoint);
                    o.Protocol = OtlpExportProtocol.Grpc;
                    // Use a short export interval so system tests don't have to wait 60 s
                    r.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 5_000;
                });
        });

    // --- Durable platform data ---
    var platformDataSettings = new PlatformDataSettings();
    builder.Configuration.GetSection("PlatformData").Bind(platformDataSettings);
    builder.Services.AddSingleton(platformDataSettings);
    builder.Services.AddSettingsProtection(platformDataSettings);
    builder.Services.AddPlatformEntity<WorkflowInstanceRecord>(platformDataSettings);
    builder.Services.AddPlatformEntity<ServiceHeartbeatRecord>(platformDataSettings);
    builder.Services.AddPlatformEntity<ScheduledTriggerRecord>(platformDataSettings);
    builder.Services.AddPlatformEntity<ArtifactTriggerRecord>(platformDataSettings);
    builder.Services.AddPlatformEntity<ViewDataRecord>(platformDataSettings);
    builder.Services.AddPlatformEntity<ArtifactRecord>(platformDataSettings);
    builder.Services.AddPlatformEntity<AuditRecord>(platformDataSettings);
    builder.Services.AddPlatformEntity<SlotProviderRecord>(platformDataSettings);
    builder.Services.AddPlatformEntity<SlotConfigurationRecord>(platformDataSettings);
    builder.Services.AddPlatformEntity<DashboardRecord>(platformDataSettings);
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<AuditLog>();
    builder.Services.AddSingleton<WorkflowStatusPublisher>();

    // --- Governance (identity, accounts, Policy Engine) ---
    var governanceSettings = new Auxilia.Governance.GovernanceSettings();
    builder.Configuration.GetSection("Governance").Bind(governanceSettings);
    builder.Services.AddGovernance(platformDataSettings, governanceSettings);

    // --- Dashboard: cookie sessions + SignalR fan-out ---
    builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.Cookies
            .CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(o =>
        {
            o.Cookie.Name = "auxilia.session";
            o.Cookie.HttpOnly = true;
            o.ExpireTimeSpan = TimeSpan.FromHours(8);
            o.SlidingExpiration = true;
            // API/hub clients get status codes, not login redirects.
            o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
        })
        .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions,
            Auxilia.BackendService.Mcp.McpApiKeyAuthenticationHandler>(
            Auxilia.BackendService.Mcp.McpApiKeyAuthenticationHandler.SchemeName, null);
    builder.Services.AddAuthorization();
    builder.Services.AddSignalR();
    builder.Services.AddSingleton<Auxilia.BackendService.Dashboard.LiveViewBroker>();
    builder.Services.AddSingleton<Auxilia.BackendService.Dashboard.DashboardComposer>();
    builder.Services.AddSingleton<Auxilia.BackendService.Dashboard.TriggerAdministration>();
    builder.Services.AddHostedService<Auxilia.BackendService.Dashboard.ViewDataFanOutHandler>();

    // --- Dashboard UI (interactive-server Blazor) ---
    builder.Services.AddRazorComponents().AddInteractiveServerComponents();
    builder.Services.AddCascadingAuthenticationState();
    var dashboardSettings = new Auxilia.BackendService.Dashboard.DashboardSettings();
    builder.Configuration.GetSection("Dashboard").Bind(dashboardSettings);
    builder.Services.AddSingleton(dashboardSettings);

    // --- MCP server: AI-agent UI parity over Streamable HTTP, API-key authenticated ---
    builder.Services.AddMcpServer()
        .WithHttpTransport(o => o.Stateless = true)
        .WithTools<Auxilia.BackendService.Mcp.AuxiliaMcpTools>();

    // --- Platform host (status fan-out, heartbeat monitor, scheduler) ---
    builder.Services.Configure<PlatformHostSettings>(
        builder.Configuration.GetSection("PlatformHost"));
    builder.Services.AddHostedService<WorkflowStatusEventHandler>();
    builder.Services.AddHostedService<HeartbeatMonitor>();
    builder.Services.AddHostedService<TriggerScheduler>();
    builder.Services.AddHostedService<ArtifactTriggerHandler>();

    // --- Email task source (the v1 integration adapter) ---
    builder.Services.Configure<Auxilia.Adapters.Email.EmailTaskSourceSettings>(
        builder.Configuration.GetSection("EmailTaskSource"));
    builder.Services.AddSingleton<Auxilia.Adapters.Email.IMailboxClient,
        Auxilia.Adapters.Email.MailKitMailboxClient>();
    builder.Services.AddHostedService<Auxilia.Adapters.Email.EmailTaskSourceAdapter>();

    // --- Hosted services ---
    builder.Services.AddHostedService<QueueInitializer>();
    builder.Services.AddHostedService<IdentificationRequestHandler>();

    var app = builder.Build();

    await app.Services.GetRequiredService<GovernanceSeeder>().SeedAsync(app.Lifetime.ApplicationStopping);

    app.UseStaticFiles();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseAntiforgery();
    Auxilia.BackendService.Dashboard.DashboardAuthEndpoints.MapDashboardAuth(app);
    app.MapHub<Auxilia.BackendService.Dashboard.ViewDataHub>("/hubs/views");
    app.MapMcp("/mcp").RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
    {
        AuthenticationSchemes = Auxilia.BackendService.Mcp.McpApiKeyAuthenticationHandler.SchemeName
    });
    app.MapRazorComponents<Auxilia.BackendService.Components.App>()
        .AddInteractiveServerRenderMode();

    app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
    app.MapPrometheusScrapingEndpoint(); // GET /metrics

    app.Run();
}
finally
{
    await Log.CloseAndFlushAsync();
}

// Make the implicit Program class visible to test projects
public partial class Program
{
}