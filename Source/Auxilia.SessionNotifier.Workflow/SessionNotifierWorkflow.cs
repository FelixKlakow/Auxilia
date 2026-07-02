using System.Text.Json;
using Auxilia.Workflows;
using Auxilia.Workflows.TaskSource;
using Auxilia.Workflows.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.SessionNotifier.Workflow;

/// <summary>
/// Chained consumer of <c>coding-session-result</c> artifacts: formats the finished live
/// session (branch, changed-file names) and posts it as a comment on the originating work
/// item — for mail-triggered sessions that is the reply to the sender.
/// </summary>
public static class SessionNotifierWorkflow
{
    public const string WorkflowType = "session-notifier";

    public static Task Main(string[] args) =>
        WorkflowBuilder.Create(WorkflowType)
            .RequiresWorkItems("work-items",
                new TaskSourceCapabilities { SupportedItemTypes = [ItemType.UserStory] },
                "Where the session summary is posted — the mail sender for mail-triggered sessions")
            .ConsumesArtifact("coding-session-result")
            .DeclaresTrigger(TriggerDeclaration.Artifact,
                "Runs after every coding-session-result artifact; chain it in the flow view.")
            .DeclaresView<NotifierProgressEntry>(NotifierApplication.ProgressViewName,
                ViewRendering.Log, ViewLifecycle.LiveAndPersisted)
            .WithApplication(ExecuteAsync)
            .Run(args);

    public static Task RunAsync() => Main(["--test-harness"]);

    private static Task ExecuteAsync(IServiceProvider provider, CancellationToken cancellationToken)
        => new NotifierApplication(
                provider.GetRequiredService<IWorkItemAccess>(),
                provider.GetService<IViewPublisher>(),
                NotifierRunContext.FromEnvironment())
            .RunAsync(cancellationToken);
}

/// <summary>One progress line of the notifier's log view.</summary>
public sealed record NotifierProgressEntry(string Phase, string Message);

/// <summary>What the chained run receives: the materialized artifact and the work item to reply to.</summary>
public sealed record NotifierRunContext(string? ConsumedArtifactPath, string? WorkItemId)
{
    public static NotifierRunContext FromEnvironment()
        => new(
            Environment.GetEnvironmentVariable("WORKFLOW_CONSUMED_ARTIFACT"),
            Environment.GetEnvironmentVariable("WORKFLOW_CONTEXT__WORKITEMID"));
}

/// <summary>The session-result JSON contract this notifier consumes (names only, no contents).</summary>
public sealed record SessionSummary(
    string Branch, IReadOnlyList<string> ChangedFiles, bool TimedOut);

public sealed class NotifierApplication(
    IWorkItemAccess workItems,
    IViewPublisher? views,
    NotifierRunContext context)
{
    public const string ProgressViewName = "progress";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (context.WorkItemId is not { Length: > 0 } workItemId)
            throw new InvalidOperationException(
                "The dispatch context carries no WorkItemId — nothing to reply to.");
        if (context.ConsumedArtifactPath is not { Length: > 0 } artifactPath
            || !File.Exists(artifactPath))
            throw new InvalidOperationException(
                "No consumed artifact was materialized — this workflow must be chained onto a coding-session-result output.");

        var summary = JsonSerializer.Deserialize<SessionSummary>(
                          await File.ReadAllTextAsync(artifactPath, cancellationToken))
                      ?? throw new InvalidOperationException("The consumed artifact is not a session summary.");

        await workItems.PostCommentAsync(workItemId, FormatSummary(summary), cancellationToken);
        await PublishAsync("notify",
            $"Summary of branch '{summary.Branch}' posted to work item {workItemId}.", cancellationToken);
    }

    /// <summary>Plain-text mail body: branch, file names, and the timeout note — never file contents.</summary>
    internal static string FormatSummary(SessionSummary summary)
    {
        var files = summary.ChangedFiles.Count == 0
            ? "  (no files changed)"
            : string.Join("\n", summary.ChangedFiles.Select(f => $"  - {f}"));
        var timeoutNote = summary.TimedOut
            ? "\n\nNote: the session hit its maximum duration and was ended by the platform."
            : "";
        return
            $"Your coding session finished.\n\n" +
            $"Branch: {summary.Branch}\n" +
            $"Changed files ({summary.ChangedFiles.Count}):\n{files}{timeoutNote}";
    }

    private Task PublishAsync(string phase, string message, CancellationToken ct)
        => views?.PublishAsync(ProgressViewName, new NotifierProgressEntry(phase, message), ct)
           ?? Task.CompletedTask;
}
