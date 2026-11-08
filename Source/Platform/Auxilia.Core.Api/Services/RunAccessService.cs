using System.Security.Claims;
using System.Text.Json;
using Auxilia.Core.Api.Auth;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Contracts;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Services;

/// <summary>How a principal relates to a run — the reasons it may reach it.</summary>
public enum RunRelation
{
    None,
    RunsFor,
    StartedBy,
    Granted,
}

/// <summary>
/// One caller's view of runs: who they are, the groups a run grant can admit them through, and
/// whether they administer the platform. Built once per request so a list never re-reads
/// group memberships per run.
/// </summary>
public sealed record RunViewer(
    Guid PrincipalId,
    bool IsAdministrator,
    IReadOnlySet<Guid> GroupIds,
    IReadOnlySet<string> DirectoryGroups)
{
    public RunRelation RelationTo(CoreRunRecord run)
    {
        if (run.RunsForPrincipalId == PrincipalId)
            return RunRelation.RunsFor;
        if (run.StartedByPrincipalId == PrincipalId)
            return RunRelation.StartedBy;
        return AccessGrantEvaluator.Parse(run.GrantsJson).Any(Admits) ? RunRelation.Granted : RunRelation.None;
    }

    /// <summary>Whether the run is visible to this caller at all (lists, streams of related records).</summary>
    public bool CanSee(CoreRunRecord run) => IsAdministrator || RelationTo(run) != RunRelation.None;

    /// <summary>Whether this caller may change who the run is shared with (grantees may not re-share).</summary>
    public bool MayShare(CoreRunRecord run)
        => IsAdministrator || RelationTo(run) is RunRelation.RunsFor or RunRelation.StartedBy;

    private bool Admits(AccessGrant grant) => grant.Kind switch
    {
        AccessGrantKind.Principal => Guid.TryParse(grant.Id, out var principal) && principal == PrincipalId,
        AccessGrantKind.Group => Guid.TryParse(grant.Id, out var group) && GroupIds.Contains(group),
        AccessGrantKind.DirectoryGroup => DirectoryGroups.Contains(grant.Id),
        _ => false,
    };
}

/// <summary>
/// Per-run authorization (ARCHITECTURE §16 "Run access"). A run — its status, live stream,
/// stored views, inputs, terminal, cancel/rerun, artifacts and events — is reachable only by the
/// principal it runs for, the principal that started it on that one's behalf, and the subjects
/// its grants admit (the sharing model connectors use). Administrators keep access; when they
/// reach someone else's run through a surface that carries its content or steers it, that is
/// audited (<c>run.admin-access</c>). Role permissions still apply on top: this decides WHICH
/// runs, the role decides WHAT a caller may do with them. An inaccessible run is
/// indistinguishable from a missing one — every surface answers 404.
/// </summary>
public sealed class RunAccessService(
    IDataAccess<CoreRunRecord> runs,
    IDataAccess<GroupMembershipRecord> groupMemberships,
    IDataAccess<PrincipalRecord> principals,
    Auxilia.Governance.PrincipalDirectory principalDirectory,
    AuditLog audit)
{
    /// <summary>Audit action recorded when an administrator reaches a run that is not theirs.</summary>
    public const string AdministratorAccessAction = "run.admin-access";

    public async Task<RunViewer> ViewerForAsync(Guid principalId, CancellationToken ct)
    {
        var isAdministrator = await principalDirectory.IsAdministratorAsync(principalId, ct);
        var groups = (await groupMemberships.ReadAsync(ct))
            .Where(m => m.PrincipalId == principalId)
            .Select(m => m.GroupId)
            .ToHashSet();
        var principal = await principals.ReadAsync(principalId, ct);
        var directoryGroups = (JsonSerializer.Deserialize<List<string>>(principal?.DirectoryGroupsJson ?? "[]") ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new RunViewer(principalId, isAdministrator, groups, directoryGroups);
    }

    /// <summary>The caller's view; null for an unauthenticated (principal-less) caller.</summary>
    public async Task<RunViewer?> ViewerForAsync(ClaimsPrincipal user, CancellationToken ct)
        => CoreClaims.PrincipalIdOf(user) is { } principalId ? await ViewerForAsync(principalId, ct) : null;

    /// <summary>The run behind an id — its instance id, or the dispatch command id a caller holds.</summary>
    public async Task<CoreRunRecord?> ResolveAsync(Guid id, CancellationToken ct)
        => await runs.ReadAsync(id, ct)
           ?? (await runs.ReadAsync(ct)).FirstOrDefault(r => r.CommandId == id);

    /// <summary>
    /// The run behind <paramref name="id"/> when <paramref name="viewer"/> may reach it — null
    /// when it is missing OR someone else's (callers answer 404 either way). With a
    /// <paramref name="surface"/>, an administrator reaching another principal's run is audited.
    /// </summary>
    public async Task<CoreRunRecord?> FindAsync(RunViewer viewer, Guid id, string? surface, CancellationToken ct)
    {
        var run = await ResolveAsync(id, ct);
        return run is not null && await AdmitAsync(viewer, run, surface, ct) ? run : null;
    }

    /// <summary>Whether <paramref name="viewer"/> may reach <paramref name="run"/>; audits administrator access on a named surface.</summary>
    public async Task<bool> AdmitAsync(RunViewer viewer, CoreRunRecord run, string? surface, CancellationToken ct)
    {
        if (viewer.RelationTo(run) != RunRelation.None)
            return true;
        if (!viewer.IsAdministrator)
            return false;
        if (surface is not null)
            await audit.AppendAsync(viewer.PrincipalId.ToString("D"), AdministratorAccessAction,
                run.Id.ToString(), surface, ct: ct);
        return true;
    }

    /// <summary>
    /// Whether the run an artifact or event came from is visible to <paramref name="viewer"/>.
    /// A run whose record is gone (history cleared) is visible to administrators only.
    /// </summary>
    public async Task<bool> CanSeeRunAsync(RunViewer viewer, Guid runId, CancellationToken ct)
    {
        if (viewer.IsAdministrator)
            return true;
        return await ResolveAsync(runId, ct) is { } run && viewer.RelationTo(run) != RunRelation.None;
    }

    /// <summary>
    /// Replaces a run's grants (the caller already passed <see cref="RunViewer.MayShare"/>). A
    /// versioned compare-and-swap: a status transition saved meanwhile is re-read, never lost.
    /// </summary>
    public async Task<CoreRunRecord?> SetGrantsAsync(Guid runId, IReadOnlyList<AccessGrant> grants, CancellationToken ct)
    {
        foreach (var grant in grants)
            if (grant.Kind is not (AccessGrantKind.Principal or AccessGrantKind.Group or AccessGrantKind.DirectoryGroup)
                || string.IsNullOrWhiteSpace(grant.Id)
                || (grant.Kind != AccessGrantKind.DirectoryGroup && !Guid.TryParse(grant.Id, out _)))
                throw new ArgumentException($"grant '{grant.Kind}:{grant.Id}' is not a principal, group or directory-group subject");
        var grantsJson = grants.Count == 0 ? null : JsonSerializer.Serialize(grants);
        while (true)
        {
            if (await runs.ReadAsync(runId, ct) is not { } current)
                return null;
            var updated = current with { GrantsJson = grantsJson };
            if (await runs.TrySaveAsync(updated, current.Version, ct))
                return await runs.ReadAsync(runId, ct);
        }
    }
}
