using Auxilia.Workflows;

namespace Auxilia.ImplementationWorkflow.Tests.Fakes;

public sealed class ImplementationFakeRegistry
{
    private readonly FakeSourceControlWriteAccess _repository;
    private readonly FakeTaskSourceAccess _taskSource;
    private readonly FakeTestRunner _testRunner;
    private readonly FakePullRequestAccess _pullRequest;
    private readonly FakeAiAgent _implementerAgent;
    private readonly FakeAiAgent _reviewerAgent;
    private readonly FakeSignalEmitter _signalEmitter;
    private readonly FakeWorkflowBootstrapSlotHandler _bootstrapHandler;

    public ImplementationFakeRegistry(
        FakeSourceControlWriteAccess repository,
        FakeTaskSourceAccess taskSource,
        FakeTestRunner testRunner,
        FakePullRequestAccess pullRequest,
        FakeAiAgent implementerAgent,
        FakeAiAgent reviewerAgent,
        FakeSignalEmitter signalEmitter,
        string outputDirectory,
        string workItemId,
        ImplementationWorkflowConfiguration configuration)
    {
        _repository = repository;
        _taskSource = taskSource;
        _testRunner = testRunner;
        _pullRequest = pullRequest;
        _implementerAgent = implementerAgent;
        _reviewerAgent = reviewerAgent;
        _signalEmitter = signalEmitter;

        _bootstrapHandler = new FakeWorkflowBootstrapSlotHandler(
            outputDirectory, signalEmitter, workItemId, configuration);
    }

    public FakeSignalEmitter SignalEmitter => _signalEmitter;
    public FakeAiAgent ImplementerAgent => _implementerAgent;
    public FakeAiAgent ReviewerAgent => _reviewerAgent;

    public void Register(SlotHandlerResolver resolver)
    {
        resolver.Register("fake-repository",           new FakeSourceControlWriteAccessSlotHandler(_repository));
        resolver.Register("fake-task-source",          new FakeTaskSourceAccessSlotHandler(_taskSource));
        resolver.Register("fake-implementation-agent", new FakeAiAgentSlotHandler(_implementerAgent));
        resolver.Register("fake-reviewer-agent",       new FakeAiAgentSlotHandler(_reviewerAgent));
        resolver.Register("fake-test-runner",          new FakeTestRunnerSlotHandler(_testRunner));
        resolver.Register("fake-pull-request",         new FakePullRequestAccessSlotHandler(_pullRequest));
        resolver.Register("fake-workflow-config",      _bootstrapHandler);
        WorkflowBuilder.TestSlotHandlerResolver = resolver;
    }

    public IReadOnlyList<(string Name, string ProviderType, Dictionary<string, string> Settings)> BuildHarnessSlots()
        =>
        [
            ("repository",           "fake-repository",           new Dictionary<string, string>()),
            ("task-source",          "fake-task-source",          new Dictionary<string, string>()),
            ("implementation-agent", "fake-implementation-agent", new Dictionary<string, string>()),
            ("reviewer-agent",       "fake-reviewer-agent",       new Dictionary<string, string>()),
            ("test-runner",          "fake-test-runner",          new Dictionary<string, string>()),
            ("pull-request",         "fake-pull-request",         new Dictionary<string, string>()),
            ("workflow-config",      "fake-workflow-config",      new Dictionary<string, string>()),
        ];
}
