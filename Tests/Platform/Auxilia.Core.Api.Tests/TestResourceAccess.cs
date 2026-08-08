using Auxilia.Governance;
using Auxilia.Governance.Policy;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.UniversalDataAccess.Implementations;

namespace Auxilia.Core.Api.Tests;

/// <summary>A fixed default-access posture for tests.</summary>
internal sealed class StubDefaultResourceAccess(bool restricted) : IDefaultResourceAccessPolicy
{
    public Task<bool> IsRestrictedAsync(CancellationToken ct) => Task.FromResult(restricted);
}

internal static class TestResourceAccess
{
    /// <summary>The pre-hardening posture: ungranted resources are open to everyone.</summary>
    public static IDefaultResourceAccessPolicy Open { get; } = new StubDefaultResourceAccess(false);

    /// <summary>The default posture: ungranted resources are administrators-only.</summary>
    public static IDefaultResourceAccessPolicy Restricted { get; } = new StubDefaultResourceAccess(true);

    /// <summary>An empty principal directory (no principal is an administrator).</summary>
    public static PrincipalDirectory EmptyPrincipalDirectory() => new(
        new InMemoryDataAccess<PrincipalRecord>(),
        new InMemoryDataAccess<RoleAssignmentRecord>(),
        new InMemoryDataAccess<CredentialRecord>(),
        new AuditLog(new InMemoryDataAccess<AuditRecord>(), TimeProvider.System));
}
