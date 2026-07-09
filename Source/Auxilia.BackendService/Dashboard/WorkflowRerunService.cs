using System.Text.Json;
using Auxilia.BackendService.PlatformHost;
using Auxilia.Governance;
using Auxilia.Governance.Policy;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Options;

namespace Auxilia.BackendService.Dashboard;

/// <summary>
/// Rerun of a terminal run (#20): re-dispatches the run's ORIGINAL
/// <see cref="RunWorkflowCommand"/> (kept as <see cref="WorkflowInstanceRecord.DispatchCommandJson"/>)
/// with a fresh command ID, the current principal as requester, and a
/// <c>RERUN_OF</c> context entry pointing at the predecessor instance. Policy-checked like any
/// trigger (<see cref="PermissionActions.WorkflowTrigger"/>) and audited.
/// </summary>
public sealed class WorkflowRerunService(
    IDataAccess<WorkflowInstanceRecord> instances,
    IPolicyEngine policyEngine,
    IMessageBusClient messageBus,
    AuditLog auditLog,
    IOptions<PlatformHostSettings> settings)
{
    public const string RerunContextKey = "RERUN_OF";

    private static readonly string[] TerminalStates = ["Success", "Failed", "Cancelled", "PreFlightFailed"];

    public static bool IsTerminal(WorkflowInstanceRecord run) => TerminalStates.Contains(run.State);

    /// <summary>Re-dispatches the run; returns the fresh command ID of the new dispatch.</summary>
    public async Task<Guid> RerunAsync(Guid actorPrincipalId, Guid instanceId, CancellationToken ct = default)
    {
        var run = await instances.ReadAsync(instanceId, ct)
                  ?? throw new InvalidOperationException("Unknown run.");
        if (!IsTerminal(run))
            throw new InvalidOperationException("Only finished runs can be rerun.");

        var original = ParseDispatchCommand(run.DispatchCommandJson)
                       ?? throw new InvalidOperationException(
                           "This run carries no dispatch command to re-dispatch.");

        var decision = await policyEngine.EvaluateAsync(
            new PolicyContext(actorPrincipalId, PermissionActions.WorkflowTrigger, instanceId.ToString())
                { WorkflowType = run.WorkflowType }, ct);
        if (!decision.Allowed)
            throw new InvalidOperationException($"workflow.trigger denied: {decision.Reason}");

        var command = BuildRerunCommand(original, instanceId, actorPrincipalId);
        await messageBus.PublishAsync(settings.Value.CommandQueueName, command, ct);

        await auditLog.AppendAsync(actorPrincipalId.ToString("D"), "workflow.rerun",
            instanceId.ToString(), command.CommandId.ToString(),
            JsonSerializer.Serialize(new { predecessorInstanceId = instanceId, workflowType = run.WorkflowType }),
            ct);

        return command.CommandId;
    }

    /// <summary>Fresh command ID, requester override, and the RERUN_OF back-reference; everything else is the original.</summary>
    internal static RunWorkflowCommand BuildRerunCommand(
        RunWorkflowCommand original, Guid predecessorInstanceId, Guid actorPrincipalId)
    {
        var context = new Dictionary<string, string>(original.Context, StringComparer.Ordinal)
        {
            [RerunContextKey] = predecessorInstanceId.ToString("D")
        };
        return original with
        {
            CommandId = Guid.NewGuid(),
            Context = context,
            RequestedBy = actorPrincipalId
        };
    }

    /// <summary>
    /// Runs that were dispatched as reruns of the given run, oldest first. Candidates are
    /// pre-filtered by workflow type so the dispatch-command JSON is only parsed for siblings.
    /// </summary>
    public static IReadOnlyList<WorkflowInstanceRecord> SuccessorsOf(
        WorkflowInstanceRecord run, IEnumerable<WorkflowInstanceRecord> candidates)
        => candidates
            .Where(c => c.Id != run.Id && c.WorkflowType == run.WorkflowType && RerunOf(c) == run.Id)
            .OrderBy(c => c.CreatedUtc)
            .ToList();

    /// <summary>The predecessor instance ID when the run was dispatched as a rerun, otherwise null.</summary>
    public static Guid? RerunOf(WorkflowInstanceRecord run)
        => ParseDispatchCommand(run.DispatchCommandJson)?.Context is { } context
           && context.TryGetValue(RerunContextKey, out var value)
           && Guid.TryParse(value, out var id)
            ? id
            : null;

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
