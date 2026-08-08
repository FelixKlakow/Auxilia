using Auxilia.Core.Client;
using Auxilia.Core.Contracts;

namespace Auxilia.AdminConsole.Support;

/// <summary>
/// Circuit-scoped cache of the sharing-subject directory (principals and groups). Every sharing
/// editor needs the same two lists to render pickers and to turn a stored
/// <see cref="AccessGrant"/> back into a name, so they are fetched once per circuit rather than
/// once per page. A directory that cannot be read degrades to raw-id entry — never to a failure.
/// </summary>
public sealed class SharingDirectory(ICoreClient core)
{
    private SharingSubjects? _subjects;
    private Task<SharingSubjects>? _inflight;

    public SharingSubjects Subjects => _subjects ?? new SharingSubjects([], []);

    public bool Loaded => _subjects is not null;

    public Task<SharingSubjects> LoadAsync(CancellationToken ct = default)
        => _subjects is { } cached ? Task.FromResult(cached) : _inflight ??= FetchAsync(ct);

    private async Task<SharingSubjects> FetchAsync(CancellationToken ct)
    {
        try
        {
            _subjects = await core.GetSharingSubjectsAsync(ct);
        }
        catch (CoreApiException)
        {
            // Pickers fall back to raw-id inputs; saving still works.
            _subjects = new SharingSubjects([], []);
        }
        _inflight = null;
        return _subjects;
    }

    /// <summary>Display name for a grant subject, falling back to the raw id when unresolvable.</summary>
    public string NameOf(string kind, string id)
    {
        if (!Guid.TryParse(id, out var parsed))
            return id;
        return kind switch
        {
            AccessGrantKind.Principal =>
                Subjects.Principals.FirstOrDefault(p => p.Id == parsed)?.DisplayName ?? id,
            AccessGrantKind.Group =>
                Subjects.Groups.FirstOrDefault(g => g.Id == parsed)?.Name ?? id,
            _ => id
        };
    }

    public string NameOf(AccessGrant grant) => NameOf(grant.Kind, grant.Id);

    /// <summary>Human label for a subject kind, used in chips and access-list rows.</summary>
    public static string KindLabel(string kind) => kind switch
    {
        AccessGrantKind.Principal => "Principal",
        AccessGrantKind.Group => "Group",
        AccessGrantKind.DirectoryGroup => "Directory group",
        _ => kind
    };
}

/// <summary>One editable row of a sharing editor — a grant before it is validated and saved.</summary>
public sealed class GrantRow
{
    public string Kind { get; set; } = AccessGrantKind.Principal;

    public string Id { get; set; } = "";
}

public static class GrantRows
{
    public static List<GrantRow> From(IEnumerable<AccessGrant>? grants)
        => (grants ?? []).Select(g => new GrantRow { Kind = g.Kind, Id = g.Id }).ToList();

    /// <summary>Drops incomplete rows — an empty picker is an unfinished edit, not a grant.</summary>
    public static List<AccessGrant> ToGrants(IEnumerable<GrantRow> rows)
        => rows.Where(r => !string.IsNullOrWhiteSpace(r.Id))
            .Select(r => new AccessGrant(r.Kind, r.Id.Trim()))
            .ToList();
}
