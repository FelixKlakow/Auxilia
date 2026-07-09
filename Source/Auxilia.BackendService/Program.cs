using System.Reflection;
using Auxilia.BackendService;
using Auxilia.BackendService.Dashboard;
using Auxilia.BackendService.PlatformHost;
using BlazorAgentView.Services;
using Auxilia.Governance;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.Workflows.Messaging;
using Microsoft.AspNetCore.DataProtection;
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

    // --- HTTP client factory (used by the session-terminal reverse proxy to reach ttyd) ---
    builder.Services.AddHttpClient("session-terminal");

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
    builder.Services.AddPlatformEntity<ProviderCatalogRecord>(platformDataSettings);
    builder.Services.AddPlatformEntity<WorkflowCatalogRecord>(platformDataSettings);
    builder.Services.AddPlatformEntity<WorkflowConfigurationRecord>(platformDataSettings);
    builder.Services.AddPlatformEntity<WorkflowPackageRecord>(platformDataSettings);
    builder.Services.AddPlatformEntity<WorkflowSchemaRecord>(platformDataSettings);
    builder.Services.AddPlatformEntity<SlotInstanceRecord>(platformDataSettings);
    builder.Services.AddPlatformEntity<ConnectorRecord>(platformDataSettings);
    builder.Services.AddPlatformEntity<MailboxTriggerRecord>(platformDataSettings);
    builder.Services.AddPlatformEntity<TriggerHealthRecord>(platformDataSettings);
    builder.Services.AddPlatformEntity<DashboardRecord>(platformDataSettings);
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<AuditLog>();
    builder.Services.AddSingleton<WorkflowStatusPublisher>();

    // --- Governance (identity, accounts, Policy Engine) ---
    var governanceSettings = new Auxilia.Governance.GovernanceSettings();
    builder.Configuration.GetSection("Governance").Bind(governanceSettings);
    builder.Services.AddGovernance(platformDataSettings, governanceSettings);

    // --- Dashboard: cookie sessions + SignalR fan-out ---
    // Durable stands persist the data-protection keys so session cookies survive restarts.
    if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keysPath));
    builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.Cookies
            .CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(o =>
        {
            o.Cookie.Name = "auxilia.session";
            o.Cookie.HttpOnly = true;
            o.ExpireTimeSpan = TimeSpan.FromHours(8);
            o.SlidingExpiration = true;
            o.LoginPath = "/login";
            // Browser navigations (Accept: text/html) land on the login page; API and hub
            // clients get the bare status code — a redirect would corrupt their protocols.
            o.Events.OnRedirectToLogin = ctx =>
            {
                if (ctx.Request.Headers.Accept.Any(
                        v => v?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true))
                    ctx.Response.Redirect(ctx.RedirectUri);
                else
                    ctx.Response.StatusCode = 401;
                return Task.CompletedTask;
            };
            o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
        })
        .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions,
            Auxilia.BackendService.Mcp.McpApiKeyAuthenticationHandler>(
            Auxilia.BackendService.Mcp.McpApiKeyAuthenticationHandler.SchemeName, null);
    builder.Services.AddAuthorization();
    builder.Services.AddSignalR();
    builder.Services.AddSingleton<Auxilia.BackendService.Dashboard.LiveViewBroker>();
    builder.Services.AddSingleton<Auxilia.BackendService.Dashboard.DashboardComposer>();
    builder.Services.AddSingleton<Auxilia.BackendService.Dashboard.ProviderCatalogService>();
    // The platform's trigger vocabulary: assembled at runtime from these bindings — a new
    // trigger kind is one additional registration, nothing else changes.
    builder.Services.AddSingleton<Auxilia.BackendService.Dashboard.Triggers.ITriggerKindBinding,
        Auxilia.BackendService.Dashboard.Triggers.ScheduleTriggerBinding>();
    builder.Services.AddSingleton<Auxilia.BackendService.Dashboard.Triggers.ITriggerKindBinding,
        Auxilia.BackendService.Dashboard.Triggers.ArtifactTriggerBinding>();
    builder.Services.AddSingleton<Auxilia.BackendService.Dashboard.Triggers.ITriggerKindBinding,
        Auxilia.BackendService.Dashboard.Triggers.MailboxTriggerBinding>();
    builder.Services.AddSingleton<Auxilia.BackendService.Dashboard.Triggers.TriggerKindCatalog>();
    // Connect flows: sign-in alternatives to pasting credentials, referenced from setting
    // descriptors by key — like trigger kinds, one registration adds a flow.
    var anthropicConnectSettings = new Auxilia.BackendService.Dashboard.Connect.AnthropicConnectSettings();
    builder.Configuration.GetSection("Connect:AnthropicClaude").Bind(anthropicConnectSettings);
    builder.Services.AddSingleton(anthropicConnectSettings);
    builder.Services.AddSingleton<Auxilia.BackendService.Dashboard.Connect.IConnectFlow,
        Auxilia.BackendService.Dashboard.Connect.AnthropicClaudeConnectFlow>();
    var gitHubConnectSettings = new Auxilia.BackendService.Dashboard.Connect.GitHubConnectSettings();
    builder.Configuration.GetSection("Connect:GitHub").Bind(gitHubConnectSettings);
    builder.Services.AddSingleton(gitHubConnectSettings);
    builder.Services.AddSingleton<Auxilia.BackendService.Dashboard.Connect.IConnectFlow,
        Auxilia.BackendService.Dashboard.Connect.GitHubConnectFlow>();
    builder.Services.AddSingleton<Auxilia.BackendService.Dashboard.Connect.IConnectFlow,
        Auxilia.BackendService.Dashboard.Connect.GmailConnectFlow>();
    builder.Services.AddSingleton<Auxilia.BackendService.Dashboard.Connect.ConnectFlowRegistry>();
    builder.Services.AddSingleton<Auxilia.BackendService.Dashboard.ConnectorService>();
    builder.Services.AddSingleton<Auxilia.BackendService.Dashboard.WorkflowConfigurationEditorService>();
    builder.Services.AddSingleton<Auxilia.BackendService.Dashboard.SlotInstanceService>();
    builder.Services.AddSingleton<Auxilia.BackendService.Dashboard.WorkflowRerunService>();
    builder.Services.AddSingleton<Auxilia.BackendService.Dashboard.RunCancelService>();
    builder.Services.AddSingleton<Auxilia.BackendService.Dashboard.DashboardStatsService>();
    builder.Services.AddHostedService<Auxilia.BackendService.Dashboard.ViewDataFanOutHandler>();

    // --- Dashboard UI (interactive-server Blazor) ---
    builder.Services.AddRazorComponents().AddInteractiveServerComponents();
    builder.Services.AddSingleton<Auxilia.BackendService.Dashboard.ViewRendererRegistry>();
    builder.Services.AddBlazorAgentView();
    builder.Services.AddViewRenderer<Auxilia.BackendService.Components.AgentChatRenderer>(
        Auxilia.Workflows.Views.AgentChatEntry.RendererKey);
    builder.Services.AddCascadingAuthenticationState();

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

    // --- Mailbox triggers (the v1 integration adapter): mailboxes are platform data
    //     (email slot instances referenced by trigger records), not deployment settings ---
    builder.Services.Configure<Auxilia.Adapters.Email.MailboxTriggerAdapterSettings>(
        builder.Configuration.GetSection("MailboxTriggers"));
    builder.Services.AddSingleton<Auxilia.Adapters.Email.IMailboxClientFactory,
        Auxilia.Adapters.Email.MailKitMailboxClientFactory>();
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
    Auxilia.BackendService.Dashboard.SessionTerminalProxy.MapSessionTerminal(app);
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