using Auxilia.CodeReview.Workflow.Compaction;
using Auxilia.CodeReview.Workflow.Context;
using Auxilia.Workflows;

namespace Auxilia.CodeReview.Workflow.Tests.Fakes;

public sealed class CodeReviewFakeRegistry
{
    private readonly FakeSourceControlAccess _sourceControl;
    private readonly FakePullRequestAccess _pullRequest;
    private readonly FakeWorkItemAccess _workItems;
    private readonly FakeAiAgent _primaryAi;
    private readonly FakeAiAgent _secondaryAi;
    private readonly string _outputDirectory;
    private readonly TwoEyesConfiguration? _twoEyes;
    private readonly WriteBackConfiguration? _writeBack;
    private readonly WorkItemRetrievalFailureBehavior _failureBehavior;
    private readonly IReadOnlyList<string>? _criticalPatterns;
    private readonly ContextCompactionOptions? _compactionOptions;

    public CodeReviewFakeRegistry(
        FakeSourceControlAccess sourceControl,
        FakePullRequestAccess pullRequest,
        FakeWorkItemAccess workItems,
        FakeAiAgent primaryAi,
        FakeAiAgent secondaryAi,
        string outputDirectory,
        TwoEyesConfiguration? twoEyes = null,
        WriteBackConfiguration? writeBack = null,
        WorkItemRetrievalFailureBehavior failureBehavior = WorkItemRetrievalFailureBehavior.Ignore,
        IReadOnlyList<string>? criticalPatterns = null,
        ContextCompactionOptions? compactionOptions = null)
    {
        _sourceControl = sourceControl;
        _pullRequest = pullRequest;
        _workItems = workItems;
        _primaryAi = primaryAi;
        _secondaryAi = secondaryAi;
        _outputDirectory = outputDirectory;
        _twoEyes = twoEyes;
        _writeBack = writeBack;
        _failureBehavior = failureBehavior;
        _criticalPatterns = criticalPatterns;
        _compactionOptions = compactionOptions;
    }

    public void Register(SlotHandlerResolver resolver)
    {
        resolver.Register("fake-source-control", new FakeSourceControlAccessSlotHandler(_sourceControl));
        resolver.Register("fake-pull-request", new FakePullRequestAccessSlotHandler(_pullRequest));
        resolver.Register("fake-work-item", new FakeWorkItemAccessSlotHandler(_workItems));
        resolver.Register("fake-primary-ai", new FakeAiAgentSlotHandler(_primaryAi));
        resolver.Register("fake-secondary-ai", new FakeAiAgentSlotHandler(_secondaryAi));
        resolver.Register("fake-workflow-bootstrap", new FakeWorkflowBootstrapSlotHandler(
            _outputDirectory, _twoEyes, _writeBack, _failureBehavior, _criticalPatterns, _compactionOptions));
        WorkflowBuilder.TestSlotHandlerResolver = resolver;
    }

    public IReadOnlyList<(string Name, string ProviderType, Dictionary<string, string> Settings)> BuildHarnessSlots()
        =>
        [
            ("repository",         "fake-source-control",     new Dictionary<string, string>()),
            ("pull-request",       "fake-pull-request",       new Dictionary<string, string>()),
            ("work-items",         "fake-work-item",          new Dictionary<string, string>()),
            ("primary-reviewer",   "fake-primary-ai",         new Dictionary<string, string>()),
            ("secondary-reviewer", "fake-secondary-ai",       new Dictionary<string, string>()),
            ("workflow-bootstrap", "fake-workflow-bootstrap", new Dictionary<string, string>()),
        ];
}
