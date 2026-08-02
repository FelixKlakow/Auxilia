using Auxilia.CodeReview.Workflow.Findings;
using Auxilia.CodeReview.Workflow.Mcp;
using Auxilia.CodeReview.Workflow.Tests.Fakes;
using Auxilia.CodeReview.Workflow.Verdicts;
using Auxilia.Workflows;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.PullRequestAccess;
using Auxilia.Workflows.Testing;

namespace Auxilia.CodeReview.Workflow.Tests.Scenarios;

public abstract class ScenarioTestBase
{
    protected string OutputDir = "";

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

    protected static ScriptedTurn ReviewedTurn(Func<FakeAiAgent?>? agentGetter = null, string? substringOverride = null)
    {
        Func<Task>? toolCall = agentGetter is null ? null : () =>
        {
            var sink = agentGetter()?.LastSessionOptions?.CapabilityTools?
                .OfType<CodeReviewResultSinkMcpTools>().FirstOrDefault();
            if (sink is null) return Task.CompletedTask;
            sink.RecordFinding("review-finding.cs", 1, 2, FindingSeverity.Medium, "Style", "Review finding", "Fix it");
            sink.RecordFileVerdict(FileVerdict.Reviewed);
            return Task.CompletedTask;
        };
        return new(substringOverride ?? "Review the following file changes", "", toolCall);
    }

    protected static ScriptedTurn SkippedTurn(Func<FakeAiAgent?>? agentGetter = null)
    {
        Func<Task>? toolCall = agentGetter is null ? null : () =>
        {
            var sink = agentGetter()?.LastSessionOptions?.CapabilityTools?
                .OfType<CodeReviewResultSinkMcpTools>().FirstOrDefault();
            sink?.RecordFileVerdict(FileVerdict.Skipped);
            return Task.CompletedTask;
        };
        return new("Review the following file changes", "", toolCall);
    }

    protected static ScriptedTurn ApprovedTurn(Func<FakeAiAgent?>? agentGetter = null)
    {
        Func<Task>? toolCall = agentGetter is null ? null : () =>
        {
            var sink = agentGetter()?.LastSessionOptions?.CapabilityTools?
                .OfType<CodeReviewResultSinkMcpTools>().FirstOrDefault();
            sink?.RecordSecondaryVerdict(SecondaryVerdict.Approved);
            return Task.CompletedTask;
        };
        return new("Review this finding", "", toolCall);
    }

    protected static ScriptedTurn RejectedTurn(Func<FakeAiAgent?>? agentGetter = null)
    {
        Func<Task>? toolCall = agentGetter is null ? null : () =>
        {
            var sink = agentGetter()?.LastSessionOptions?.CapabilityTools?
                .OfType<CodeReviewResultSinkMcpTools>().FirstOrDefault();
            sink?.RecordSecondaryVerdict(SecondaryVerdict.Rejected);
            return Task.CompletedTask;
        };
        return new("Review this finding", "", toolCall);
    }

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
