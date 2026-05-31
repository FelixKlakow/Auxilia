using Auxilia.CodeReview.Workflow.Findings;
using Auxilia.CodeReview.Workflow.Mcp;
using Auxilia.CodeReview.Workflow.Verdicts;
using CrFake = Auxilia.CodeReview.Workflow.Tests.Fakes;

namespace Auxilia.Workflows.RealWorkflowHarness;

internal static class HarnessScriptedTurns
{
    internal static CrFake.ScriptedTurn CreateReviewedTurn(Func<CrFake.FakeAiAgent?> agentGetter) =>
        new(
            ExpectedPromptSubstring: "Review the following file changes",
            Response: "",
            ToolCall: () =>
            {
                var sink = agentGetter()?.LastSessionOptions?.CapabilityTools
                    ?.OfType<CodeReviewResultSinkMcpTools>().FirstOrDefault();
                if (sink is null) return Task.CompletedTask;
                sink.RecordFinding("src/Widget.cs", 1, 2, FindingSeverity.Medium,
                    "Style", "Review finding", "Fix it");
                sink.RecordFileVerdict(FileVerdict.Reviewed);
                return Task.CompletedTask;
            });
}
