using Auxilia.Workflows.SourceControl;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.ImplementationWorkflow.Branch;

public sealed class BranchSetupService(
    [FromKeyedServices("repository")] ISourceControlWriteAccess repository)
{
    public Task SetupAsync(string branchName, CancellationToken cancellationToken = default)
        => repository.CreateBranchAsync(branchName, cancellationToken: cancellationToken);
}
