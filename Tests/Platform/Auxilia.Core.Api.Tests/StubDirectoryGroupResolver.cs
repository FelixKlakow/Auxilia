using Auxilia.Core.Api.Auth;

namespace Auxilia.Core.Api.Tests;

/// <summary>
/// Stands in for the Microsoft Graph overage lookup in component tests: returns a fixed set of group
/// ids for any subject, so the <c>/auth/callback</c> overage path (detect → fetch → map to roles)
/// runs without a live tenant or Graph call.
/// </summary>
public sealed class StubDirectoryGroupResolver(IReadOnlyList<string> groupIds) : IDirectoryGroupResolver
{
    public Task<IReadOnlyList<string>> GetGroupIdsAsync(string subject, string? accessToken, CancellationToken ct = default)
        => Task.FromResult(groupIds);
}
