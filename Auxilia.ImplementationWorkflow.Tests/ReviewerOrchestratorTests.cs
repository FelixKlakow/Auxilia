using Auxilia.ImplementationWorkflow.Context;
using Auxilia.ImplementationWorkflow.Mcp;
using Auxilia.ImplementationWorkflow.Tests.Fakes;
using Auxilia.Workflows.TaskSource;

namespace Auxilia.ImplementationWorkflow.Tests;

[TestFixture]
public sealed class ReviewerOrchestratorTests
{
    private static AgentCompletionResult MakeAgentResult() =>
        new("Implementation done.", "feature/wi-1", "WI-1");

    private static ImplementationContext MakeContext() =>
        new(new WorkItem("WI-1", "Test Task", "Implement the feature.", null, null, []),
            "instructions", "feature/wi-1", "/repo");

    [Test]
    public async Task ReviewerDisabled_ReturnsEmpty_NoSessionOpened()
    {
        var agent = new FakeAiAgent(new Queue<IReadOnlyList<ScriptedTurn>>([]));
        var orchestrator = new ReviewerOrchestrator(
            agent,
            new FakeSourceControlWriteAccess(),
            new FakePullRequestAccess(),
            new ImplementationWorkflowConfiguration { ReviewerEnabled = false });

        var result = await orchestrator.RunAsync(MakeAgentResult(), MakeContext());

        Assert.That(result, Is.Empty);
        Assert.That(agent.OpenSessionCallCount, Is.EqualTo(0));
    }

    [Test]
    public async Task ReviewerEnabled_ToolCallRecordsNote_NoteReturnedWithCorrectSeverity()
    {
        FakeAiAgent? agentRef = null;
        var turns = new Queue<IReadOnlyList<ScriptedTurn>>([
            [new ScriptedTurn("", "", () =>
            {
                var sink = agentRef?.LastSessionOptions?.CapabilityTools?
                    .OfType<ImplementationReviewResultSinkMcpTools>().FirstOrDefault();
                sink?.RecordReviewNote("Potential null reference", "src/Handler.cs", ReviewNoteSeverity.Warning);
                return Task.CompletedTask;
            })]
        ]);
        agentRef = new FakeAiAgent(turns);

        var orchestrator = new ReviewerOrchestrator(
            agentRef,
            new FakeSourceControlWriteAccess(),
            new FakePullRequestAccess(),
            new ImplementationWorkflowConfiguration { ReviewerEnabled = true });

        var result = await orchestrator.RunAsync(MakeAgentResult(), MakeContext());

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Severity, Is.EqualTo(ReviewNoteSeverity.Warning));
        Assert.That(result[0].Description, Is.EqualTo("Potential null reference"));
    }

    [Test]
    public async Task ReviewerEnabled_NoToolCall_ReturnsEmptyNotes()
    {
        var agent = new FakeAiAgent(new Queue<IReadOnlyList<ScriptedTurn>>([
            [new ScriptedTurn("", "")]
        ]));

        var orchestrator = new ReviewerOrchestrator(
            agent,
            new FakeSourceControlWriteAccess(),
            new FakePullRequestAccess(),
            new ImplementationWorkflowConfiguration { ReviewerEnabled = true });

        var result = await orchestrator.RunAsync(MakeAgentResult(), MakeContext());

        Assert.That(result, Is.Empty);
        Assert.That(agent.OpenSessionCallCount, Is.EqualTo(1));
    }
}
