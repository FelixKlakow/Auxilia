using Auxilia.Core.Contracts;
using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Dispatches runs to the runner pool. Both the "on the fly" and "from a stored configuration"
/// paths resolve here into a self-contained <see cref="RunWorkflowCommand"/> the runner can
/// execute without reading any Core store. The command carries a run-scoped resolution token —
/// never secrets — that the runner presents to resolve credentialed slots just-in-time.
/// </summary>
public sealed class RunService(
    IMessageBusClient bus,
    RunConfigurationService configurations,
    SlotCredentialResolver credentialResolver,
    IOptions<CoreApiSettings> settings,
    ILogger<RunService> logger)
{
    public Task<RunAccepted> RunInlineAsync(RunRequest request, CancellationToken ct)
        => DispatchAsync(
            request.WorkflowType, request.PackageUri,
            new Dictionary<string, string>(request.Context ?? new Dictionary<string, string>()),
            request.SlotBindings ?? [], ct);

    public async Task<RunAccepted> RunConfigurationAsync(Guid configurationId, Guid? requestedBy, CancellationToken ct)
    {
        var config = await configurations.GetAsync(configurationId, ct)
                     ?? throw new KeyNotFoundException($"configuration '{configurationId}' not found");
        if (!config.Enabled)
            throw new InvalidOperationException($"configuration '{config.Name}' is disabled");

        return await DispatchAsync(
            config.WorkflowType, config.PackageUri,
            new Dictionary<string, string>(config.Context), config.SlotBindings, ct);
    }

    /// <summary>Requests cancellation of a run; the runner consumes the command and stops the container.</summary>
    public async Task CancelAsync(Guid runId, CancellationToken ct)
    {
        await bus.PublishAsync(settings.Value.CancelCommandQueue, new CancelWorkflowCommand(runId), ct);
        logger.LogInformation("Requested cancel. RunId={RunId}", runId);
    }

    private async Task<RunAccepted> DispatchAsync(
        string workflowType, string packageUri, Dictionary<string, string> context,
        IReadOnlyList<SlotBinding> slotBindings, CancellationToken ct)
    {
        // The Core is the single authorization authority: the caller was already authorized here,
        // so the command is dispatched WITHOUT a RequestedBy principal. The runner trusts
        // Core-dispatched commands and cannot resolve a Core-database principal against its own.
        var commandId = Guid.NewGuid();

        // Stash the run's slot→connector references under a run-scoped resolution token; the runner
        // presents the token to resolve credentialed slots JIT. No secrets travel in the command —
        // only the provider types, so the runner can load the matching slot-handler plugins.
        var resolutionToken = Guid.NewGuid().ToString("N");
        await credentialResolver.StashAsync(commandId, resolutionToken, slotBindings, ct);
        var providerTypes = slotBindings
            .Where(b => !string.IsNullOrEmpty(b.ProviderType))
            .Select(b => b.ProviderType!)
            .Distinct()
            .ToList();

        var command = new RunWorkflowCommand(
            commandId, workflowType, packageUri, context,
            RequestedBy: null, WorkflowConfigurationId: null, ResolutionToken: resolutionToken,
            SlotProviderTypes: providerTypes);
        await bus.PublishAsync(settings.Value.RunCommandQueue, command, ct);
        logger.LogInformation(
            "Dispatched run. CommandId={CommandId} WorkflowType={WorkflowType}", commandId, workflowType);
        return new RunAccepted(commandId, commandId);
    }
}
