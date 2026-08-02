using System.Collections.Concurrent;
using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Workflows;

/// <summary>
/// Workflow-side client for the Core.Runner's audited Resource Proxy (ARCHITECTURE §8):
/// every structured external call is published as a <see cref="ResourceRequest"/> authenticated
/// by the instance token and answered on the instance's exclusive response queue, correlated
/// by request id — the workflow itself never holds resource credentials.
/// </summary>
public sealed class ResourceProxyClient(
    IMessageBusClient messageBus,
    Guid instanceId,
    string? instanceToken,
    string proxyQueueName,
    string responseTopic) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<ResourceResponse>> _pending = new();
    private IAsyncDisposable? _subscription;

    internal TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public async Task StartAsync(CancellationToken ct = default)
    {
        // Own per-instance queue: subscribing the main response queue would compete with the
        // slot activator for activation/configuration messages on a real broker.
        await messageBus.DeclareQueueAsync(responseTopic, ct);
        _subscription = await messageBus.SubscribeAsync<ResourceResponse>(
            responseTopic,
            (response, _) =>
            {
                if (_pending.TryRemove(response.RequestId, out var tcs))
                    tcs.TrySetResult(response);
                return Task.CompletedTask;
            },
            ct);
    }

    /// <summary>Executes one audited resource operation via the platform and returns its result JSON.</summary>
    public async Task<string> CallAsync(
        string resourceName, string operation, string payloadJson, CancellationToken ct = default)
    {
        var requestId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<ResourceResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;

        await messageBus.PublishAsync(proxyQueueName,
            new ResourceRequest(instanceId, resourceName, operation, payloadJson, requestId, instanceToken), ct);

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(CallTimeout, ct));
        if (completed != tcs.Task)
        {
            _pending.TryRemove(requestId, out _);
            throw new TimeoutException(
                $"Resource call '{resourceName}:{operation}' received no response within {CallTimeout.TotalSeconds:0}s.");
        }

        var response = tcs.Task.Result;
        if (!response.Success)
            throw new InvalidOperationException(
                $"Resource call '{resourceName}:{operation}' failed: {response.ErrorMessage ?? "unknown error"}");

        return response.ResultJson ?? string.Empty;
    }

    public async ValueTask DisposeAsync()
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}
