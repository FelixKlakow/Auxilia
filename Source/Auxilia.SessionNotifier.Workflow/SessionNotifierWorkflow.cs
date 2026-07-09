using System.Text.Json;
using Auxilia.Workflows;
using Auxilia.Workflows.TaskSource;
using Auxilia.Workflows.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.SessionNotifier.Workflow;

/// <summary>
/// Chained consumer of session results: formats a finished <c>coding-session-result</c>
/// (branch, changed-file names) or a Claude Code <c>session-report</c> (summary, turns,
/// duration) and posts it as a comment on the originating work item — for mail-triggered
/// sessions that is the reply to the sender.
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
            .ConsumesArtifact("session-report")
            .DeclaresTrigger(TriggerDeclaration.Artifact,
                "Runs after every coding-session-result or session-report artifact; chain it in the flow view.")
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

/// <summary>
/// The Claude Code <c>session-report</c> JSON contract this notifier consumes — mirrored
/// here (like <see cref="SessionSummary"/>) so the artifact stays the only coupling.
/// </summary>
public sealed record AgentSessionReport(
    string Instruction,
    bool Success,
    string? Summary,
    int TurnCount,
    decimal? TotalCostUsd,
    long? DurationMs,
    string? ErrorMessage);

public sealed class NotifierApplication(
    IWorkItemAccess workItems,
    IViewPublisher? views,
    NotifierRunContext context)
{
    public const string ProgressViewName = "progress";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (context.WorkItemId is not { Length: > 0 } workItemId)
        {
            // Manually started sessions have no originating mail — nothing to reply to is a
            // clean outcome, not a failure.
            await PublishAsync("notify",
                "The session had no originating work item — no summary mail to send.",
                cancellationToken);
            return;
        }

        if (context.ConsumedArtifactPath is not { Length: > 0 } artifactPath
            || !File.Exists(artifactPath))
            throw new InvalidOperationException(
                "No consumed artifact was materialized — this workflow must be chained onto a "
                + "coding-session-result or session-report output.");

        var json = await File.ReadAllTextAsync(artifactPath, cancellationToken);
        var (body, what) = FormatArtifact(json);
        await workItems.PostCommentAsync(workItemId, body, cancellationToken);
        await PublishAsync("notify", $"{what} posted to work item {workItemId}.", cancellationToken);
    }

    /// <summary>The two consumed artifact types share no schema; the shape decides the format.</summary>
    internal static (string Body, string Description) FormatArtifact(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty(nameof(SessionSummary.ChangedFiles), out _))
        {
            var summary = JsonSerializer.Deserialize<SessionSummary>(json)
                          ?? throw new InvalidOperationException("The consumed artifact is not a session summary.");
            return (FormatSummary(summary), $"Summary of branch '{summary.Branch}'");
        }

        var report = JsonSerializer.Deserialize<AgentSessionReport>(json)
                     ?? throw new InvalidOperationException("The consumed artifact is not a session report.");
        return (FormatReport(report), "Agent session report");
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

    /// <summary>Plain-text mail body of a Claude Code run: instruction, outcome, and effort figures.</summary>
    internal static string FormatReport(AgentSessionReport report)
    {
        var outcome = report.Success
            ? report.Summary is { Length: > 0 } summary ? summary : "The agent finished without a summary."
            : $"The run failed: {report.ErrorMessage ?? "(no error message)"}";
        var figures = new List<string> { $"Turns: {report.TurnCount}" };
        if (report.DurationMs is { } ms)
            figures.Add($"Duration: {TimeSpan.FromMilliseconds(ms):hh\\:mm\\:ss}");
        if (report.TotalCostUsd is { } cost)
            figures.Add($"Cost: ${cost.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}");
        return
            $"Your Claude Code run {(report.Success ? "finished" : "failed")}.\n\n" +
            $"Instruction: {report.Instruction}\n\n" +
            $"{outcome}\n\n" +
            string.Join(" · ", figures);
    }

    private Task PublishAsync(string phase, string message, CancellationToken ct)
        => views?.PublishAsync(ProgressViewName, new NotifierProgressEntry(phase, message), ct)
           ?? Task.CompletedTask;
}
