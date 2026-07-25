using Auxilia.Core.Contracts;
using Auxilia.Core.Runner.Workflows;

namespace Auxilia.Core.Runner.Tests.ComponentTests;

/// <summary>
/// Component-test stub for <see cref="ICoreCredentialClient"/>: returns no credential, so the
/// runner resolves slots from its local stores (these hosts set no Core base address).
/// </summary>
public sealed class FakeCoreCredentialClient : ICoreCredentialClient
{
    public Task<ResolvedSlotCredential?> ResolveAsync(
        Guid coreRunId, string resolutionToken, string slotName, string publicKey, CancellationToken ct)
        => Task.FromResult<ResolvedSlotCredential?>(null);
}
