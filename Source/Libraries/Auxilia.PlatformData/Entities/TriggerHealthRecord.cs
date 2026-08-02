using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData.Entities;

/// <summary>
/// Observability sidecar of one trigger (the record ID is the trigger ID): when it last
/// polled, last succeeded, last dispatched, and what is currently failing. Written by the
/// polling adapter, kept separate from the trigger configuration so editor reconciliation
/// never races the health writer. Carries no mail content — only timestamps and the
/// connection error text.
/// </summary>
public sealed record TriggerHealthRecord : IEntity
{
    public Guid Id { get; init; }
    public DateTimeOffset? LastPollUtc { get; init; }
    public DateTimeOffset? LastSuccessUtc { get; init; }
    public DateTimeOffset? LastDispatchUtc { get; init; }
    /// <summary>The current failure, null while healthy.</summary>
    public string? LastError { get; init; }
    /// <summary>When the CURRENT failure streak started — "failing since".</summary>
    public DateTimeOffset? FailingSinceUtc { get; init; }
}
