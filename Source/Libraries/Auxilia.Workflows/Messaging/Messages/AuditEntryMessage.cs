namespace Auxilia.Workflows.Messaging.Messages;

/// <summary>
/// One audit record a platform service (the runner) recorded, forwarded to the Core — the
/// single central audit authority. Platform-only: a workload can neither publish nor forge one.
/// </summary>
public sealed record AuditEntryMessage(
    Guid RecordId,
    DateTimeOffset TimestampUtc,
    string Actor,
    string Action,
    string Subject,
    string Outcome,
    string? DetailJson,
    string Source) : Auxilia.Messaging.IPlatformOnlyMessage
{
    public const string ExchangeName = "platform.audit";
}
