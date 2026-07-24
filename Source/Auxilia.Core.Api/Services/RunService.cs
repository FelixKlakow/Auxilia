using Auxilia.Core.Contracts;
using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Dispatches runs to the runner pool. Both the "on the fly" and "from a stored configuration"
/// paths resolve here into a self-contained <see cref="RunWorkflowCommand"/> the runner can
/// execute without reading any Core store.
/// </summary>
public sealed class RunService(
    IMessageBusClient bus,
    RunConfigurationService configurations,
    IOptions<CoreApiSettings> settings,
    ILogger<RunService> logger)
{
    public Task<RunAccepted> RunInlineAsync(RunRequest request, CancellationToken ct)
        => DispatchAsync(
            request.WorkflowType, request.PackageUri,
            new Dictionary<string, string>(request.Context ?? new Dictionary<string, string>()),
            request.RequestedBy, ct);

    public async Task<RunAccepted> RunConfigurationAsync(Guid configurationId, Guid? requestedBy, CancellationToken ct)
    {
        var config = await configurations.GetAsync(configurationId, ct)
                     ?? throw new KeyNotFoundException($"configuration '{configurationId}' not found");
        if (!config.Enabled)
            throw new InvalidOperationException($"configuration '{config.Name}' is disabled");

        return await DispatchAsync(
            config.WorkflowType, config.PackageUri,
            new Dictionary<string, string>(config.Context), requestedBy, ct);
    }

    private async Task<RunAccepted> DispatchAsync(
        string workflowType, string packageUri, Dictionary<string, string> context,
        Guid? requestedBy, CancellationToken ct)
    {
        var commandId = Guid.NewGuid();
        var command = new RunWorkflowCommand(commandId, workflowType, packageUri, context, requestedBy);
        await bus.PublishAsync(settings.Value.RunCommandQueue, command, ct);
        logger.LogInformation(
            "Dispatched run. CommandId={CommandId} WorkflowType={WorkflowType}", commandId, workflowType);
        return new RunAccepted(commandId, commandId);
    }
}
