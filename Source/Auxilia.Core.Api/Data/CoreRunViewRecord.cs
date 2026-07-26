using Auxilia.PlatformData;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Data;

/// <summary>
/// One persisted view item of a run, mirrored into the Core's own store from the runner's
/// <see cref="Auxilia.Workflows.Messaging.Messages.ViewDataMessage"/> bus fanout (same isolation
/// pattern as <see cref="CoreRunRecord"/>). This is what lets a client read a run's outputs
/// AFTER the fact — the SSE stream is live-only.
/// </summary>
public sealed record CoreRunViewRecord : IEntity
{
    public Guid Id { get; init; }

    /// <summary>The runner-assigned workflow instance id (the run id of <see cref="CoreRunRecord"/>).</summary>
    public Guid RunId { get; init; }

    public required string ViewName { get; init; }

    /// <summary>Per-(run, view) monotonic sequence — orders and de-duplicates items.</summary>
    public long Sequence { get; init; }

    public required string PayloadJson { get; init; }

    public DateTimeOffset TimestampUtc { get; init; }

    public static Guid IdFor(Guid runId, string viewName, long sequence)
        => DeterministicGuid.For("core-run-view", runId.ToString("N"), viewName, sequence.ToString());
}
