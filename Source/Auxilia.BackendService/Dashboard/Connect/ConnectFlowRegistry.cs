namespace Auxilia.BackendService.Dashboard.Connect;

/// <summary>
/// A begun connect attempt. Two shapes exist: paste-back flows send the user to
/// <see cref="AuthorizeUrl"/> and expect the shown code pasted back
/// (<see cref="PasteRequired"/>), device flows display <see cref="UserCode"/> for the user
/// to enter on the provider's page and finish by polling.
/// </summary>
public sealed record ConnectStart(
    string AuthorizeUrl,
    string State,
    string? UserCode = null,
    bool PasteRequired = false);

/// <summary>
/// One way to obtain a credential by signing in instead of pasting it. Flows are
/// DI-registered and referenced by key from a setting descriptor's <c>ConnectFlow</c> —
/// the editors render a "Connect…" affordance for any secret that names one.
/// </summary>
public interface IConnectFlow
{
    /// <summary>Key a <see cref="Auxilia.Workflows.SettingDescriptor.ConnectFlow"/> references.</summary>
    string Key { get; }

    string DisplayName { get; }

    /// <summary>One sentence telling the user what to do after opening the sign-in page.</summary>
    string Instructions { get; }

    Task<ConnectStart> BeginAsync(CancellationToken ct = default);

    /// <summary>
    /// Finishes the attempt and returns the credential value. Throws
    /// <see cref="ConnectPendingException"/> when the user has not approved yet — the
    /// attempt stays valid and finishing can simply be retried.
    /// </summary>
    Task<string> CompleteAsync(string state, string? pastedCode, CancellationToken ct = default);
}

/// <summary>The sign-in is not approved yet — retry finishing after the user approves.</summary>
public sealed class ConnectPendingException(string message) : Exception(message);

/// <summary>The connect flows this platform instance offers, assembled at runtime.</summary>
public sealed class ConnectFlowRegistry(IEnumerable<IConnectFlow> flows)
{
    private readonly IReadOnlyList<IConnectFlow> _flows = flows.ToList();

    /// <summary>Every registered flow — the account types the connectors page offers.</summary>
    public IReadOnlyList<IConnectFlow> All => _flows;

    public IConnectFlow? Find(string? key)
        => key is { Length: > 0 } ? _flows.FirstOrDefault(f => f.Key == key) : null;
}
