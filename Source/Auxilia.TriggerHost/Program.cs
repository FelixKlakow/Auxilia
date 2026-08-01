using Auxilia.Adapters.Email;
using Auxilia.Core.Client;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.Workflows.Client;

var builder = WebApplication.CreateBuilder(args);

// Host-owned storage: the email adapter's trigger/credential records and this host's audit
// trail. A trigger host holds its OWN credentials (e.g. the mailbox password) — connector
// secrets never leave the Core, so client-side intake credentials are host configuration
// by design, protected at rest with the host's protection key.
var platformData = new PlatformDataSettings();
builder.Configuration.GetSection("PlatformData").Bind(platformData);
builder.Services.AddSingleton(platformData);
builder.Services.AddPlatformEntity<MailboxTriggerRecord>(platformData);
builder.Services.AddPlatformEntity<SlotInstanceRecord>(platformData);
builder.Services.AddPlatformEntity<TriggerHealthRecord>(platformData);
builder.Services.AddPlatformEntity<AuditRecord>(platformData);
builder.Services.AddSettingsProtection(platformData);
builder.Services.AddSingleton<AuditLog>();

// A pure Core client: REST + SSE only — this host never touches the message bus.
builder.Services.AddCoreClient(
    builder.Configuration["Core:BaseAddress"] ?? "http://localhost:8080",
    builder.Configuration["Core:ApiKey"] ?? "");

// The workflow-domain library: interval scheduler + artifact chaining over the Core's
// filtered artifact SSE stream. Any app can embed the same pieces; this host is merely
// the always-on reference deployment.
builder.Services.AddWorkflowClient(builder.Configuration.GetSection("WorkflowClient").Bind);
builder.Services.AddWorkflowClientHosting();

// Email work-item intake, dispatching through the Core Run API on behalf of the trigger's
// principal.
builder.Services.Configure<MailboxTriggerAdapterSettings>(
    builder.Configuration.GetSection("MailboxTriggers"));
builder.Services.AddSingleton<IMailboxClientFactory, MailKitMailboxClientFactory>();
builder.Services.AddSingleton<ITaskSourceRunDispatcher, CoreClientRunDispatcher>();
builder.Services.AddHostedService<EmailTaskSourceAdapter>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();

public partial class Program;
