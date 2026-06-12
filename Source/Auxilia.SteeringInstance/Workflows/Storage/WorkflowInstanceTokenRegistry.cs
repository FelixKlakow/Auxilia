using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Auxilia.SteeringInstance.Workflows.Storage;

/// <summary>
/// Issues and validates the one-time instance tokens that authenticate the workflow
/// registration handshake. A token is issued per launch, validated on announcement,
/// and consumed (single use) when the registration is answered.
/// </summary>
public sealed class WorkflowInstanceTokenRegistry(
    IOptions<WorkflowDispatcherSettings> settings,
    TimeProvider timeProvider)
{
    private sealed record Entry(string Token, string WorkflowType, DateTimeOffset IssuedAt);

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    public IssuedInstanceToken Issue(string workflowType)
    {
        var instanceId = Guid.NewGuid();
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _entries[instanceId] = new Entry(token, workflowType, timeProvider.GetUtcNow());
        return new IssuedInstanceToken(instanceId, token);
    }

    public bool Validate(Guid workflowInstanceId, string? token)
    {
        if (string.IsNullOrEmpty(token))
            return false;
        if (!_entries.TryGetValue(workflowInstanceId, out var entry))
            return false;

        if (timeProvider.GetUtcNow() - entry.IssuedAt > settings.Value.InstanceTokenLifetime)
        {
            _entries.TryRemove(workflowInstanceId, out _);
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(entry.Token),
            Encoding.UTF8.GetBytes(token));
    }

    public void Consume(Guid workflowInstanceId) => _entries.TryRemove(workflowInstanceId, out _);
}

public sealed record IssuedInstanceToken(Guid WorkflowInstanceId, string Token);
