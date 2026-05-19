using Auxilia.CodeReview.Workflow.Tests.Fakes;
using Auxilia.Workflows;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.PullRequestAccess;
using Auxilia.Workflows.Testing;

namespace Auxilia.CodeReview.Workflow.Tests.Scenarios;

public abstract class ScenarioTestBase
{
    protected string OutputDir = "";

    private const string ReviewedJson =
        """{"verdict":"Reviewed","findings":[{"lineStart":1,"lineEnd":2,"severity":"Medium","category":"Style","message":"Review finding","suggestion":"Fix it"}]}""";

    [SetUp]
    public void SetUp()
    {
        OutputDir = Path.Combine(Path.GetTempPath(), $"cr-scenario-{Guid.NewGuid():N}");
    }

    [TearDown]
    public void TearDown()
    {
        WorkflowBuilder.TestSlotHandlerResolver = null;
        if (Directory.Exists(OutputDir))
            Directory.Delete(OutputDir, recursive: true);
    }

    protected static ScriptedTurn ReviewedTurn(string? substringOverride = null) =>
        new(substringOverride ?? "Review the following file changes", ReviewedJson);

    protected static ScriptedTurn SkippedTurn() =>
        new("Review the following file changes",
            """{"verdict":"Skipped","findings":[]}""");

    protected static ScriptedTurn ApprovedTurn() =>
        new("Review this finding", """{"verdict":"Approved"}""");

    protected static ScriptedTurn RejectedTurn() =>
        new("Review this finding", """{"verdict":"Rejected"}""");

    protected static ScriptedTurn CompactionSummaryTurn() =>
        new("Summarize all findings", "compact summary");

    protected static ScriptedTurn ContextInjectionTurn() =>
        new("Context from prior reviews", "ok");

    protected static ChangedFile File(string path, ChangeKind kind = ChangeKind.Modified) =>
        new(path, kind);

    protected static DiffHunk Hunk(string path, string content = "diff content") =>
        new(path, 1, 1, 1, 1, content);

    protected async Task<HarnessResult> RunScenarioAsync(CodeReviewFakeRegistry registry)
    {
        var resolver = new SlotHandlerResolver();
        registry.Register(resolver);

        var harnessSlots = registry.BuildHarnessSlots();
        var builder = WorkflowTestHarness
            .For(() => PullRequestReviewWorkflow.RunAsync())
            .WithDirective(WorkflowDirectiveKind.Run)
            .WithTimeout(TimeSpan.FromSeconds(20));

        foreach (var (name, providerType, settings) in harnessSlots)
            builder = builder.WithSlot(name, providerType, settings);

        return await builder.Build().RunAsync();
    }
}
