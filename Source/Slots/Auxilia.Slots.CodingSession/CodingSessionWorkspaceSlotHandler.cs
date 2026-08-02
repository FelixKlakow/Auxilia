using Auxilia.Workflows;
using Auxilia.Workflows.SourceControl;
using Auxilia.Workflows.TaskSource;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Slots.CodingSession;

/// <summary>
/// Backs a live coding session's slots with the mounted workspace. The "repository" slot
/// exposes the working directory the CLI operates in (the session's GitWorkspace creates and
/// git-inits it); the optional "work-items" slot is an inert stand-in for dashboard-triggered
/// sessions that carry no originating work item. Depends only on assemblies baked into the
/// coding-session image, so its types resolve when the plugin loader scans the shipped DLL.
/// </summary>
public sealed class CodingSessionWorkspaceSlotHandler : ISlotHandler
{
    public void Register(IServiceCollection services, string slotName, Type serviceType, SlotConfiguration configuration)
    {
        switch (slotName)
        {
            case "repository":
                var workingPath = configuration.Settings.GetValueOrDefault("WorkingPath", "/workspace");
                services.AddScoped<ISourceControlAccess>(_ => new WorkspaceSourceControlAccess(workingPath));
                break;

            case "work-items":
                services.AddScoped<IWorkItemAccess>(_ => new NoWorkItemAccess());
                break;

            default:
                throw new InvalidOperationException($"Unknown slot: {slotName}");
        }
    }

    /// <summary>Exposes the workspace path; file reads are unused by the session and return empty.</summary>
    private sealed class WorkspaceSourceControlAccess(string workingPath) : ISourceControlAccess
    {
        public string WorkingPath { get; } = workingPath;

        public Task<IReadOnlyList<string>> ListFilesAsync(string? relativePath = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string> ReadFileContentAsync(string relativePath, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);

        public Task<IReadOnlyList<ChangedFile>> GetChangedFilesAsync(string baseRef, string headRef, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ChangedFile>>([]);
    }

    /// <summary>No originating work item — attachments default to none via the interface.</summary>
    private sealed class NoWorkItemAccess : IWorkItemAccess
    {
        public Task<WorkItem?> GetWorkItemAsync(string id, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkItem?>(null);

        public Task<IReadOnlyList<WorkItem>> GetWorkItemsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkItem>>([]);

        public Task PostCommentAsync(string id, string comment, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
