using Auxilia.Workflows.AiAgent;
using Auxilia.Workflows.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Workflows.SlotPackages.Tests.AiAgent;

[TestFixture]
[Category("Unit")]
public class AgentChatPublisherTests
{
    private sealed class CapturingViewPublisher : IViewPublisher
    {
        public List<(string ViewName, object? Item)> Published { get; } = [];

        public Task PublishAsync<TItem>(string viewName, TItem item, CancellationToken ct = default)
        {
            Published.Add((viewName, item));
            return Task.CompletedTask;
        }
    }

    private static ViewDescriptor AgentChatView(string name = "agent-conversation")
        => new(name, "{}", ViewRendering.Custom, ViewLifecycle.LiveAndPersisted, AgentChatEntry.RendererKey);

    private static IServiceProvider BuildProvider(
        IViewPublisher? views = null, params ViewDescriptor[] declared)
    {
        var services = new ServiceCollection();
        if (views is not null)
            services.AddSingleton(views);
        if (declared.Length > 0)
            services.AddSingleton(new DeclaredViews(declared));
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task Create_WithDeclaredAgentChatView_PublishesToThatView()
    {
        var views = new CapturingViewPublisher();
        var publisher = AgentChatPublisher.Create(BuildProvider(views, AgentChatView()));

        await publisher.PublishUserAsync("Review this PR");

        Assert.That(publisher.IsActive, Is.True);
        Assert.That(views.Published, Has.Count.EqualTo(1));
        Assert.That(views.Published[0].ViewName, Is.EqualTo("agent-conversation"));
        var entry = (AgentChatEntry)views.Published[0].Item!;
        Assert.That(entry.Role, Is.EqualTo(AgentChatRole.User));
        Assert.That(entry.Content, Is.EqualTo("Review this PR"));
    }

    [Test]
    public async Task Create_WithoutViewPublisher_IsInactiveNoOp()
    {
        var publisher = AgentChatPublisher.Create(BuildProvider(views: null, AgentChatView()));

        await publisher.PublishUserAsync("ignored");

        Assert.That(publisher.IsActive, Is.False);
    }

    [Test]
    public async Task Create_WithoutAgentChatView_IsInactiveNoOp()
    {
        var views = new CapturingViewPublisher();
        var other = new ViewDescriptor("progress", "{}", ViewRendering.Log, ViewLifecycle.Live);
        var publisher = AgentChatPublisher.Create(BuildProvider(views, other));

        await publisher.PublishAssistantAsync("ignored");

        Assert.That(publisher.IsActive, Is.False);
        Assert.That(views.Published, Is.Empty);
    }

    [Test]
    public void Create_CustomViewWithForeignRendererKey_IsNotMatched()
    {
        var views = new CapturingViewPublisher();
        var foreign = new ViewDescriptor("plot", "{}", ViewRendering.Custom, ViewLifecycle.Live, "scatter-plot");

        var publisher = AgentChatPublisher.Create(BuildProvider(views, foreign));

        Assert.That(publisher.IsActive, Is.False);
    }

    [Test]
    public async Task PublishToolAsync_CarriesToolNameStateAndLabel()
    {
        var views = new CapturingViewPublisher();
        var publisher = AgentChatPublisher.Create(BuildProvider(views, AgentChatView()));

        await publisher.PublishToolAsync("read_file", "Success", "// content", "src/Widget.cs");

        var entry = (AgentChatEntry)views.Published.Single().Item!;
        Assert.Multiple(() =>
        {
            Assert.That(entry.Role, Is.EqualTo(AgentChatRole.Tool));
            Assert.That(entry.ToolName, Is.EqualTo("read_file"));
            Assert.That(entry.ToolState, Is.EqualTo("Success"));
            Assert.That(entry.Content, Is.EqualTo("// content"));
            Assert.That(entry.Label, Is.EqualTo("src/Widget.cs"));
        });
    }
}
