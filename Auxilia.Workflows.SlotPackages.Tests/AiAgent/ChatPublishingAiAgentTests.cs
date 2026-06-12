using Auxilia.Workflows.AiAgent;
using Auxilia.Workflows.Views;

namespace Auxilia.Workflows.SlotPackages.Tests.AiAgent;

[TestFixture]
[Category("Unit")]
public class ChatPublishingAiAgentTests
{
    private sealed class CapturingViewPublisher : IViewPublisher
    {
        public List<AgentChatEntry> Entries { get; } = [];

        public Task PublishAsync<TItem>(string viewName, TItem item, CancellationToken ct = default)
        {
            Entries.Add((AgentChatEntry)(object)item!);
            return Task.CompletedTask;
        }
    }

    private sealed class StubAgent(string response) : IAiAgent
    {
        public Task<IAiSession> OpenSessionAsync(
            AiSessionOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IAiSession>(new StubSession(response));
    }

    private sealed class StubSession(string response) : IAiSession
    {
        public Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult(response);

        public Task CompactAsync(string focusDescription, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Test]
    public async Task ExecuteAsync_PublishesUserTurnThenAssistantTurn()
    {
        var views = new CapturingViewPublisher();
        var chat = new AgentChatPublisher(views, "agent-conversation");
        var agent = new ChatPublishingAiAgent(new StubAgent("the answer"), chat);

        await using var session = await agent.OpenSessionAsync();
        var response = await session.ExecuteAsync("the question");

        Assert.That(response, Is.EqualTo("the answer"));
        Assert.That(views.Entries, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(views.Entries[0].Role, Is.EqualTo(AgentChatRole.User));
            Assert.That(views.Entries[0].Content, Is.EqualTo("the question"));
            Assert.That(views.Entries[1].Role, Is.EqualTo(AgentChatRole.Assistant));
            Assert.That(views.Entries[1].Content, Is.EqualTo("the answer"));
        });
    }

    [Test]
    public async Task ExecuteAsync_InactivePublisher_StillReturnsResponse()
    {
        var chat = new AgentChatPublisher(views: null, viewName: null);
        var agent = new ChatPublishingAiAgent(new StubAgent("ok"), chat);

        await using var session = await agent.OpenSessionAsync();

        Assert.That(await session.ExecuteAsync("q"), Is.EqualTo("ok"));
    }
}
