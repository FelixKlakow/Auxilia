using Auxilia.Workflows.SourceControl;
using Auxilia.Workflows.TaskSource;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.ImplementationWorkflow.Context;

public sealed class ContextAssembler(
    [FromKeyedServices("task-source")] ITaskSourceAccess taskSource,
    [FromKeyedServices("repository")] ISourceControlWriteAccess repository,
    WorkItemTrigger trigger,
    InstructionsFileLocator instructionsLocator,
    BranchNamingService branchNaming)
{
    public async Task<ImplementationContext> AssembleAsync(CancellationToken cancellationToken = default)
    {
        var workItem = await taskSource.GetWorkItemAsync(trigger.WorkItemId, cancellationToken)
            ?? throw new InvalidOperationException($"Work item '{trigger.WorkItemId}' was not found.");

        var instructionsContent = await instructionsLocator.LocateAsync(cancellationToken);
        var branchName = branchNaming.Compute(workItem);

        return new ImplementationContext(
            workItem,
            instructionsContent,
            branchName,
            repository.WorkingPath);
    }
}
