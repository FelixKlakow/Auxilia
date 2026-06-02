using Auxilia.ImplementationWorkflow.Signals;
using Auxilia.Workflows;

namespace Auxilia.ImplementationWorkflow;

public sealed class CompletionSignalEmitter(ISignalEmitter signalEmitter)
{
    public async Task EmitAsync(
        AgentCompletionResult agentResult,
        string prUrl,
        IReadOnlyList<ReviewNote> reviewNotes,
        CancellationToken cancellationToken = default)
    {
        if (reviewNotes.Count > 0)
        {
            await signalEmitter.EmitAsync("ReviewNotesFlagged", new ReviewNotesFlaggedSignalPayload
            {
                PrUrl = prUrl,
                BranchName = agentResult.BranchName,
                WorkItemId = agentResult.WorkItemId,
                ReviewNotes = reviewNotes.Select(n => new ReviewNoteDto(n.Description, n.FilePath, n.Severity.ToString())).ToList()
            }, cancellationToken);
        }

        await signalEmitter.EmitAsync("Completed", new CompletedSignalPayload
        {
            PrUrl = prUrl,
            BranchName = agentResult.BranchName,
            WorkItemId = agentResult.WorkItemId
        }, cancellationToken);
    }
}
