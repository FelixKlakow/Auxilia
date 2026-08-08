using Auxilia.Governance.Policy;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Binds the governance default-access posture to the runtime platform setting
/// <c>security.default-resource-access</c> (unset = restricted).
/// </summary>
public sealed class PlatformDefaultResourceAccessPolicy(PlatformSettingsService settings)
    : IDefaultResourceAccessPolicy
{
    public Task<bool> IsRestrictedAsync(CancellationToken ct)
        => settings.IsDefaultResourceAccessRestrictedAsync(ct);
}
