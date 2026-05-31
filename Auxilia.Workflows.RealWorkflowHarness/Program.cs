using Auxilia.CodeReview.Workflow;
using Auxilia.CodeReview.Workflow.Findings;
using Auxilia.ImplementationWorkflow;
using Auxilia.ImplementationWorkflow.Context;
using Auxilia.Workflows;
using Auxilia.Workflows.PullRequestAccess;
using Auxilia.Workflows.RealWorkflowHarness;
using Auxilia.Workflows.TaskSource;
using CrFake = Auxilia.CodeReview.Workflow.Tests.Fakes;
using ImplFake = Auxilia.ImplementationWorkflow.Tests.Fakes;

var workflowName= Environment.GetEnvironmentVariable("WORKFLOW_CONTEXT__WORKFLOW_NAME")
    ?? throw new InvalidOperationException(
        "WORKFLOW_CONTEXT__WORKFLOW_NAME environment variable is not set.");

var fakeMode = Environment.GetEnvironmentVariable("WORKFLOW_CONTEXT__FAKE_MODE") ?? "happy-path";

var outputDirectory = Path.Combine(Path.GetTempPath(), $"harness-{Guid.NewGuid():N}");
var resolver = new SlotHandlerResolver();

await ((workflowName, fakeMode) switch
{
    ("pull-request-code-review", "happy-path") => RunCodeReviewHappyPath(),
    ("pull-request-code-review", "write-back-failure") => RunCodeReviewWriteBackFailure(),
    ("implementation-workflow",  "happy-path")    => RunImplementationHappyPath(),
    ("implementation-workflow",  "agent-failure")  => RunImplementationAgentFailure(),
    _ => throw new InvalidOperationException(
        $"Unknown combination: workflow='{workflowName}' fake-mode='{fakeMode}'")
});

async Task RunCodeReviewHappyPath()
{
    CrFake.FakeAiAgent? primaryAi = null;
    var reviewedTurn = HarnessScriptedTurns.CreateReviewedTurn(() => primaryAi);
    primaryAi = new CrFake.FakeAiAgent(
        new Queue<IReadOnlyList<CrFake.ScriptedTurn>>([[reviewedTurn]]));

    var registry = new CrFake.CodeReviewFakeRegistry(
        sourceControl: new CrFake.FakeSourceControlAccess(),
        pullRequest: new CrFake.FakePullRequestAccess(
            changedFiles: [new ChangedFile("src/Widget.cs", ChangeKind.Modified)]),
        workItems: new CrFake.FakeWorkItemAccess(),
        primaryAi: primaryAi,
        secondaryAi: new CrFake.FakeAiAgent(new Queue<IReadOnlyList<CrFake.ScriptedTurn>>()),
        outputDirectory: outputDirectory,
        writeBack: new WriteBackConfiguration { MinimumSeverity = FindingSeverity.Info });
    registry.Register(resolver);
    await PullRequestReviewWorkflow.RunAsync();
}

async Task RunCodeReviewWriteBackFailure()
{
    CrFake.FakeAiAgent? primaryAi = null;
    var reviewedTurn = HarnessScriptedTurns.CreateReviewedTurn(() => primaryAi);
    primaryAi = new CrFake.FakeAiAgent(
        new Queue<IReadOnlyList<CrFake.ScriptedTurn>>([[reviewedTurn]]));

    var registry = new CrFake.CodeReviewFakeRegistry(
        sourceControl: new CrFake.FakeSourceControlAccess(),
        pullRequest: new CrFake.FakePullRequestAccess(
            changedFiles: [new ChangedFile("src/Widget.cs", ChangeKind.Modified)],
            throwOnPost: new Exception("Simulated write-back failure")),
        workItems: new CrFake.FakeWorkItemAccess(),
        primaryAi: primaryAi,
        secondaryAi: new CrFake.FakeAiAgent(new Queue<IReadOnlyList<CrFake.ScriptedTurn>>()),
        outputDirectory: outputDirectory,
        writeBack: new WriteBackConfiguration { MinimumSeverity = FindingSeverity.Info });
    registry.Register(resolver);
    await PullRequestReviewWorkflow.RunAsync();
}

async Task RunImplementationHappyPath()
{
    var taskSource = new ImplFake.FakeTaskSourceAccess();
    taskSource.WorkItems["WI-1"] = new WorkItem("WI-1", "Implement feature", null, null, null, []);

    var registry = new ImplFake.ImplementationFakeRegistry(
        repository: new ImplFake.FakeSourceControlWriteAccess(),
        taskSource: taskSource,
        testRunner: new ImplFake.FakeTestRunner(),
        pullRequest: new ImplFake.FakePullRequestAccess(),
        implementerAgent: new ImplFake.FakeAiAgent(
            new Queue<IReadOnlyList<ImplFake.ScriptedTurn>>(
                [[new ImplFake.ScriptedTurn("", "Implementation complete.")]])),
        reviewerAgent: new ImplFake.FakeAiAgent(
            new Queue<IReadOnlyList<ImplFake.ScriptedTurn>>()),
        signalEmitter: new ImplFake.FakeSignalEmitter(),
        outputDirectory: outputDirectory,
        workItemId: "WI-1",
        configuration: new ImplementationWorkflowConfiguration { OutputDirectory = Path.GetTempPath() });
    registry.Register(resolver);
    await ImplementationWorkflow.RunAsync();
}

async Task RunImplementationAgentFailure()
{
    var taskSource = new ImplFake.FakeTaskSourceAccess();
    taskSource.WorkItems["WI-1"] = new WorkItem("WI-1", "Implement feature", null, null, null, []);

    var registry = new ImplFake.ImplementationFakeRegistry(
        repository: new ImplFake.FakeSourceControlWriteAccess(),
        taskSource: taskSource,
        testRunner: new ImplFake.FakeTestRunner(),
        pullRequest: new ImplFake.FakePullRequestAccess(),
        implementerAgent: new ImplFake.FakeAiAgent(
            new Queue<IReadOnlyList<ImplFake.ScriptedTurn>>(),
            initialFailCount: 1),
        reviewerAgent: new ImplFake.FakeAiAgent(
            new Queue<IReadOnlyList<ImplFake.ScriptedTurn>>()),
        signalEmitter: new ImplFake.FakeSignalEmitter(),
        outputDirectory: outputDirectory,
        workItemId: "WI-1",
        configuration: new ImplementationWorkflowConfiguration { OutputDirectory = Path.GetTempPath() });
    registry.Register(resolver);
    await ImplementationWorkflow.RunAsync();
}
