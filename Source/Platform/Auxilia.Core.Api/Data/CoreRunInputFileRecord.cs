using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Data;

/// <summary>
/// A file uploaded for a run. The content lives encrypted beside the artifact payloads
/// (<see cref="Services.RunInputFileService"/>); this record is its index and its binding: unbound
/// it belongs to <see cref="OwnerPrincipalId"/> until <see cref="ExpiresUtc"/>, bound it belongs to
/// one run's dispatch command under the workflow's declared <see cref="Name"/>.
/// </summary>
public sealed record CoreRunInputFileRecord : IVersionedEntity
{
    public Guid Id { get; init; }

    /// <summary>Store-managed optimistic-concurrency version: an upload binds to exactly one run.</summary>
    public long Version { get; set; }

    public Guid OwnerPrincipalId { get; init; }
    public long Length { get; init; }

    /// <summary>Hex SHA-256 of the content.</summary>
    public required string Sha256 { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>When an unbound upload is deleted.</summary>
    public DateTimeOffset ExpiresUtc { get; init; }

    /// <summary>The dispatch command of the run the file is bound to; null while unbound.</summary>
    public Guid? BoundCommandId { get; init; }

    /// <summary>The input-file name the workflow declares, once bound.</summary>
    public string? Name { get; init; }

    public DateTimeOffset? BoundUtc { get; init; }
}
