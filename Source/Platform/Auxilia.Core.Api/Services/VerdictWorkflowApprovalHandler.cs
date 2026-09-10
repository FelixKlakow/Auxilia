using System.Text.Json;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Contracts;
using Auxilia.PlatformData.Artifacts;
using Auxilia.UniversalDataAccess;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Narrow dispatch seam of the verdict handler — the concrete <see cref="RunService"/> has a
/// wide dependency surface tests should not have to satisfy.
/// </summary>
public interface IVerdictRunDispatcher
{
    Task<RunAccepted> DispatchAsync(RunRequest request, CancellationToken ct);
}

/// <summary>Production dispatcher: the verdict run goes through the ordinary Run API path.</summary>
public sealed class RunServiceVerdictRunDispatcher(RunService runs) : IVerdictRunDispatcher
{
    public Task<RunAccepted> DispatchAsync(RunRequest request, CancellationToken ct)
        => runs.RunInlineAsync(request, triggeredBy: null, ct);
}

/// <summary>
/// Approval-pipeline handler "verdict-workflow": dispatches a CONFIGURED (Active) workflow type
/// over a pending registration — context keys <c>approval-workflow-type</c>,
/// <c>approval-package-uri</c>, <c>approval-publisher-key</c>, <c>approval-registered-by</c>,
/// plus <c>approval-package-download-url</c> for a Core-stored (<c>core://</c>) package —
/// waits for the run, and applies the verdict artifact it publishes
/// (<c>{"decision":"approve"|"deny","reason":"…"}</c>). The Core never interprets the check
/// itself (an AI safety review is just what the configured workflow happens to do). Anything
/// inconclusive — handler unconfigured, run failed, timed out, no/unparseable verdict — DEFERS
/// to the human signing authority; a denial requires an explicit verdict.
/// </summary>
public sealed class VerdictWorkflowApprovalHandler(
    IVerdictRunDispatcher dispatcher,
    IDataAccess<CoreRunRecord> runs,
    IDataAccess<CoreArtifactRecord> artifacts,
    IArtifactPayloadReader payloads,
    PendingPackageDownloadTokenService downloadTokens,
    IOptions<CoreApiSettings> settings,
    TimeProvider clock,
    ILogger<VerdictWorkflowApprovalHandler> logger) : IWorkflowTypeApprovalHandler
{
    public string Name => "verdict-workflow";

    public async Task<ApprovalHandlerResult> EvaluateAsync(
        WorkflowTypeRegistrationDto registration, CancellationToken ct)
    {
        var options = settings.Value.ApprovalVerdictWorkflow;
        if (string.IsNullOrWhiteSpace(options.WorkflowType))
            return ApprovalHandlerResult.Deferred;

        var context = new Dictionary<string, string>
        {
            ["approval-workflow-type"] = registration.WorkflowType,
            ["approval-package-uri"] = registration.PackageUri ?? "",
            ["approval-publisher-key"] = registration.PublisherKeyBase64 ?? "",
            ["approval-registered-by"] = registration.RegisteredBy?.ToString("D") ?? "",
        };
        // A core:// coordinate is not fetchable from inside the verdict container (it holds no
        // principal credential, and its own resolution token covers only its OWN package), so the
        // pending package rides as a download URL authorized by a scoped, evaluation-window token.
        if (registration.PackageUri?.StartsWith("core://", StringComparison.OrdinalIgnoreCase) == true
            && settings.Value.PublicBaseAddress?.TrimEnd('/') is { Length: > 0 } baseAddress)
            context["approval-package-download-url"] =
                $"{baseAddress}/api/workflow-types/{Uri.EscapeDataString(registration.WorkflowType)}/package"
                + "?approvalToken=" + Uri.EscapeDataString(
                    downloadTokens.Issue(registration.WorkflowType, registration.PackageHashBase64));

        RunAccepted accepted;
        try
        {
            accepted = await dispatcher.DispatchAsync(new RunRequest(options.WorkflowType, context), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Verdict workflow '{WorkflowType}' could not be dispatched — deferring.", options.WorkflowType);
            return ApprovalHandlerResult.Deferred;
        }

        var run = await AwaitTerminalAsync(accepted, options, ct);
        if (run is null)
        {
            logger.LogWarning(
                "Verdict run {CommandId} for '{Registration}' did not finish within {Timeout}s — deferring.",
                accepted.CommandId, registration.WorkflowType, options.TimeoutSeconds);
            return ApprovalHandlerResult.Deferred;
        }
        if (!string.Equals(run.State, "Success", StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Verdict run {RunId} for '{Registration}' ended {State} — deferring.",
                run.Id, registration.WorkflowType, run.State);
            return ApprovalHandlerResult.Deferred;
        }

        return await ReadVerdictAsync(run, options, registration.WorkflowType, ct);
    }

    /// <summary>The terminal run record, or null on timeout. Addressable by run id OR command id.</summary>
    private async Task<CoreRunRecord?> AwaitTerminalAsync(
        RunAccepted accepted, ApprovalVerdictWorkflowSettings options, CancellationToken ct)
    {
        var deadline = clock.GetUtcNow().AddSeconds(options.TimeoutSeconds);
        while (true)
        {
            var run = (await runs.ReadAsync(ct)).FirstOrDefault(r =>
                r.Id == accepted.RunId || r.CommandId == accepted.CommandId);
            if (run is not null && RunStates.IsTerminal(run.State))
                return run;
            if (clock.GetUtcNow() >= deadline)
                return null;
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.1, options.PollIntervalSeconds)), ct);
        }
    }

    private async Task<ApprovalHandlerResult> ReadVerdictAsync(
        CoreRunRecord run, ApprovalVerdictWorkflowSettings options, string registrationType, CancellationToken ct)
    {
        var verdictArtifact = (await artifacts.ReadAsync(ct))
            .Where(a => a.RunInstanceId == run.Id
                        && string.Equals(a.ArtifactType, options.ArtifactType, StringComparison.Ordinal))
            .OrderByDescending(a => a.CreatedUtc)
            .FirstOrDefault();
        if (verdictArtifact is null)
        {
            logger.LogWarning(
                "Verdict run {RunId} for '{Registration}' produced no '{ArtifactType}' artifact — deferring.",
                run.Id, registrationType, options.ArtifactType);
            return ApprovalHandlerResult.Deferred;
        }

        try
        {
            await using var payload = await payloads.OpenReadAsync(verdictArtifact.Id, ct);
            if (payload is null)
                return ApprovalHandlerResult.Deferred;
            using var doc = await JsonDocument.ParseAsync(payload, cancellationToken: ct);
            var decision = doc.RootElement.TryGetProperty("decision", out var d) ? d.GetString() : null;
            var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
            return decision?.ToLowerInvariant() switch
            {
                "approve" => new ApprovalHandlerResult(ApprovalHandlerResult.Approve, reason),
                "deny" => new ApprovalHandlerResult(ApprovalHandlerResult.Deny, reason),
                _ => ApprovalHandlerResult.Deferred,
            };
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex,
                "Verdict artifact {ArtifactId} of run {RunId} is not valid verdict JSON — deferring.",
                verdictArtifact.Id, run.Id);
            return ApprovalHandlerResult.Deferred;
        }
    }
}
