namespace Auxilia.Slots.GitHubCopilot;

/// <summary>A GitHub token and the lifetime reported for it to the Copilot runtime.</summary>
public readonly record struct CopilotSessionToken(string Value, TimeSpan Lifetime);

/// <summary>
/// The GitHub token a Copilot session runs on, re-read from the slot's connector while the run
/// goes on — so a token its owner replaces, or the Core refreshes, reaches a running session
/// instead of the session dying when the token it started with expires. A re-read is a slot
/// activation (resolved by the Core for this run, encrypted for this instance, audited); reads
/// are at least <see cref="MinimumRecheck"/> apart.
/// <para>
/// The lifetime it reports drives when the runtime asks again: the runtime refreshes once less
/// than an hour remains (and rejects a token reported with less), so the reported lifetime is
/// the connector's declared remaining lifetime (<see cref="ExpiresAtKey"/>) capped at
/// <see cref="MaximumReportedLifetime"/> — re-read about hourly — and floored just above that
/// hour: a token inside its last hour is asked for again at the runtime's next operation,
/// picking up a replacement within a minute.
/// </para>
/// </summary>
public sealed class CopilotTokenSource
{
    /// <summary>Connector setting naming when the token expires (ISO 8601 or Unix time).</summary>
    public const string ExpiresAtKey = "token_expires_at";

    /// <summary>The Copilot runtime refreshes a token with less than this left — and rejects one reported with less.</summary>
    internal static readonly TimeSpan RuntimeRefreshThreshold = TimeSpan.FromHours(1);

    /// <summary>Reads closer together than this answer from the last one.</summary>
    internal static readonly TimeSpan MinimumRecheck = TimeSpan.FromMinutes(1);

    /// <summary>The longest lifetime reported, so even a long-lived token is re-read about hourly.</summary>
    internal static readonly TimeSpan MaximumReportedLifetime = TimeSpan.FromHours(2);

    /// <summary>The shortest lifetime reported: just above the runtime's threshold, by one re-check interval.</summary>
    internal static readonly TimeSpan MinimumReportedLifetime = RuntimeRefreshThreshold + MinimumRecheck;

    private readonly Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>>? _reread;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CopilotCliOptions _current;
    private DateTimeOffset _readAt;

    /// <param name="initial">The slot's configuration at activation.</param>
    /// <param name="reread">Fetches the slot's settings again; null = the token never changes (bundled configuration, tests).</param>
    /// <param name="time">Clock (tests).</param>
    public CopilotTokenSource(
        CopilotCliOptions initial,
        Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>>? reread = null,
        TimeProvider? time = null)
    {
        if (!initial.HasCredential)
            throw new InvalidOperationException("The github-copilot-cli provider requires a GitHub token with Copilot access.");
        _current = initial;
        _reread = reread;
        _time = time ?? TimeProvider.System;
        _readAt = _time.GetUtcNow();
    }

    /// <summary>The token the session should use now. Throws when the connector no longer holds one.</summary>
    public async Task<CopilotSessionToken> CurrentAsync(CancellationToken ct = default)
    {
        if (_reread is not null && _time.GetUtcNow() - _readAt >= MinimumRecheck)
        {
            await _gate.WaitAsync(ct);
            try
            {
                if (_time.GetUtcNow() - _readAt >= MinimumRecheck)
                    await RereadAsync(_reread, ct);
            }
            finally
            {
                _gate.Release();
            }
        }
        return new CopilotSessionToken(_current.Token!, ReportedLifetime(_current));
    }

    private async Task RereadAsync(
        Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>> reread, CancellationToken ct)
    {
        IReadOnlyDictionary<string, string> settings;
        try
        {
            settings = await reread(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The platform did not answer this time (a broker hiccup): keep the token we have —
            // it is still the owner's — and try again after the next interval.
            _readAt = _time.GetUtcNow();
            return;
        }
        var latest = CopilotCliOptions.FromSettings(settings);
        _readAt = _time.GetUtcNow();
        // The owner withdrew the credential: the session must not go on with the old one.
        if (!latest.HasCredential)
            throw new InvalidOperationException("The slot's connector no longer holds a GitHub token — the session cannot continue.");
        _current = _current with { Token = latest.Token, TokenExpiresAt = latest.TokenExpiresAt };
    }

    private TimeSpan ReportedLifetime(CopilotCliOptions options)
    {
        var remaining = options.TokenExpiresAt is { } expiresAt
            ? expiresAt - _time.GetUtcNow()
            : MaximumReportedLifetime;
        return remaining < MinimumReportedLifetime ? MinimumReportedLifetime
            : remaining > MaximumReportedLifetime ? MaximumReportedLifetime
            : remaining;
    }
}
