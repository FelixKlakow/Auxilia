namespace Auxilia.Governance.Policy;

/// <summary>
/// Supplies the platform's default-resource-access posture: restricted means a grantable
/// resource that carries NO explicit grants (e.g. a workflow type with no access list) is
/// usable by administrators only. The host binds this to its runtime settings store; when no
/// implementation is registered the posture reads as restricted — the maximum-security default.
/// </summary>
public interface IDefaultResourceAccessPolicy
{
    Task<bool> IsRestrictedAsync(CancellationToken ct);
}

/// <summary>
/// The open posture, for hosts that are NOT the resource-access authority: the Core.Api owns the
/// runtime setting and gates resource use at dispatch; a re-checking host (e.g. the runner's
/// pre-flight) evaluates role permissions only and must not re-apply the restricted default.
/// </summary>
public sealed class OpenDefaultResourceAccessPolicy : IDefaultResourceAccessPolicy
{
    public Task<bool> IsRestrictedAsync(CancellationToken ct) => Task.FromResult(false);
}
