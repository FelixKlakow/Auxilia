using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Options;

namespace Auxilia.Adapters.Email;

/// <summary>
/// Dispatch seam for the mailbox trigger: turns a matched mail into a workflow run without the
/// adapter knowing <em>how</em> the run reaches the platform. BackendService binds the default
/// <see cref="BusRunDispatcher"/> (publish a <c>RunWorkflowCommand</c> to the command queue);
/// WorkflowStudio binds <see cref="CoreClientRunDispatcher"/> (drive the Core Run API).
/// </summary>
public interface ITaskSourceRunDispatcher
{
    /// <summary>
    /// Dispatches one run. With <paramref name="configurationId"/> set, the run comes from a stored
    /// configuration; otherwise <paramref name="workflowType"/>/<paramref name="packageUri"/> spec it
    /// ad hoc. Returns a correlation id for the dispatch (command or run id) for auditing.
    /// </summary>
    Task<Guid> DispatchAsync(
        Guid? configurationId, string? workflowType, string? packageUri,
        IReadOnlyDictionary<string, string> context, Guid? runAsPrincipalId,
        CancellationToken ct = default);
}

/// <summary>
/// Default dispatcher: builds a <see cref="RunWorkflowCommand"/> and publishes it to the Core.Runner
/// command queue — the behaviour the adapter has always had inside BackendService.
/// </summary>
public sealed class BusRunDispatcher(
    IMessageBusClient messageBus,
    IOptions<MailboxTriggerAdapterSettings> options) : ITaskSourceRunDispatcher
{
    public async Task<Guid> DispatchAsync(
        Guid? configurationId, string? workflowType, string? packageUri,
        IReadOnlyDictionary<string, string> context, Guid? runAsPrincipalId,
        CancellationToken ct = default)
    {
        var command = new RunWorkflowCommand(
            Guid.NewGuid(), workflowType, packageUri, context, runAsPrincipalId, configurationId);
        await messageBus.PublishAsync(options.Value.CommandQueueName, command, ct);
        return command.CommandId;
    }
}
