using Auxilia.Core.Client;
using Auxilia.PlatformData;
using Auxilia.WorkflowStudio;
using Auxilia.WorkflowStudio.Data;
using Auxilia.WorkflowStudio.Services;

var builder = WebApplication.CreateBuilder(args);

// The Studio's own database (workflow-domain: types). Never shared with the Core.
var platformData = new PlatformDataSettings();
builder.Configuration.GetSection("PlatformData").Bind(platformData);
builder.Services.AddSingleton(platformData);
builder.Services.AddPlatformEntity<StudioWorkflowTypeRecord>(platformData);
builder.Services.AddSingleton(TimeProvider.System);

// Drive the Core purely over its API — no bus, no shared database.
builder.Services.AddCoreClient(
    builder.Configuration["Core:BaseAddress"] ?? "http://localhost:8080",
    builder.Configuration["Core:ApiKey"] ?? "");

builder.Services.AddSingleton<WorkflowTypeCatalog>();
builder.Services.AddSingleton<WorkflowAuthoringService>();

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
