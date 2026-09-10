using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Auth;

/// <summary>
/// Per-account failed-attempt throttle for credential proofs — <c>POST /auth/login</c> (keyed by
/// username) and step-up (keyed by principal id, REST and MCP alike): after
/// <see cref="CoreSecuritySettings.LoginFailureLimitPerUsername"/> failures within the sliding
/// window, further attempts for that account are refused until the oldest failure ages out.
/// Complements the per-IP rate-limit policy — this one follows the ACCOUNT, so guessing that
/// rotates source IPs is still throttled. A successful proof clears the account's slate. The two
/// key spaces never collide (a principal key is namespaced), so a step-up brute force against a
/// leaked bearer is throttled exactly like password guessing at the login.
/// </summary>
public sealed class LoginAttemptThrottle(TimeProvider clock, IOptions<CoreSecuritySettings> settings)
{
    private static string KeyFor(Guid principalId) => "principal:" + principalId.ToString("D");

    /// <summary>Whether step-up attempts for <paramref name="principalId"/> are currently refused.</summary>
    public bool IsBlocked(Guid principalId) => IsBlocked(KeyFor(principalId));
    public void RecordFailure(Guid principalId) => RecordFailure(KeyFor(principalId));
    public void RecordSuccess(Guid principalId) => RecordSuccess(KeyFor(principalId));

    private readonly int _limit = Math.Max(1, settings.Value.LoginFailureLimitPerUsername);
    private readonly TimeSpan _window = TimeSpan.FromMinutes(Math.Max(1, settings.Value.LoginFailureWindowMinutes));
    private readonly Dictionary<string, Queue<DateTimeOffset>> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _sync = new();

    /// <summary>Whether attempts for <paramref name="username"/> are currently refused.</summary>
    public bool IsBlocked(string username)
    {
        lock (_sync)
        {
            return PruneLocked(username) is { } recent && recent.Count >= _limit;
        }
    }

    public void RecordFailure(string username)
    {
        lock (_sync)
        {
            SweepLocked();
            var recent = PruneLocked(username);
            if (recent is null)
                _failures[username] = recent = new Queue<DateTimeOffset>();
            recent.Enqueue(clock.GetUtcNow());
        }
    }

    public void RecordSuccess(string username)
    {
        lock (_sync)
        {
            _failures.Remove(username);
        }
    }

    /// <summary>Drops failures older than the window; removes (and returns null for) empty entries.</summary>
    private Queue<DateTimeOffset>? PruneLocked(string username)
    {
        if (!_failures.TryGetValue(username, out var recent))
            return null;

        var cutoff = clock.GetUtcNow() - _window;
        while (recent.Count > 0 && recent.Peek() <= cutoff)
            recent.Dequeue();
        if (recent.Count > 0)
            return recent;

        _failures.Remove(username);
        return null;
    }

    /// <summary>Bounds memory: once many usernames accumulate, drop every fully aged-out entry.</summary>
    private void SweepLocked()
    {
        if (_failures.Count < 1024)
            return;
        var cutoff = clock.GetUtcNow() - _window;
        foreach (var username in _failures.Where(e => e.Value.All(t => t <= cutoff)).Select(e => e.Key).ToList())
            _failures.Remove(username);
    }
}
