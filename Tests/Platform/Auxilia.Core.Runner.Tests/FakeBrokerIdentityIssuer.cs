using System.Collections.Concurrent;
using Auxilia.Messaging;

namespace Auxilia.Core.Runner.Tests;

/// <summary>In-memory broker identity issuer: records every issued identity and its permissions.</summary>
internal sealed class FakeBrokerIdentityIssuer : IBrokerIdentityIssuer
{
    public ConcurrentDictionary<string, BrokerPermissions> Active { get; } = new();
    public ConcurrentBag<string> Revoked { get; } = [];

    public Task<string> IssueAsync(string userName, BrokerPermissions permissions, CancellationToken cancellationToken = default)
    {
        Active[userName] = permissions;
        return Task.FromResult($"pw-{userName}");
    }

    public Task<bool> RevokeAsync(string userName, CancellationToken cancellationToken = default)
    {
        Revoked.Add(userName);
        return Task.FromResult(Active.TryRemove(userName, out _));
    }
}
