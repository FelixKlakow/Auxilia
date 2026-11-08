using System.Collections.Concurrent;
using Auxilia.Messaging;
using Auxilia.Workflows.Crypto;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Workflows;

/// <summary>
/// Just-in-time slot activation: each declared slot's configuration is requested individually
/// from the Core.Runner (audited, expiring, encrypted for the instance's ephemeral key,
/// answered only on the pre-created response queue) — credentials never arrive as a bundle at
/// registration. Every slot activates at bootstrap, because <see cref="ISlotHandler"/> contributes
/// arbitrary registrations to the workflow's service collection; afterwards the activator serves
/// the run as its <see cref="ISlotConfigurationSource"/>, so a handler whose credential outlives
/// the start (a token that expires, one the owner replaces) fetches its slot again. One
/// activation per slot is in flight at a time — responses are matched by slot name.
/// </summary>
public sealed class SlotActivator(
    IMessageBusClient messageBus,
    EphemeralKeyPair keyPair,
    Guid instanceId,
    string? instanceToken,
    string activationQueueName,
    string responseTopic) : ISlotConfigurationSource, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<SlotActivationResponse>> _pending = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _inFlight = new(StringComparer.Ordinal);
    private IAsyncDisposable? _subscription;

    internal TimeSpan ActivationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public async Task StartAsync(CancellationToken ct = default)
    {
        _subscription = await messageBus.SubscribeAsync<SlotActivationResponse>(
            responseTopic,
            (response, _) =>
            {
                if (_pending.TryRemove(response.SlotName, out var tcs))
                    tcs.TrySetResult(response);
                return Task.CompletedTask;
            },
            ct);
    }

    /// <summary>Requests and decrypts one slot's configuration — at bootstrap, and again whenever a handler asks.</summary>
    public async Task<SlotConfiguration> FetchAsync(string slotName, CancellationToken ct = default)
    {
        var gate = _inFlight.GetOrAdd(slotName, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await ActivateAsync(slotName, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SlotConfiguration> ActivateAsync(string slotName, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<SlotActivationResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[slotName] = tcs;

        await messageBus.PublishAsync(activationQueueName,
            new SlotActivationRequest(instanceId, slotName, keyPair.PublicKeyBase64, instanceToken), ct);

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(ActivationTimeout, ct));
        if (completed != tcs.Task)
        {
            _pending.TryRemove(slotName, out _);
            // A cancelled delay task completes too; the caller's cancellation is not a timeout.
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException(
                $"Slot activation for '{slotName}' received no response within {ActivationTimeout.TotalSeconds:0}s.");
        }

        var response = tcs.Task.Result;
        if (!response.Success || response.Slot is null)
            throw new InvalidOperationException(
                $"Slot activation for '{slotName}' failed: {response.ErrorMessage ?? "no configuration returned"}");

        return SlotConfigurationCrypto.Decrypt(response.Slot, keyPair);
    }

    public async ValueTask DisposeAsync()
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
        foreach (var gate in _inFlight.Values)
            gate.Dispose();
    }
}
