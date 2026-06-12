using System.Net;
using System.Text;
using System.Text.Json;
using Auxilia.Governance;
using Auxilia.Messaging;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.Views;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Auxilia.BackendService.Tests.ComponentTests;

/// <summary>
/// Drives the /mcp Streamable-HTTP endpoint with raw JSON-RPC posts: API-key authentication,
/// Policy Engine enforcement per tool, and round-trips against seeded platform data.
/// </summary>
[TestFixture]
[Category("Component")]
public class McpServerTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private FakeMessageBusClient _bus = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _bus = new FakeMessageBusClient();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("PlatformData:Backend", "InMemory");
            builder.UseSetting("Governance:BootstrapAdminUsername", "bootstrap-admin");
            builder.UseSetting("Governance:BootstrapAdminPassword", "bootstrap-pw-1");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMessageBusClient>();
                services.AddSingleton<IMessageBusClient>(_bus);
            });
        });
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => _factory.Dispose();

    // --- helpers -------------------------------------------------------------------------

    private HttpClient CreateClient() => _factory.CreateClient(
        new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<(Guid PrincipalId, string ApiKey)> CreateAiPrincipalAsync(
        string displayName, params string[] roles)
    {
        var directory = _factory.Services.GetRequiredService<PrincipalDirectory>();
        var (principal, apiKey) = await directory.CreateApiKeyPrincipalAsync(displayName, "AiAgent");
        foreach (var role in roles)
            await directory.AssignRoleAsync(principal.Id, role);
        return (principal.Id, apiKey);
    }

    private static object InitializeRequest() => new
    {
        jsonrpc = "2.0",
        id = 1,
        method = "initialize",
        @params = new
        {
            protocolVersion = "2025-03-26",
            capabilities = new { },
            clientInfo = new { name = "component-test", version = "1.0" }
        }
    };

    private static object ToolCallRequest(string name, object arguments) => new
    {
        jsonrpc = "2.0",
        id = 2,
        method = "tools/call",
        @params = new { name, arguments }
    };

    private static async Task<HttpResponseMessage> PostRpcAsync(
        HttpClient client, string? apiKey, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        if (apiKey is not null)
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }

    /// <summary>Posts a JSON-RPC request and returns the parsed JSON-RPC response envelope.</summary>
    private static async Task<JsonElement> PostRpcForResultAsync(
        HttpClient client, string apiKey, object payload)
    {
        var response = await PostRpcAsync(client, apiKey, payload);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var body = await response.Content.ReadAsStringAsync();
        var json = response.Content.Headers.ContentType?.MediaType == "text/event-stream"
            ? ExtractLastSseData(body)
            : body;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static string ExtractLastSseData(string sse)
        => sse.Replace("\r\n", "\n").Split('\n')
            .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
            .Select(line => line["data:".Length..].Trim())
            .Last();

    /// <summary>Runs initialize followed by tools/call and returns the tools/call "result".</summary>
    private async Task<JsonElement> CallToolAsync(string apiKey, string toolName, object arguments)
    {
        using var client = CreateClient();

        var initialize = await PostRpcForResultAsync(client, apiKey, InitializeRequest());
        Assert.That(initialize.TryGetProperty("result", out var initResult), Is.True,
            $"initialize must succeed, got: {initialize}");
        Assert.That(initResult.TryGetProperty("protocolVersion", out _), Is.True);

        var callResponse = await PostRpcForResultAsync(
            client, apiKey, ToolCallRequest(toolName, arguments));
        Assert.That(callResponse.TryGetProperty("result", out var result), Is.True,
            $"tools/call must return a result, got: {callResponse}");
        return result;
    }

    private static string ToolText(JsonElement toolResult)
        => toolResult.GetProperty("content")[0].GetProperty("text").GetString()!;

    private static bool IsToolError(JsonElement toolResult)
        => toolResult.TryGetProperty("isError", out var isError) && isError.GetBoolean();

    private async Task SeedRunAsync(WorkflowInstanceRecord record)
    {
        var instances = _factory.Services.GetRequiredService<IDataAccess<WorkflowInstanceRecord>>();
        await instances.SaveAsync(record);
    }

    // --- authentication ------------------------------------------------------------------

    [Test]
    public async Task McpRequest_WithoutApiKey_Returns401()
    {
        using var client = CreateClient();

        var response = await PostRpcAsync(client, apiKey: null, InitializeRequest());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task McpRequest_WithWrongApiKey_Returns401()
    {
        using var client = CreateClient();

        var response = await PostRpcAsync(client, "aux_definitely-not-a-valid-key", InitializeRequest());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // --- policy enforcement --------------------------------------------------------------

    [Test]
    public async Task TriggerWorkflow_PrincipalWithoutRoles_ReturnsPolicyDenial()
    {
        var (_, apiKey) = await CreateAiPrincipalAsync("Roleless Agent");

        var result = await CallToolAsync(apiKey, "trigger_workflow", new
        {
            workflowType = "DeniedWorkflow",
            workflowPackageUri = "https://packages.example.com/denied.workflow.zip"
        });

        Assert.That(IsToolError(result), Is.True, "Denial must surface as a tool error.");
        Assert.That(ToolText(result), Does.Contain("policy"));
        Assert.That(ToolText(result), Does.Contain("workflow.trigger"));
    }

    [Test]
    public async Task TriggerWorkflow_WithUserRole_PublishesRunWorkflowCommandWithCallerPrincipal()
    {
        var (principalId, apiKey) = await CreateAiPrincipalAsync("Trigger Agent", BuiltInRoles.User);

        var result = await CallToolAsync(apiKey, "trigger_workflow", new
        {
            workflowType = "McpTestWorkflow",
            workflowPackageUri = "https://packages.example.com/mcp-test.workflow.zip",
            workItemId = "WI-42"
        });

        Assert.That(IsToolError(result), Is.False, $"Expected success, got: {ToolText(result)}");
        var payload = JsonDocument.Parse(ToolText(result)).RootElement;
        var commandId = payload.GetProperty("commandId").GetGuid();

        var command = _bus.PublishedMessages
            .Where(p => p.Topic == "workflow.run-commands")
            .Select(p => p.Message)
            .OfType<RunWorkflowCommand>()
            .Single(c => c.CommandId == commandId);
        Assert.That(command.RequestedBy, Is.EqualTo(principalId));
        Assert.That(command.WorkflowType, Is.EqualTo("McpTestWorkflow"));
        Assert.That(command.WorkflowPackageUri,
            Is.EqualTo("https://packages.example.com/mcp-test.workflow.zip"));
        Assert.That(command.Context["WorkItemId"], Is.EqualTo("WI-42"));
    }

    // --- run observation -----------------------------------------------------------------

    [Test]
    public async Task GetWorkflowStatus_ReturnsSeededRecord()
    {
        var (_, apiKey) = await CreateAiPrincipalAsync("Status Agent", BuiltInRoles.User);
        var run = new WorkflowInstanceRecord
        {
            Id = Guid.NewGuid(),
            WorkflowType = "StatusWorkflow",
            State = "Failed",
            CreatedUtc = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero),
            CompletedUtc = new DateTimeOffset(2026, 6, 1, 10, 5, 0, TimeSpan.Zero),
            ErrorMessage = "container exited with code 1"
        };
        await SeedRunAsync(run);

        var result = await CallToolAsync(apiKey, "get_workflow_status",
            new { instanceId = run.Id.ToString("D") });

        Assert.That(IsToolError(result), Is.False, $"Expected success, got: {ToolText(result)}");
        var payload = JsonDocument.Parse(ToolText(result)).RootElement;
        Assert.That(payload.GetProperty("instanceId").GetGuid(), Is.EqualTo(run.Id));
        Assert.That(payload.GetProperty("workflowType").GetString(), Is.EqualTo("StatusWorkflow"));
        Assert.That(payload.GetProperty("state").GetString(), Is.EqualTo("Failed"));
        Assert.That(payload.GetProperty("createdUtc").GetDateTimeOffset(), Is.EqualTo(run.CreatedUtc));
        Assert.That(payload.GetProperty("completedUtc").GetDateTimeOffset(), Is.EqualTo(run.CompletedUtc));
        Assert.That(payload.GetProperty("errorMessage").GetString(),
            Is.EqualTo("container exited with code 1"));
    }

    [Test]
    public async Task ListRuns_ContainsSeededRecordNewestFirst()
    {
        var (_, apiKey) = await CreateAiPrincipalAsync("List Agent", BuiltInRoles.User);
        var older = new WorkflowInstanceRecord
        {
            Id = Guid.NewGuid(),
            WorkflowType = "ListWorkflow",
            State = "Success",
            CreatedUtc = new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero)
        };
        var newer = new WorkflowInstanceRecord
        {
            Id = Guid.NewGuid(),
            WorkflowType = "ListWorkflow",
            State = "Running",
            CreatedUtc = new DateTimeOffset(2026, 6, 2, 8, 0, 0, TimeSpan.Zero)
        };
        await SeedRunAsync(older);
        await SeedRunAsync(newer);

        var result = await CallToolAsync(apiKey, "list_runs", new { limit = 100 });

        Assert.That(IsToolError(result), Is.False, $"Expected success, got: {ToolText(result)}");
        var runs = JsonDocument.Parse(ToolText(result)).RootElement.GetProperty("runs");
        var ids = runs.EnumerateArray()
            .Select(r => r.GetProperty("instanceId").GetGuid())
            .ToList();
        Assert.That(ids, Does.Contain(older.Id));
        Assert.That(ids, Does.Contain(newer.Id));
        Assert.That(ids.IndexOf(newer.Id), Is.LessThan(ids.IndexOf(older.Id)),
            "Runs must be ordered newest first.");
    }

    [Test]
    public async Task ListViews_ParsesViewsJsonIntoDescriptors()
    {
        var (_, apiKey) = await CreateAiPrincipalAsync("Views Agent", BuiltInRoles.User);
        var descriptors = new List<ViewDescriptor>
        {
            new("progress", "{}", ViewRendering.Log, ViewLifecycle.LiveAndPersisted)
        };
        var run = new WorkflowInstanceRecord
        {
            Id = Guid.NewGuid(),
            WorkflowType = "ViewsWorkflow",
            State = "Running",
            CreatedUtc = DateTimeOffset.UtcNow,
            ViewsJson = JsonSerializer.Serialize(descriptors)
        };
        await SeedRunAsync(run);

        var result = await CallToolAsync(apiKey, "list_views",
            new { instanceId = run.Id.ToString("D") });

        Assert.That(IsToolError(result), Is.False, $"Expected success, got: {ToolText(result)}");
        var views = JsonDocument.Parse(ToolText(result)).RootElement.GetProperty("views");
        Assert.That(views.GetArrayLength(), Is.EqualTo(1));
        Assert.That(views[0].GetProperty("name").GetString(), Is.EqualTo("progress"));
        Assert.That(views[0].GetProperty("rendering").GetString(), Is.EqualTo("Log"));
        Assert.That(views[0].GetProperty("lifecycle").GetString(), Is.EqualTo("LiveAndPersisted"));
    }

    [Test]
    public async Task GetViewData_ReturnsSeededRecordsInSequenceOrder()
    {
        var (_, apiKey) = await CreateAiPrincipalAsync("ViewData Agent", BuiltInRoles.User);
        var run = new WorkflowInstanceRecord
        {
            Id = Guid.NewGuid(),
            WorkflowType = "ViewDataWorkflow",
            State = "Success",
            CreatedUtc = DateTimeOffset.UtcNow
        };
        await SeedRunAsync(run);

        var viewData = _factory.Services.GetRequiredService<IDataAccess<ViewDataRecord>>();
        foreach (var sequence in new long[] { 2, 0, 1 }) // saved out of order on purpose
            await viewData.SaveAsync(new ViewDataRecord
            {
                Id = ViewDataRecord.IdFor(run.Id, "progress", sequence),
                WorkflowInstanceId = run.Id,
                ViewName = "progress",
                Sequence = sequence,
                PayloadJson = $"{{\"step\":{sequence}}}",
                TimestampUtc = DateTimeOffset.UtcNow
            });

        var result = await CallToolAsync(apiKey, "get_view_data",
            new { instanceId = run.Id.ToString("D"), viewName = "progress" });

        Assert.That(IsToolError(result), Is.False, $"Expected success, got: {ToolText(result)}");
        var items = JsonDocument.Parse(ToolText(result)).RootElement.GetProperty("items");
        Assert.That(items.GetArrayLength(), Is.EqualTo(3));
        Assert.That(items.EnumerateArray().Select(i => i.GetProperty("sequence").GetInt64()),
            Is.EqualTo(new long[] { 0, 1, 2 }));
        Assert.That(items[1].GetProperty("payloadJson").GetString(), Is.EqualTo("{\"step\":1}"));
    }

    // --- cancellation --------------------------------------------------------------------

    [Test]
    public async Task CancelWorkflow_WithOperatorRole_PublishesCancelCommandAndAudits()
    {
        var (principalId, apiKey) = await CreateAiPrincipalAsync("Cancel Agent", BuiltInRoles.Operator);
        var run = new WorkflowInstanceRecord
        {
            Id = Guid.NewGuid(),
            WorkflowType = "CancelWorkflow",
            State = "Running",
            CreatedUtc = DateTimeOffset.UtcNow
        };
        await SeedRunAsync(run);

        var result = await CallToolAsync(apiKey, "cancel_workflow",
            new { instanceId = run.Id.ToString("D") });

        Assert.That(IsToolError(result), Is.False, $"Expected success, got: {ToolText(result)}");
        var cancel = _bus.PublishedMessages
            .Where(p => p.Topic == "workflow.cancel-commands")
            .Select(p => p.Message)
            .OfType<CancelWorkflowCommand>()
            .Single(c => c.WorkflowInstanceId == run.Id);
        Assert.That(cancel.WorkflowInstanceId, Is.EqualTo(run.Id));

        var auditRecords = _factory.Services.GetRequiredService<IDataAccess<AuditRecord>>();
        var audits = await auditRecords.ReadAsync();
        Assert.That(audits.Any(a =>
                a.Action == "mcp.cancel" &&
                a.Actor == principalId.ToString("D") &&
                a.Subject == run.Id.ToString("D")),
            Is.True, "Cancellation must be audited as mcp.cancel with the principal as actor.");
    }

    // --- audit log -----------------------------------------------------------------------

    [Test]
    public async Task ReadAudit_DeniedForUserRole_AllowedAfterAssigningAuditor()
    {
        var (principalId, apiKey) = await CreateAiPrincipalAsync("Audit Agent", BuiltInRoles.User);

        var denied = await CallToolAsync(apiKey, "read_audit", new { limit = 10 });
        Assert.That(IsToolError(denied), Is.True, "User role must not read the audit log.");
        Assert.That(ToolText(denied), Does.Contain("policy"));
        Assert.That(ToolText(denied), Does.Contain("audit.read"));

        var directory = _factory.Services.GetRequiredService<PrincipalDirectory>();
        await directory.AssignRoleAsync(principalId, BuiltInRoles.Auditor);

        var allowed = await CallToolAsync(apiKey, "read_audit", new { limit = 10 });
        Assert.That(IsToolError(allowed), Is.False, $"Expected success, got: {ToolText(allowed)}");
        var entries = JsonDocument.Parse(ToolText(allowed)).RootElement.GetProperty("entries");
        Assert.That(entries.GetArrayLength(), Is.GreaterThan(0));
        Assert.That(entries[0].TryGetProperty("action", out _), Is.True);
    }
}
