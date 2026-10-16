namespace Auxilia.Messaging;

/// <summary>
///     Naming and attribution rules for per-instance workload broker identities. Every workload
///     container connects as its own broker user (<see cref="UserNameFor" />), never with a
///     platform account; platform service accounts must not use the <see cref="UserNamePrefix" />.
/// </summary>
public static class WorkloadBusIdentity
{
    public const string UserNamePrefix = "auxilia-wf-";

    public static string UserNameFor(Guid instanceId) => $"{UserNamePrefix}{instanceId:N}";

    public static bool IsWorkloadUser(string? userName)
        => userName is not null && userName.StartsWith(UserNamePrefix, StringComparison.Ordinal);

    /// <summary>
    ///     Whether a message published by <paramref name="publisherUserId" /> (the broker-validated
    ///     publisher identity) may speak for <paramref name="instanceId" />: platform identities
    ///     may speak for any instance, a workload identity only for its own, and an unattributed
    ///     publish for none.
    /// </summary>
    public static bool MayPublishFor(string? publisherUserId, Guid instanceId)
    {
        if (string.IsNullOrEmpty(publisherUserId))
            return false;
        return !IsWorkloadUser(publisherUserId)
               || string.Equals(publisherUserId, UserNameFor(instanceId), StringComparison.Ordinal);
    }
}
