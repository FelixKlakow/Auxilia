using Auxilia.Adapters.Email;
using Auxilia.Core.Client;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.WorkflowStudio;
using Auxilia.WorkflowStudio.Data;
using Auxilia.WorkflowStudio.Services;
using Auxilia.WorkflowStudio.Triggers;

var builder = WebApplication.CreateBuilder(args);

// The Studio's own database (workflow-domain: types + triggers). Never shared with the Core.
var platformData = new PlatformDataSettings();
builder.Configuration.GetSection("PlatformData").Bind(platformData);
builder.Services.AddSingleton(platformData);
builder.Services.AddPlatformEntity<StudioWorkflowTypeRecord>(platformData);
builder.Services.AddPlatformEntity<ScheduledTriggerRecord>(platformData);
builder.Services.AddPlatformEntity<ArtifactTriggerRecord>(platformData);
builder.Services.AddPlatformEntity<AuditRecord>(platformData);
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton(TimeProvider.System);

// Drive the Core purely over its API — no shared database. The only bus dependency is the
// read-only artifact-event subscription that feeds the artifact-completion trigger.
builder.Services.AddCoreClient(
    builder.Configuration["Core:BaseAddress"] ?? "http://localhost:8080",
    builder.Configuration["Core:ApiKey"] ?? "");
builder.Services.AddSingleton<IMessageBusClient>(_ =>
    RabbitMqClient.CreateAsync(
        builder.Configuration["RabbitMq:Host"] ?? "localhost",
        int.Parse(builder.Configuration["RabbitMq:Port"] ?? "5672"),
        builder.Configuration["RabbitMq:UserName"] ?? "guest",
        builder.Configuration["RabbitMq:Password"] ?? "guest").GetAwaiter().GetResult());

builder.Services.AddSingleton<WorkflowTypeCatalog>();
builder.Services.AddSingleton<WorkflowAuthoringService>();

// Triggers (workflow-domain): interval scheduler + artifact-completion chaining, dispatching via
// the Core Run API. These are the schedulers dissolved out of BackendService.
builder.Services.Configure<TriggerSettings>(builder.Configuration.GetSection("Triggers"));
builder.Services.AddHostedService<TriggerScheduler>();
builder.Services.AddHostedService<ArtifactTriggerHandler>();

// Mailbox trigger integration adapter (email task source), hosted here and dispatching runs via the
// Core Run API instead of the bus. The mailbox credential is still read as a SlotInstanceRecord (email
// slot); converting it into a Core connector is a separate later step of the retirement plan.
builder.Services.AddPlatformEntity<MailboxTriggerRecord>(platformData);
builder.Services.AddPlatformEntity<SlotInstanceRecord>(platformData);
builder.Services.AddPlatformEntity<TriggerHealthRecord>(platformData);
builder.Services.AddSettingsProtection(platformData);
builder.Services.Configure<MailboxTriggerAdapterSettings>(builder.Configuration.GetSection("MailboxTriggers"));
builder.Services.AddSingleton<IMailboxClientFactory, MailKitMailboxClientFactory>();
builder.Services.AddSingleton<ITaskSourceRunDispatcher, CoreClientRunDispatcher>();
builder.Services.AddHostedService<EmailTaskSourceAdapter>();

var app = builder.Build();

// --- Workflow types (the workflow-domain catalog) ---
app.MapPost("/api/workflow-types", async (
        RegisterWorkflowType request, WorkflowTypeCatalog catalog, CancellationToken ct) =>
    Results.Ok(await catalog.RegisterAsync(request, ct)));

app.MapGet("/api/workflow-types", async (WorkflowTypeCatalog catalog, CancellationToken ct) =>
    Results.Ok(await catalog.ListAsync(ct)));

app.MapGet("/api/workflow-types/{name}", async (
        string name, WorkflowTypeCatalog catalog, CancellationToken ct) =>
    await catalog.GetByNameAsync(name, ct) is { } type ? Results.Ok(type) : Results.NotFound());

// --- Author + dispatch (produces and runs a Core run configuration) ---
app.MapPost("/api/configure", async (
        ConfigureWorkflow request, WorkflowAuthoringService authoring, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await authoring.ConfigureAsync(request, ct));
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/configured/{coreConfigId:guid}/run", async (
        Guid coreConfigId, WorkflowAuthoringService authoring, CancellationToken ct) =>
    Results.Ok(await authoring.RunAsync(coreConfigId, ct)));

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();

// Exposed for WebApplicationFactory-based component tests.
public partial class Program;
