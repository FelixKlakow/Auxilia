using Auxilia.Core.Contracts;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Services;

/// <summary>A pipeline handler's verdict on a pending workflow-type registration.</summary>
public sealed record ApprovalHandlerResult(string Decision, string? Reason = null)
{
    /// <summary>No verdict — leave the registration pending (e.g. a notifier, or an inconclusive check).</summary>
    public const string Defer = "Defer";
    public const string Approve = "Approve";
    public const string Deny = "Deny";

    public static readonly ApprovalHandlerResult Deferred = new(Defer);
}

/// <summary>
/// One pluggable step of the signing authority's approval pipeline. Handlers are a DI-bound,
/// name-keyed catalog (no compiled enum): host configuration (<c>CoreApi:ApprovalHandlers</c>)
/// selects which run, in order, when a registration enters Pending. A handler may notify
/// (email → Defer), auto-decide (an AI safety-check workflow → Approve/Deny), or anything else —
/// new handlers plug in by registering another implementation.
/// </summary>
public interface IWorkflowTypeApprovalHandler
{
    /// <summary>Catalog name referenced from <c>CoreApi:ApprovalHandlers</c>.</summary>
    string Name { get; }

    Task<ApprovalHandlerResult> EvaluateAsync(WorkflowTypeRegistrationDto registration, CancellationToken ct);
}

/// <summary>
/// Runs the configured approval handlers over a pending registration. The first Approve or Deny
/// verdict is applied through the registry (as the signing authority, actor
/// <c>approval-pipeline:{handler}</c>); if every handler defers, the registration stays Pending
/// for a human operator. Kicked off in the background after a Pending registration — the
/// register call itself never waits on the pipeline (async approval by design).
/// </summary>
public sealed class WorkflowTypeApprovalPipeline(
    WorkflowTypeRegistryService registry,
    IEnumerable<IWorkflowTypeApprovalHandler> handlers,
    IOptions<CoreApiSettings> settings,
    ILogger<WorkflowTypeApprovalPipeline> logger)
{
    /// <summary>Fire-and-forget entry: process the pending registration without blocking the caller.</summary>
    public void KickOff(string workflowType)
        => _ = Task.Run(async () =>
        {
            try
            {
                await ProcessPendingAsync(workflowType, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Approval pipeline failed for workflow type {WorkflowType}.", workflowType);
            }
        });

    /// <summary>Runs the configured handlers; exposed directly so tests (and re-runs) are deterministic.</summary>
    public async Task ProcessPendingAsync(string workflowType, CancellationToken ct)
    {
        var registration = await registry.GetRegistrationAsync(workflowType, ct);
        if (registration is null || registration.Status != WorkflowTypeStatus.Pending)
            return;

        var catalog = handlers.ToDictionary(h => h.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var name in settings.Value.ApprovalHandlers)
        {
            if (!catalog.TryGetValue(name, out var handler))
            {
                logger.LogWarning("Approval handler '{Handler}' is configured but not registered — skipped.", name);
                continue;
            }

            var result = await handler.EvaluateAsync(registration, ct);
            logger.LogInformation(
                "Approval handler '{Handler}' on {WorkflowType}: {Decision}{Reason}",
                name, workflowType, result.Decision,
                result.Reason is { Length: > 0 } r ? $" ({r})" : "");

            switch (result.Decision)
            {
                case ApprovalHandlerResult.Approve:
                    await registry.ApproveAsync(workflowType, $"approval-pipeline:{name}", ct);
                    return;
                case ApprovalHandlerResult.Deny:
                    await registry.DenyAsync(
                        workflowType, result.Reason ?? $"denied by approval handler '{name}'",
                        $"approval-pipeline:{name}", ct);
                    return;
            }
        }
        // Every handler deferred — the registration awaits a human signing decision.
    }
}
