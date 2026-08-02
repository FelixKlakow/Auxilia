using System.Security.Cryptography;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Api.Services;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess;
using Auxilia.UniversalDataAccess.Implementations;

namespace Auxilia.Core.Api.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public sealed class DelegatedTokenStoreTests
{
    private static DelegatedTokenStore NewStore(out IDataAccess<DelegatedUserTokenRecord> backing)
    {
        backing = new InMemoryDataAccess<DelegatedUserTokenRecord>();
        return new DelegatedTokenStore(
            backing, new AesGcmSettingsProtector(RandomNumberGenerator.GetBytes(32)), TimeProvider.System);
    }

    [Test]
    public async Task Retain_ThenGet_RoundTripsTheToken()
    {
        var store = NewStore(out _);
        var principalId = Guid.NewGuid();
        await store.RetainAsync(principalId, "user-token", DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);

        Assert.That(await store.GetAsync(principalId, CancellationToken.None), Is.EqualTo("user-token"));
    }

    [Test]
    public async Task Get_ReturnsNull_WhenExpired()
    {
        var store = NewStore(out _);
        var principalId = Guid.NewGuid();
        await store.RetainAsync(principalId, "user-token", DateTimeOffset.UtcNow.AddSeconds(-1), CancellationToken.None);

        Assert.That(await store.GetAsync(principalId, CancellationToken.None), Is.Null,
            "An expired retained token must be treated as absent (session-lifetime delegation).");
    }

    [Test]
    public async Task Get_ReturnsNull_WhenAbsent()
        => Assert.That(await NewStore(out _).GetAsync(Guid.NewGuid(), CancellationToken.None), Is.Null);

    [Test]
    public async Task Retain_StoresTheTokenEncrypted_NotPlaintext()
    {
        var store = NewStore(out var backing);
        var principalId = Guid.NewGuid();
        await store.RetainAsync(principalId, "super-secret-token", DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);

        var record = await backing.ReadAsync(principalId, CancellationToken.None);
        Assert.That(record!.ProtectedAccessToken, Does.Not.Contain("super-secret-token"),
            "Retained user tokens must be encrypted at rest.");
    }
}
