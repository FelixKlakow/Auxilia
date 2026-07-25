using Auxilia.Core.Contracts;
using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.Workspace;
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
    ConnectorAccessPolicy connectorAccess,
    IOptions<CoreApiSettings> settings,
    ILogger<RunService> logger)
{
    /// <summary>Prefix of the synthetic slot a repository's auth connector is stashed under.</summary>
    internal const string RepositoryAuthSlotPrefix = "repo-auth:";

    public Task<RunAccepted> RunInlineAsync(RunRequest request, Guid? triggeredBy, CancellationToken ct)
        => DispatchAsync(
            request.WorkflowType, request.PackageUri,
            new Dictionary<string, string>(request.Context ?? new Dictionary<string, string>()),
            request.SlotBindings ?? [], request.Repositories ?? [], triggeredBy, ct);

    public async Task<RunAccepted> RunConfigurationAsync(Guid configurationId, Guid? triggeredBy, CancellationToken ct)
    {
        var config = await configurations.GetAsync(configurationId, ct)
                     ?? throw new KeyNotFoundException($"configuration '{configurationId}' not found");
        if (!config.Enabled)
            throw new InvalidOperationException($"configuration '{config.Name}' is disabled");

        return await DispatchAsync(
            config.WorkflowType, config.PackageUri,
            new Dictionary<string, string>(config.Context), config.SlotBindings, [], triggeredBy, ct);
    }

    /// <summary>Requests cancellation of a run; the runner consumes the command and stops the container.</summary>
    public async Task CancelAsync(Guid runId, CancellationToken ct)
    {
        await bus.PublishAsync(settings.Value.CancelCommandQueue, new CancelWorkflowCommand(runId), ct);
        logger.LogInformation("Requested cancel. RunId={RunId}", runId);
    }

    private async Task<RunAccepted> DispatchAsync(
        string workflowType, string packageUri, Dictionary<string, string> context,
        IReadOnlyList<SlotBinding> slotBindings, IReadOnlyList<RepositorySpec> repositories,
        Guid? triggeredBy, CancellationToken ct)
    {
        // Each authenticated repository's credential lives in a Core connector; stash it under a
        // synthetic slot so the runner resolves it JIT at dispatch (never on the bus). The repo URL
        // itself is non-secret and rides the command.
        var authBindings = repositories
            .Where(r => r.AuthConnectorId is not null)
            .Select(r => new SlotBinding(RepositoryAuthSlotPrefix + r.Id, ConnectorId: r.AuthConnectorId))
            .ToList();
        var stashedBindings = slotBindings.Concat(authBindings).ToList();

        // Gate identity-linked connectors — including each repo's auth connector: the triggering
        // principal must be allowed to use every connector this run binds. Company connectors pass
        // freely; a personal connector admits only its owner and granted subjects.
        foreach (var connectorId in stashedBindings
                     .Where(b => b.ConnectorId is not null)
                     .Select(b => b.ConnectorId!.Value)
                     .Distinct())
            if (!await connectorAccess.CanUseAsync(connectorId, triggeredBy, ct))
                throw new ConnectorAccessDeniedException(connectorId);

        // The Core is the single authorization authority: the caller was already authorized here,
        // so the command is dispatched WITHOUT a RequestedBy principal. The runner trusts
        // Core-dispatched commands and cannot resolve a Core-database principal against its own.
        var commandId = Guid.NewGuid();

        // Stash the run's slot→connector references under a run-scoped resolution token; the runner
        // presents the token to resolve credentialed slots (and repo auth) JIT. No secrets travel in
        // the command — only the provider types, so the runner can load the matching slot plugins.
        var resolutionToken = Guid.NewGuid().ToString("N");
        await credentialResolver.StashAsync(commandId, resolutionToken, stashedBindings, triggeredBy, ct);
        var providerTypes = slotBindings
            .Where(b => !string.IsNullOrEmpty(b.ProviderType))
            .Select(b => b.ProviderType!)
            .Distinct()
            .ToList();
        var repositoryDispatch = repositories
            .Select(r => new RepositoryDispatch(
                r.Id, r.CloneUrl, r.Branch,
                r.AuthConnectorId is not null ? RepositoryAuthSlotPrefix + r.Id : null, r.NoCache))
            .ToList();

        var command = new RunWorkflowCommand(
            commandId, workflowType, packageUri, context,
            RequestedBy: null, WorkflowConfigurationId: null, ResolutionToken: resolutionToken,
            SlotProviderTypes: providerTypes,
            Repositories: repositoryDispatch.Count > 0 ? repositoryDispatch : null);
        await bus.PublishAsync(settings.Value.RunCommandQueue, command, ct);
        logger.LogInformation(
            "Dispatched run. CommandId={CommandId} WorkflowType={WorkflowType} Repositories={RepositoryCount}",
            commandId, workflowType, repositoryDispatch.Count);
        return new RunAccepted(commandId, commandId);
    }
}
