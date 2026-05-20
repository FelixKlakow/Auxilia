using Auxilia.ImplementationWorkflow.Tests.Fakes;
using Auxilia.Workflows;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.Testing;
using Auxilia.Workflows.TaskSource;

namespace Auxilia.ImplementationWorkflow.Tests.Scenarios;

public abstract class ScenarioTestBase
{
    protected string OutputDir = "";

    [SetUp]
    public void SetUp()
    {
        OutputDir = Path.Combine(Path.GetTempPath(), $"impl-scenario-{Guid.NewGuid():N}");
    }

    [TearDown]
    public void TearDown()
    {
        WorkflowBuilder.TestSlotHandlerResolver = null;
        if (Directory.Exists(OutputDir))
            Directory.Delete(OutputDir, recursive: true);
    }

    protected static WorkItem DefaultWorkItem(string id = "WI-1", string title = "Test Task") =>
        new(id, title, "Implement the requested feature.", null, null, []);

    protected static FakeAiAgent DefaultImplementerAgent(string implResponse = "Implementation complete. I committed and pushed the changes.") =>
        new(new Queue<IReadOnlyList<ScriptedTurn>>([
            [new ScriptedTurn("", implResponse)]
        ]));

    protected static FakeAiAgent DefaultReviewerAgent(string reviewResponse = "[]") =>
        new(new Queue<IReadOnlyList<ScriptedTurn>>([
            [new ScriptedTurn("", reviewResponse)]
        ]));

    protected ImplementationFakeRegistry DefaultRegistry(
        WorkItem? workItem = null,
        FakeAiAgent? implementerAgent = null,
        FakeAiAgent? reviewerAgent = null,
        FakeSourceControlWriteAccess? repository = null,
        FakeTaskSourceAccess? taskSource = null,
        FakeTestRunner? testRunner = null,
        FakePullRequestAccess? pullRequest = null,
        ImplementationWorkflowConfiguration? configuration = null)
    {
        var wi = workItem ?? DefaultWorkItem();
        var ts = taskSource ?? new FakeTaskSourceAccess();
        ts.WorkItems[wi.Id] = wi;

        return new ImplementationFakeRegistry(
            repository ?? new FakeSourceControlWriteAccess(),
            ts,
            testRunner ?? new FakeTestRunner(),
            pullRequest ?? new FakePullRequestAccess(),
            implementerAgent ?? DefaultImplementerAgent(),
            reviewerAgent ?? DefaultReviewerAgent(),
            new FakeSignalEmitter(),
            OutputDir,
            wi.Id,
            configuration ?? new ImplementationWorkflowConfiguration());
    }

    protected async Task<HarnessResult> RunScenarioAsync(ImplementationFakeRegistry registry)
    {
        var resolver = new SlotHandlerResolver();
        registry.Register(resolver);

        var harnessSlots = registry.BuildHarnessSlots();
        var builder = WorkflowTestHarness
            .For(() => ImplementationWorkflow.RunAsync())
            .WithDirective(WorkflowDirectiveKind.Run)
            .WithTimeout(TimeSpan.FromSeconds(30));

        foreach (var (name, providerType, settings) in harnessSlots)
            builder = builder.WithSlot(name, providerType, settings);

        return await builder.Build().RunAsync();
    }
}
