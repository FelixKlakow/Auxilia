using Auxilia.Workflows.PullRequestAccess;
using Auxilia.Workflows.SourceControl;
using Auxilia.Workflows.TaskSource;
using Microsoft.Extensions.Logging;
using PrChangeKind = Auxilia.Workflows.PullRequestAccess.ChangeKind;

namespace Auxilia.CodeReview.Workflow.Context;

public sealed class ContextAssembler(
    ISourceControlAccess sourceControl,
    IPullRequestAccess pullRequestAccess,
    IWorkItemAccess workItemAccess,
    CriticalityClassifier classifier,
    WorkItemRetrievalFailureBehavior failureBehavior,
    ILogger<ContextAssembler>? logger = null)
{
    public async Task<ReviewContext> AssembleAsync(CancellationToken cancellationToken = default)
    {
        var workingPath = sourceControl.WorkingPath;
        var changedFiles = await pullRequestAccess.GetChangedFilesAsync(cancellationToken);

        var reviewableFiles = new List<ReviewableFile>();
        foreach (var file in changedFiles)
        {
            var hunks = await pullRequestAccess.GetDiffHunksAsync(file.RelativePath, cancellationToken);
            var kind = file.Kind == PrChangeKind.Renamed ? PrChangeKind.Modified : file.Kind;
            reviewableFiles.Add(new ReviewableFile
            {
                FilePath = file.RelativePath,
                ChangeKind = kind,
                Criticality = classifier.Classify(file.RelativePath),
                Hunks = hunks
            });
        }

        var linkedWorkItemRefs = await pullRequestAccess.GetLinkedWorkItemsAsync(cancellationToken);
        IReadOnlyList<WorkItemSummary> linkedWorkItems;
        try
        {
            var ids = linkedWorkItemRefs.Select(r => r.Id);
            var workItems = await workItemAccess.GetWorkItemsAsync(ids, cancellationToken);
            linkedWorkItems = workItems
                .Select(wi => new WorkItemSummary(wi.Id, wi.Title, wi.Description))
                .ToList()
                .AsReadOnly();
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to retrieve linked work items for PR '{PrIdentifier}'; continuing without them.", pullRequestAccess.PrIdentifier);
            if (failureBehavior == WorkItemRetrievalFailureBehavior.Fail)
                throw;
            linkedWorkItems = Array.Empty<WorkItemSummary>();
        }

        return new ReviewContext
        {
            PullRequest = new PullRequestReference(pullRequestAccess.PrIdentifier, pullRequestAccess.BaseRef, pullRequestAccess.HeadRef),
            RepositoryWorkingPath = workingPath,
            Files = reviewableFiles.AsReadOnly(),
            LinkedWorkItems = linkedWorkItems
        };
    }
}
