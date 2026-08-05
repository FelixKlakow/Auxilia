using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Auxilia.Core.Contracts;
using Auxilia.Core.Runner.Workflows;
using Moq;

namespace Auxilia.Core.Runner.Tests.Workflows;

/// <summary>
/// The resolver presents a fresh public key to the Core and decrypts the returned settings. These
/// tests drive the real keygen + decrypt by encrypting exactly as the Core's SlotCredentialResolver
/// does (RSA-OAEP-SHA256 over the settings JSON) with the public key the resolver hands the client.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class RepositoryAuthResolverTests
{
    private static string Encrypt(string publicKeyBase64, Dictionary<string, string> settings)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
        var cipher = rsa.Encrypt(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(settings)), RSAEncryptionPadding.OaepSHA256);
        return Convert.ToBase64String(cipher);
    }

    private static ICoreCredentialClient ClientReturning(Dictionary<string, string>? settings)
    {
        var mock = new Mock<ICoreCredentialClient>();
        mock.Setup(c => c.ResolveAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, string _, string _, string publicKey, CancellationToken _) =>
                Task.FromResult<ResolvedSlotCredential?>(settings is null
                    ? null
                    : new ResolvedSlotCredential("git-auth", Encrypt(publicKey, settings), DateTimeOffset.MaxValue)));
        return mock.Object;
    }

    [Test]
    public async Task Resolve_DecryptsTheConnectorSettings_AndReturnsUsernameAndToken()
    {
        var resolver = new RepositoryAuthResolver(
            ClientReturning(new Dictionary<string, string> { ["username"] = "u", ["token"] = "PAT" }));

        var auth = await resolver.ResolveAsync(Guid.NewGuid(), "tok", "repo-auth:main", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(auth!.Username, Is.EqualTo("u"));
            Assert.That(auth.Token, Is.EqualTo("PAT"));
        });
    }

    [Test]
    public async Task Resolve_TokenWithoutUsername_ReturnsTokenOnly()
    {
        var resolver = new RepositoryAuthResolver(
            ClientReturning(new Dictionary<string, string> { ["pat"] = "PAT" }));

        var auth = await resolver.ResolveAsync(Guid.NewGuid(), "tok", "repo-auth:main", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(auth!.Token, Is.EqualTo("PAT"));
            Assert.That(auth.Username, Is.Null);
        });
    }

    [Test]
    public async Task Resolve_PushToken_RidesTheAuth_WhenTheConnectorCarriesOne()
    {
        var resolver = new RepositoryAuthResolver(ClientReturning(new Dictionary<string, string>
        {
            ["token"] = "FULL", ["Push-Token"] = "PUSH-ONLY"
        }));

        var auth = await resolver.ResolveAsync(Guid.NewGuid(), "tok", "repo-auth:main", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(auth!.Token, Is.EqualTo("FULL"));
            Assert.That(auth.PushToken, Is.EqualTo("PUSH-ONLY"), "setting keys match case-insensitively");
        });
    }

    [Test]
    public async Task Resolve_WithoutPushToken_LeavesItNull()
    {
        var resolver = new RepositoryAuthResolver(
            ClientReturning(new Dictionary<string, string> { ["token"] = "PAT" }));

        var auth = await resolver.ResolveAsync(Guid.NewGuid(), "tok", "repo-auth:main", CancellationToken.None);

        Assert.That(auth!.PushToken, Is.Null);
    }

    [Test]
    public async Task Resolve_WhenCoreReturnsNothing_YieldsNull()
        => Assert.That(
            await new RepositoryAuthResolver(ClientReturning(null))
                .ResolveAsync(Guid.NewGuid(), "tok", "s", CancellationToken.None),
            Is.Null);

    [Test]
    public async Task Resolve_WhenSettingsCarryNoToken_YieldsNull()
        => Assert.That(
            await new RepositoryAuthResolver(ClientReturning(new Dictionary<string, string> { ["url"] = "x" }))
                .ResolveAsync(Guid.NewGuid(), "tok", "s", CancellationToken.None),
            Is.Null);
}
