using System.Collections.Concurrent;
using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Workflows.Companions;

/// <summary>
/// In-container <see cref="IPodController"/>: publishes token-authenticated
/// <see cref="PodControlRequest"/>s to the runner's pod-control queue and awaits the
/// response on the instance's exclusive pod queue (the <see cref="ResourceProxyClient"/>
/// pattern). The generous timeout covers first-time image pulls.
/// </summary>
public sealed class PodControlClient(
    IMessageBusClient messageBus,
    Guid instanceId,
    string? instanceToken,
    string podControlQueueName,
    string responseTopic) : IPodController, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<PodControlResponse>> _pending = new();
    private IAsyncDisposable? _subscription;

    internal TimeSpan CallTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public async Task StartAsync(CancellationToken ct = default)
    {
        await messageBus.DeclareQueueAsync(responseTopic, ct);
        _subscription = await messageBus.SubscribeAsync<PodControlResponse>(
            responseTopic,
            (response, _) =>
            {
                if (_pending.TryRemove(response.RequestId, out var tcs))
                    tcs.TrySetResult(response);
                return Task.CompletedTask;
            },
            ct);
    }

    public async Task<SpawnedCompanion> SpawnAsync(CompanionSpec spec, CancellationToken ct = default)
    {
        var resultJson = await CallAsync(PodControlRequest.Spawn, JsonSerializer.Serialize(spec), ct);
        return JsonSerializer.Deserialize<SpawnedCompanion>(resultJson, JsonOptions)
               ?? throw new InvalidOperationException("pod-control spawn returned no companion");
    }

    public async Task StopAsync(string name, CancellationToken ct = default)
        => await CallAsync(PodControlRequest.Stop, JsonSerializer.Serialize(new CompanionSpec(name, "")), ct);

    private async Task<string> CallAsync(string action, string specJson, CancellationToken ct)
    {
        var requestId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<PodControlResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;

        await messageBus.PublishAsync(podControlQueueName,
            new PodControlRequest(instanceId, action, specJson, requestId, instanceToken), ct);

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(CallTimeout, ct));
        if (completed != tcs.Task)
        {
            _pending.TryRemove(requestId, out _);
            // A cancelled delay task completes too; the caller's cancellation is not a timeout.
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException(
                $"Pod-control '{action}' received no response within {CallTimeout.TotalSeconds:0}s.");
        }

        var response = tcs.Task.Result;
        if (!response.Success)
            throw new InvalidOperationException(
                $"Pod-control '{action}' refused: {response.ErrorMessage ?? "unknown error"}");
        return response.ResultJson ?? string.Empty;
    }

    public async ValueTask DisposeAsync()
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}
