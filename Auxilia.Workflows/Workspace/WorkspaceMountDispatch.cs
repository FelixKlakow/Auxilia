namespace Auxilia.Workflows.Workspace;

/// <summary>
/// One workspace mount of a dispatched run — the generic wire shape between the control plane and
/// the execution plane. The Core builds it from a slot binding of a provider that declares
/// <c>MountsIntoWorkspace</c>, mapping the binding's settings to the provider's declared setting
/// ROLES as pure data; only the execution plane interprets the role vocabulary (e.g. what to clone
/// and how). <see cref="AuthSlotName"/> names the synthetic slot the mount's credential connector is
/// stashed under for just-in-time resolution — no secret rides this record.
/// </summary>
public sealed record WorkspaceMountDispatch(
    string MountId,
    string ProviderType,
    IReadOnlyDictionary<string, string> SettingsByRole,
    string? AuthSlotName = null);
