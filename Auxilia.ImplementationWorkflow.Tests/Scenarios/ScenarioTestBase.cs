using Auxilia.ImplementationWorkflow;
using Auxilia.ImplementationWorkflow.Mcp;
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

    protected static FakeAiAgent DefaultReviewerAgent(IReadOnlyList<ReviewNote>? notes = null)
    {
        FakeAiAgent? agent = null;
        Func<Task>? toolCall = null;
        if (notes is { Count: > 0 })
        {
            toolCall = () =>
            {
                var sink = agent?.LastSessionOptions?.CapabilityTools?
                    .OfType<ImplementationReviewResultSinkMcpTools>().FirstOrDefault();
                if (sink is null) return Task.CompletedTask;
                foreach (var note in notes)
                    sink.RecordReviewNote(note.Description, note.FilePath, note.Severity);
                return Task.CompletedTask;
            };
        }
        agent = new FakeAiAgent(new Queue<IReadOnlyList<ScriptedTurn>>([[new ScriptedTurn("", "", toolCall)]]));
        return agent;
    }

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
