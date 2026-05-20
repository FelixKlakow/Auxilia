using Auxilia.ImplementationWorkflow.Context;
using Auxilia.Workflows.TaskSource;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Auxilia.ImplementationWorkflow;

public sealed class WriteBackService(
    [FromKeyedServices("task-source")] ITaskSourceAccess taskSource,
    ImplementationWorkflowConfiguration configuration,
    ILoggerFactory? loggerFactory = null)
{
    public async Task WriteAsync(
        ImplementationContext context,
        string prUrl,
        CancellationToken cancellationToken = default)
    {
        var logger = loggerFactory?.CreateLogger<WriteBackService>();

        try
        {
            await taskSource.PostCommentAsync(
                context.WorkItem.Id,
                $"Implementation complete. Pull request: {prUrl}",
                cancellationToken);
        }
        catch (Exception ex)
        {
            if (configuration.WriteBackBehavior == WriteBackBehavior.Fail)
                throw;

            logger?.LogWarning(ex, "Write-back comment failed; continuing because WriteBackBehavior is Warn.");
        }

        try
        {
            await taskSource.UpdateStatusAsync(context.WorkItem.Id, "InReview", cancellationToken);
        }
        catch (Exception ex)
        {
            if (configuration.WriteBackBehavior == WriteBackBehavior.Fail)
                throw;

            logger?.LogWarning(ex, "Status update failed; continuing because WriteBackBehavior is Warn.");
        }
    }
}
