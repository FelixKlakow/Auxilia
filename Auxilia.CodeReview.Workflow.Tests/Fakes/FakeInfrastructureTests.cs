using Auxilia.Workflows.AiAgent;
using Auxilia.Workflows.SourceControl;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.CodeReview.Workflow.Tests.Fakes;

[TestFixture]
public sealed class FakeInfrastructureTests
{
    [Test]
    public void FakeAiAgent_ThrowsWhenTranscriptExhausted()
    {
        var agent = new FakeAiAgent(new Queue<ScriptedTurn>([new ScriptedTurn("x", "y")]));

        Assert.DoesNotThrowAsync(() => agent.OpenSessionAsync());
        Assert.ThrowsAsync<InvalidOperationException>(() => agent.OpenSessionAsync());
    }

    [Test]
    public async Task FakeAiAgent_TracksOpenSessionCallCount()
    {
        var turns = new Queue<ScriptedTurn>([
            new ScriptedTurn("a", "1"),
            new ScriptedTurn("b", "2"),
            new ScriptedTurn("c", "3"),
        ]);
        var agent = new FakeAiAgent(turns);

        await agent.OpenSessionAsync();
        await agent.OpenSessionAsync();
        await agent.OpenSessionAsync();

        Assert.That(agent.OpenSessionCallCount, Is.EqualTo(3));
    }

    [Test]
    public async Task FakeAiSession_MatchingPrompt_ReturnsScriptedResponse()
    {
        var session = new FakeAiSession(new ScriptedTurn("hello", "world"));

        var result = await session.ExecuteAsync("say hello");

        Assert.That(result, Is.EqualTo("world"));
    }

    [Test]
    public void FakeAiSession_MismatchedPrompt_Throws()
    {
        var session = new FakeAiSession(new ScriptedTurn("hello", "world"));

        Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync("goodbye"));
    }

    [Test]
    public async Task FakePullRequestAccess_PostComment_AppendsToPostedComments()
    {
        var access = new FakePullRequestAccess();

        await access.PostCommentAsync("first message", "file.cs", 1);
        await access.PostCommentAsync("second message", "other.cs", 2);

        Assert.That(access.PostedComments.Count, Is.EqualTo(2));
        Assert.That(access.PostedComments[0].Body, Is.EqualTo("first message"));
        Assert.That(access.PostedComments[0].FilePath, Is.EqualTo("file.cs"));
        Assert.That(access.PostedComments[0].LineNumber, Is.EqualTo(1));
    }

    [Test]
    public void FakePullRequestAccess_ThrowMode_PostCommentThrows()
    {
        var access = new FakePullRequestAccess(throwOnPost: new InvalidOperationException("post blocked"));

        Assert.ThrowsAsync<InvalidOperationException>(() => access.PostCommentAsync("msg"));
    }

    [Test]
    public void FakeWorkItemAccess_FailOnLookup_Throws()
    {
        var access = new FakeWorkItemAccess(failOnLookup: new KeyNotFoundException("work item not found"));

        Assert.ThrowsAsync<KeyNotFoundException>(() => access.GetWorkItemAsync("some-id"));
    }

    [Test]
    public void FakeAiAgentSlotHandler_Register_BindsKeyedInterface()
    {
        var agent = new FakeAiAgent(new Queue<ScriptedTurn>());
        var handler = new FakeAiAgentSlotHandler(agent);
        var services = new ServiceCollection();
        var config = new SlotConfiguration("fake-ai-agent", new Dictionary<string, string>());

        handler.Register(services, "primary-reviewer", config);

        var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredKeyedService<IAiAgent>("primary-reviewer");

        Assert.That(resolved, Is.SameAs(agent));
    }

    [Test]
    public void FakeSourceControlAccessSlotHandler_Register_BindsInterface()
    {
        var access = new FakeSourceControlAccess(workingPath: "/repo");
        var handler = new FakeSourceControlAccessSlotHandler(access);
        var services = new ServiceCollection();
        var config = new SlotConfiguration("fake-source-control", new Dictionary<string, string>());

        handler.Register(services, "repository", config);

        var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<ISourceControlAccess>();

        Assert.That(resolved, Is.SameAs(access));
    }
}
