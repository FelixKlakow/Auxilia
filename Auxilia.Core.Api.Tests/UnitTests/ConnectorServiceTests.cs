using System.Security.Cryptography;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess.Implementations;

namespace Auxilia.Core.Api.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public sealed class ConnectorServiceTests
{
    private static ConnectorService New(out InMemoryDataAccess<CoreConnectorRecord> store)
    {
        store = new InMemoryDataAccess<CoreConnectorRecord>();
        var protector = new AesGcmSettingsProtector(RandomNumberGenerator.GetBytes(32));
        return new ConnectorService(store, protector, TimeProvider.System);
    }

    [Test]
    public async Task Create_StoresSettingsProtected_NotPlaintext()
    {
        var service = New(out var store);

        var dto = await service.CreateAsync(
            new CreateConnector("gh", "github",
                new Dictionary<string, string> { ["token"] = "secret-xyz" }),
            ownerPrincipalId: null, CancellationToken.None);

        Assert.That(dto.SettingKeys, Does.Contain("token"));
        var record = await store.ReadAsync(dto.Id, CancellationToken.None);
        Assert.That(record!.ProtectedSettingsJson, Does.Not.Contain("secret-xyz"),
            "Connector settings must be encrypted at rest.");
    }

    [Test]
    public async Task ResolveSettings_RoundTripsDecryptedValues()
    {
        var service = New(out _);

        var dto = await service.CreateAsync(
            new CreateConnector("gh", "github",
                new Dictionary<string, string> { ["token"] = "secret-xyz" }),
            ownerPrincipalId: null, CancellationToken.None);

        var resolved = await service.ResolveSettingsAsync(dto.Id, CancellationToken.None);
        Assert.That(resolved, Is.Not.Null);
        Assert.That(resolved!["token"], Is.EqualTo("secret-xyz"));
    }
}
