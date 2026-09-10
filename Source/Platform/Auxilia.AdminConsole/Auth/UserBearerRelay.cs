using Microsoft.AspNetCore.Components;

namespace Auxilia.AdminConsole.Auth;

/// <summary>
/// Relays the operator's Core session (minted bearer + the session cookie it came from) across the
/// prerender → interactive-circuit boundary. The cookie is only readable while an <c>HttpContext</c> is
/// live (i.e. during prerender); the interactive circuit runs over SignalR with no <c>HttpContext</c>
/// and, in Blazor Server, in a fresh DI scope. This relay lets the prerender-side token provider hand
/// its session to the circuit-side provider so <see cref="Auxilia.Core.Client.ICoreClient"/> calls keep
/// acting as the user for the whole circuit — including re-minting the bearer when it expires.
/// </summary>
public interface IUserBearerRelay
{
    /// <summary>Registers a callback pulled at persist time (end of prerender) to snapshot the current session.</summary>
    void OnPersist(Func<RelayedUserSession?> snapshot);

    /// <summary>The session relayed from prerender on the circuit side, or null when there is nothing to relay.</summary>
    RelayedUserSession? TryTake();
}

/// <summary>
/// <see cref="IUserBearerRelay"/> over <see cref="PersistentComponentState"/> + a server-side
/// <see cref="UserBearerHandleStore"/>: at the end of prerender the session is stashed server-side
/// and only a cryptographically random ONE-TIME handle is persisted into the page, so neither the
/// raw token nor the cookie rides the prerendered HTML/state blob. The circuit side redeems the
/// handle (single use) to recover the session.
/// </summary>
public sealed class PersistentUserBearerRelay : IUserBearerRelay, IDisposable
{
    private const string StateKey = "auxilia.console.userbearer";

    private readonly PersistentComponentState _state;
    private readonly UserBearerHandleStore _store;
    private PersistingComponentStateSubscription? _subscription;
    private Func<RelayedUserSession?>? _snapshot;

    public PersistentUserBearerRelay(PersistentComponentState state, UserBearerHandleStore store)
    {
        _state = state;
        _store = store;
    }

    public void OnPersist(Func<RelayedUserSession?> snapshot)
    {
        // Register once per scope; the last snapshot delegate wins (there is a single provider per scope).
        _snapshot = snapshot;
        _subscription ??= _state.RegisterOnPersisting(() =>
        {
            if (_snapshot?.Invoke() is { } session)
                _state.PersistAsJson(StateKey, _store.Stash(session));
            return Task.CompletedTask;
        });
    }

    public RelayedUserSession? TryTake()
        => _state.TryTakeFromJson<string>(StateKey, out var handle) ? _store.Redeem(handle) : null;

    public void Dispose() => _subscription?.Dispose();
}
