using Auxilia.Workflows.SourceControl;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.ImplementationWorkflow.Context;

public sealed class InstructionsFileLocator(
    [FromKeyedServices("repository")] ISourceControlWriteAccess repository)
{
    public async Task<string> LocateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await repository.ReadFileContentAsync("copilot-instructions.md", cancellationToken);
        }
        catch
        {
            // fall through
        }

        try
        {
            return await repository.ReadFileContentAsync("Claude.md", cancellationToken);
        }
        catch
        {
            // neither file found
        }

        return string.Empty;
    }
}
