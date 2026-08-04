using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Runner.Workflows.Storage;

/// <summary>
/// Issues and validates the instance tokens that authenticate the workflow handshake.
/// A token is issued per launch; registration is single-use (<see cref="TryBeginRegistration"/>);
/// after registration the token stays valid as the instance credential for just-in-time slot
/// activations until the run reaches a terminal state (<see cref="Consume"/>). The issuance
/// lifetime only bounds the launch→registration window.
/// </summary>
public sealed class WorkflowInstanceTokenRegistry(
    IOptions<WorkflowDispatcherSettings> settings,
    TimeProvider timeProvider)
{
    private sealed record Entry(string Token, string WorkflowType, DateTimeOffset IssuedAt)
    {
        public bool Registered { get; set; }
    }

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    public IssuedInstanceToken Issue(string workflowType)
    {
        var instanceId = Guid.NewGuid();
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _entries[instanceId] = new Entry(token, workflowType, timeProvider.GetUtcNow());
        return new IssuedInstanceToken(instanceId, token);
    }

    /// <summary>
    /// Restores a re-adopted instance's token after a runner restart so the in-container SDK
    /// keeps authenticating. Registered instances (Running/Draining) keep their credential
    /// alive until terminal; unregistered ones get a fresh launch→registration window.
    /// </summary>
    public void Restore(Guid workflowInstanceId, string token, string workflowType, bool registered)
        => _entries[workflowInstanceId] =
            new Entry(token, workflowType, timeProvider.GetUtcNow()) { Registered = registered };

    /// <summary>Valid token for an instance that has not yet registered or is registered.</summary>
    public bool Validate(Guid workflowInstanceId, string? token)
    {
        if (string.IsNullOrEmpty(token))
            return false;
        if (!_entries.TryGetValue(workflowInstanceId, out var entry))
            return false;

        // The issuance lifetime bounds only the unregistered launch window; once
        // registered, the token lives until the run terminates.
        if (!entry.Registered &&
            timeProvider.GetUtcNow() - entry.IssuedAt > settings.Value.InstanceTokenLifetime)
        {
            _entries.TryRemove(workflowInstanceId, out _);
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(entry.Token),
            Encoding.UTF8.GetBytes(token));
    }

    /// <summary>
    /// Validates the token and marks the instance registered — exactly once. A second
    /// registration attempt with the same token is rejected.
    /// </summary>
    public bool TryBeginRegistration(Guid workflowInstanceId, string? token)
    {
        if (!Validate(workflowInstanceId, token))
            return false;
        if (!_entries.TryGetValue(workflowInstanceId, out var entry))
            return false;

        lock (entry)
        {
            if (entry.Registered)
                return false;
            entry.Registered = true;
            return true;
        }
    }

    /// <summary>True when the instance has completed its (single) registration.</summary>
    public bool IsRegistered(Guid workflowInstanceId)
        => _entries.TryGetValue(workflowInstanceId, out var entry) && entry.Registered;

    public void Consume(Guid workflowInstanceId) => _entries.TryRemove(workflowInstanceId, out _);
}

public sealed record IssuedInstanceToken(Guid WorkflowInstanceId, string Token);
