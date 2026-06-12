using System.Text.Json;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.BackendService.Tests.ComponentTests;

/// <summary>
/// Custom view rendering through the real host: a run declaring a Custom view with the
/// "agent-chat" renderer key renders the BlazorAgentView chat markup on the run-detail page;
/// an unknown renderer key falls back to raw items with a visible notice.
/// </summary>
[TestFixture]
[Category("Component")]
public class CustomViewRendererTests : DashboardComponentTestBase
{
    private readonly Guid _chatRunId = Guid.NewGuid();
    private readonly Guid _unknownRendererRunId = Guid.NewGuid();

    [OneTimeSetUp]
    public async Task SeedRunsWithCustomViews()
    {
        var instances = Factory.Services.GetRequiredService<IDataAccess<WorkflowInstanceRecord>>();
        var viewData = Factory.Services.GetRequiredService<IDataAccess<ViewDataRecord>>();

        await instances.SaveAsync(NewRun(_chatRunId, new ViewDescriptor(
            "agent-conversation", "{}", ViewRendering.Custom, ViewLifecycle.LiveAndPersisted,
            AgentChatEntry.RendererKey)));
        await SaveEntryAsync(viewData, _chatRunId, "agent-conversation", 1,
            new AgentChatEntry(AgentChatRole.User, "Please review src/Widget.cs", DateTimeOffset.UtcNow));
        await SaveEntryAsync(viewData, _chatRunId, "agent-conversation", 2,
            new AgentChatEntry(AgentChatRole.Assistant, "Looking at the diff now.", DateTimeOffset.UtcNow));
        await SaveEntryAsync(viewData, _chatRunId, "agent-conversation", 3,
            new AgentChatEntry(AgentChatRole.Tool, "// fake content", DateTimeOffset.UtcNow,
                Label: "src/Widget.cs", ToolName: "read_file", ToolState: "Success"));

        await instances.SaveAsync(NewRun(_unknownRendererRunId, new ViewDescriptor(
            "plot", "{}", ViewRendering.Custom, ViewLifecycle.LiveAndPersisted, "scatter-plot")));
        await SaveEntryAsync(viewData, _unknownRendererRunId, "plot", 1, new { x = 1, y = "raw-plot-point" });
    }

    private static WorkflowInstanceRecord NewRun(Guid id, ViewDescriptor view) => new()
    {
        Id = id,
        WorkflowType = "code-review",
        State = "Success",
        CreatedUtc = DateTimeOffset.UtcNow,
        CompletedUtc = DateTimeOffset.UtcNow,
        ViewsJson = JsonSerializer.Serialize(new List<ViewDescriptor> { view })
    };

    private static Task SaveEntryAsync<TItem>(
        IDataAccess<ViewDataRecord> viewData, Guid instanceId, string viewName, long sequence, TItem item)
        => viewData.SaveAsync(new ViewDataRecord
        {
            Id = ViewDataRecord.IdFor(instanceId, viewName, sequence),
            WorkflowInstanceId = instanceId,
            ViewName = viewName,
            Sequence = sequence,
            PayloadJson = JsonSerializer.Serialize(item),
            TimestampUtc = DateTimeOffset.UtcNow
        });

    [Test]
    public async Task RunDetail_AgentChatDescriptor_RendersBlazorAgentViewChatMarkup()
    {
        using var client = CreateClient();
        var (cookie, _) = await LoginAsync(client, AdminUsername, AdminPassword);

        var html = await GetHtmlAsync(client, $"/runs/{_chatRunId}", cookie);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("bav-chat-view"), "the package's chat component must render");
            Assert.That(html, Does.Contain("bav-message-user"), "the user entry must render as a user bubble");
            Assert.That(html, Does.Contain("bav-message-assistant"), "the assistant entry must render");
            Assert.That(html, Does.Contain("bav-tool-call"), "the tool entry must render as a tool-call card");
            Assert.That(html, Does.Contain("read_file"), "the tool name must be visible");
            Assert.That(html, Does.Not.Contain("is not installed"), "no fallback notice for a registered key");
        });
    }

    [Test]
    public async Task RunDetail_UnknownRendererKey_FallsBackToRawItemsWithNotice()
    {
        using var client = CreateClient();
        var (cookie, _) = await LoginAsync(client, AdminUsername, AdminPassword);

        var html = await GetHtmlAsync(client, $"/runs/{_unknownRendererRunId}", cookie);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("scatter-plot&#x27; is not installed").Or.Contain("scatter-plot' is not installed"),
                "the muted notice must name the missing renderer");
            Assert.That(html, Does.Contain("raw-plot-point"), "raw items must still be shown");
            Assert.That(html, Does.Not.Contain("bav-chat-view"));
        });
    }
}
