namespace Auxilia.Messaging;

/// <summary>A topic-exchange routing-key restriction for one exchange (regular expressions).</summary>
public sealed record BrokerTopicPermission(string Exchange, string Write, string Read);

/// <summary>
///     The resource permissions of a broker identity, as anchored regular expressions over queue
///     and exchange names (configure = declare/delete, write = publish/bind-to, read =
///     consume/bind-from), plus routing-key restrictions per topic exchange.
/// </summary>
public sealed record BrokerPermissions(
    string Configure,
    string Write,
    string Read,
    IReadOnlyList<BrokerTopicPermission> TopicPermissions);

/// <summary>
///     Mints and removes short-lived broker identities with narrowly scoped permissions — the
///     mechanism that gives every workload container its own bus credential instead of a shared
///     platform account.
/// </summary>
public interface IBrokerIdentityIssuer
{
    /// <summary>Creates (or replaces) <paramref name="userName" /> with a fresh random password and returns it.</summary>
    Task<string> IssueAsync(string userName, BrokerPermissions permissions, CancellationToken cancellationToken = default);

    /// <summary>Deletes the identity; idempotent — false when it did not exist (not an error).</summary>
    Task<bool> RevokeAsync(string userName, CancellationToken cancellationToken = default);
}
