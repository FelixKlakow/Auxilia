using System.Text.Json;
using Auxilia.PlatformData.Entities;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.BackendService.Dashboard;

/// <summary>What started a run, derived from its stored dispatch command (#21).</summary>
public enum TriggerOriginKind
{
    Unknown,
    Manual,
    Schedule,
    ArtifactChain,
    Mail,
    Rerun
}

/// <summary>
/// Trigger origin of a run for the run-history surfaces: a short human label plus the
/// predecessor instance for reruns so rows can link back to the original run.
/// </summary>
public sealed record TriggerOrigin(TriggerOriginKind Kind, string Label, Guid? PredecessorInstanceId = null)
{
    /// <summary>Stable prefix of work-item IDs the email task-source adapter generates.</summary>
    private const string MailWorkItemPrefix = "mail-";

    public static TriggerOrigin Of(WorkflowInstanceRecord run)
    {
        var command = ParseDispatchCommand(run.DispatchCommandJson);
        if (command is null)
            return new TriggerOrigin(TriggerOriginKind.Unknown, "—");

        var context = command.Context ?? new Dictionary<string, string>();

        if (context.TryGetValue(WorkflowRerunService.RerunContextKey, out var rerunOf)
            && Guid.TryParse(rerunOf, out var predecessorId))
            return new TriggerOrigin(TriggerOriginKind.Rerun,
                $"Rerun of {predecessorId.ToString("N")[..8]}", predecessorId);

        if (context.ContainsKey("ArtifactId") || context.ContainsKey("ArtifactType"))
            return new TriggerOrigin(TriggerOriginKind.ArtifactChain, "Artifact chain");

        var isMail = context.GetValueOrDefault("WorkItemId")?
                         .StartsWith(MailWorkItemPrefix, StringComparison.Ordinal) == true
                     || (context.ContainsKey("Title") && context.ContainsKey("From"));
        if (isMail)
        {
            var subject = context.GetValueOrDefault("Title");
            return new TriggerOrigin(TriggerOriginKind.Mail,
                string.IsNullOrWhiteSpace(subject) ? "Mail" : $"Mail · {subject}");
        }

        // Configuration-bound dispatches without any adapter context come from the scheduler;
        // a configuration-less command was published by hand (Trigger page, MCP, automation).
        return (command.WorkflowConfigurationId ?? run.WorkflowConfigurationId) is not null
            ? new TriggerOrigin(TriggerOriginKind.Schedule, "Schedule")
            : new TriggerOrigin(TriggerOriginKind.Manual, "Manual");
    }

    private static RunWorkflowCommand? ParseDispatchCommand(string? dispatchCommandJson)
    {
        if (string.IsNullOrWhiteSpace(dispatchCommandJson))
            return null;
        try
        {
            return JsonSerializer.Deserialize<RunWorkflowCommand>(dispatchCommandJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
