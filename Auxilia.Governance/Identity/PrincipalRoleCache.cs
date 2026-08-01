using System.Collections.Concurrent;
using Auxilia.PlatformData.Entities;

namespace Auxilia.Governance.Identity;

/// <summary>
/// Short-TTL cache of a principal's record + resolved roles, shared by the identity provider
/// (per-request bearer authentication) and the Policy Engine (per-action checks) — without it
/// every authenticated request costs several store reads, which is the first thing to fall
/// over at high client counts. OFF by default (<see cref="GovernanceSettings.PrincipalCacheSeconds"/> = 0)
/// so library semantics are exact unless the host opts in. Same-node admin writes invalidate
/// eagerly; across nodes staleness is bounded by the TTL — keep it small (seconds).
/// </summary>
public sealed class PrincipalRoleCache(GovernanceSettings settings, TimeProvider clock)
{
    private sealed class Entry
    {
        public required DateTimeOffset ExpiresUtc { get; init; }
        public required PrincipalRecord? Principal { get; init; }
        public required IReadOnlyList<string> DirectRoles { get; init; }
        /// <summary>Filled lazily by the Policy Engine; null = not resolved yet.</summary>
        public volatile IReadOnlyList<string>? GroupRoles;
    }

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    public bool Enabled => settings.PrincipalCacheSeconds > 0;

    public bool TryGetPrincipal(
        Guid principalId, out PrincipalRecord? principal, out IReadOnlyList<string> directRoles)
    {
        principal = null;
        directRoles = [];
        if (!TryGetLive(principalId, out var entry))
            return false;
        principal = entry.Principal;
        directRoles = entry.DirectRoles;
        return true;
    }

    public void SetPrincipal(Guid principalId, PrincipalRecord? principal, IReadOnlyList<string> directRoles)
    {
        if (!Enabled)
            return;
        _entries[principalId] = new Entry
        {
            ExpiresUtc = clock.GetUtcNow().AddSeconds(settings.PrincipalCacheSeconds),
            Principal = principal,
            DirectRoles = directRoles
        };
    }

    public bool TryGetGroupRoles(Guid principalId, out IReadOnlyList<string> groupRoles)
    {
        groupRoles = [];
        if (!TryGetLive(principalId, out var entry) || entry.GroupRoles is not { } cached)
            return false;
        groupRoles = cached;
        return true;
    }

    public void SetGroupRoles(Guid principalId, IReadOnlyList<string> groupRoles)
    {
        if (TryGetLive(principalId, out var entry))
            entry.GroupRoles = groupRoles;
    }

    /// <summary>Call after any write affecting ONE principal (role grant/revoke, enable/disable).</summary>
    public void Invalidate(Guid principalId) => _entries.TryRemove(principalId, out _);

    /// <summary>Call after group-level writes — membership/role changes fan out to many principals.</summary>
    public void Clear() => _entries.Clear();

    private bool TryGetLive(Guid principalId, out Entry entry)
    {
        entry = null!;
        if (!Enabled || !_entries.TryGetValue(principalId, out var found))
            return false;
        if (found.ExpiresUtc <= clock.GetUtcNow())
        {
            _entries.TryRemove(principalId, out _);
            return false;
        }
        entry = found;
        return true;
    }
}
