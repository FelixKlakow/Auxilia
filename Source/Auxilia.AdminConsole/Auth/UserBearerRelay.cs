using Microsoft.AspNetCore.Components;
using Auxilia.Core.Contracts;

namespace Auxilia.AdminConsole.Auth;

/// <summary>
/// Relays the per-user Core bearer across the prerender → interactive-circuit boundary. The operator's
/// Core session cookie is only readable while an <c>HttpContext</c> is live (i.e. during prerender); the
/// interactive circuit runs over SignalR with no <c>HttpContext</c> and, in Blazor Server, in a fresh DI
/// scope. This relay lets the prerender-side token provider hand its freshly minted bearer to the
/// circuit-side provider so <see cref="Auxilia.Core.Client.ICoreClient"/> calls keep acting as the user
/// during interactive rendering — instead of silently falling back to the static app key.
/// </summary>
public interface IUserBearerRelay
{
    /// <summary>Registers a callback pulled at persist time (end of prerender) to snapshot the current token.</summary>
    void OnPersist(Func<UserBearerToken?> snapshot);

    /// <summary>The token relayed from prerender on the circuit side, or null when there is nothing to relay.</summary>
    UserBearerToken? TryTake();
}

/// <summary>
/// <see cref="IUserBearerRelay"/> over <see cref="PersistentComponentState"/>: persists the minted bearer
/// at the end of prerender and restores it once the circuit starts. The relayed token is short-lived and
/// belongs to the very user the page is rendering for; it never outlives its <c>ExpiresUtc</c>.
/// </summary>
public sealed class PersistentUserBearerRelay : IUserBearerRelay, IDisposable
{
    private const string StateKey = "auxilia.console.userbearer";

    private readonly PersistentComponentState _state;
    private PersistingComponentStateSubscription? _subscription;
    private Func<UserBearerToken?>? _snapshot;

    public PersistentUserBearerRelay(PersistentComponentState state) => _state = state;

    public void OnPersist(Func<UserBearerToken?> snapshot)
    {
        // Register once per scope; the last snapshot delegate wins (there is a single provider per scope).
        _snapshot = snapshot;
        _subscription ??= _state.RegisterOnPersisting(() =>
        {
            if (_snapshot?.Invoke() is { } token)
                _state.PersistAsJson(StateKey, token);
            return Task.CompletedTask;
        });
    }

    public UserBearerToken? TryTake()
        => _state.TryTakeFromJson<UserBearerToken>(StateKey, out var token) ? token : null;

    public void Dispose() => _subscription?.Dispose();
}
