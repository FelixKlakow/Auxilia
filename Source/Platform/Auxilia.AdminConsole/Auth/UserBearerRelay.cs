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
/// <see cref="IUserBearerRelay"/> over <see cref="PersistentComponentState"/> + a server-side
/// <see cref="UserBearerHandleStore"/>: at the end of prerender the minted bearer is stashed
/// server-side and only a cryptographically random ONE-TIME handle is persisted into the page,
/// so the raw token never rides the prerendered HTML/state blob. The circuit side redeems the
/// handle (single use) to recover the token.
/// </summary>
public sealed class PersistentUserBearerRelay : IUserBearerRelay, IDisposable
{
    private const string StateKey = "auxilia.console.userbearer";

    private readonly PersistentComponentState _state;
    private readonly UserBearerHandleStore _store;
    private PersistingComponentStateSubscription? _subscription;
    private Func<UserBearerToken?>? _snapshot;

    public PersistentUserBearerRelay(PersistentComponentState state, UserBearerHandleStore store)
    {
        _state = state;
        _store = store;
    }

    public void OnPersist(Func<UserBearerToken?> snapshot)
    {
        // Register once per scope; the last snapshot delegate wins (there is a single provider per scope).
        _snapshot = snapshot;
        _subscription ??= _state.RegisterOnPersisting(() =>
        {
            if (_snapshot?.Invoke() is { } token)
                _state.PersistAsJson(StateKey, _store.Stash(token));
            return Task.CompletedTask;
        });
    }

    public UserBearerToken? TryTake()
        => _state.TryTakeFromJson<string>(StateKey, out var handle) ? _store.Redeem(handle) : null;

    public void Dispose() => _subscription?.Dispose();
}
