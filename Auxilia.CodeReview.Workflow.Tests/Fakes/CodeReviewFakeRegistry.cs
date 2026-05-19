using Auxilia.Workflows;

namespace Auxilia.CodeReview.Workflow.Tests.Fakes;

public sealed class CodeReviewFakeRegistry
{
    private readonly FakeSourceControlAccess _sourceControl;
    private readonly FakePullRequestAccess _pullRequest;
    private readonly FakeWorkItemAccess _workItems;
    private readonly FakeAiAgent _aiAgent;

    public CodeReviewFakeRegistry(
        FakeSourceControlAccess sourceControl,
        FakePullRequestAccess pullRequest,
        FakeWorkItemAccess workItems,
        FakeAiAgent aiAgent)
    {
        _sourceControl = sourceControl;
        _pullRequest = pullRequest;
        _workItems = workItems;
        _aiAgent = aiAgent;
    }

    public void Register(SlotHandlerResolver resolver)
    {
        resolver.Register("fake-source-control", new FakeSourceControlAccessSlotHandler(_sourceControl));
        resolver.Register("fake-pull-request", new FakePullRequestAccessSlotHandler(_pullRequest));
        resolver.Register("fake-work-item", new FakeWorkItemAccessSlotHandler(_workItems));
        resolver.Register("fake-ai-agent", new FakeAiAgentSlotHandler(_aiAgent));
    }

    public IReadOnlyList<(string Name, string ProviderType, Dictionary<string, string> Settings)> BuildHarnessSlots()
        =>
        [
            ("repository",          "fake-source-control", new Dictionary<string, string>()),
            ("pull-request",        "fake-pull-request",   new Dictionary<string, string>()),
            ("work-items",          "fake-work-item",      new Dictionary<string, string>()),
            ("primary-reviewer",   "fake-ai-agent",       new Dictionary<string, string>()),
            ("secondary-reviewer", "fake-ai-agent",       new Dictionary<string, string>()),
        ];
}
