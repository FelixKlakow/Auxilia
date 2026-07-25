using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess.Implementations;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public sealed class SlotCredentialResolverTests
{
    private sealed class StubExchange(string? downstream) : IDelegatedTokenExchange
    {
        public Task<string?> ExchangeAsync(string userAccessToken, string resource, CancellationToken ct = default)
            => Task.FromResult(downstream);
    }

    private static (SlotCredentialResolver Resolver, ConnectorService Connectors, DelegatedTokenStore Tokens) New(
        IDelegatedTokenExchange? exchange = null)
    {
        var protector = new AesGcmSettingsProtector(RandomNumberGenerator.GetBytes(32));
        var connectors = new ConnectorService(
            new InMemoryDataAccess<CoreConnectorRecord>(), protector, TimeProvider.System);
        var tokens = new DelegatedTokenStore(
            new InMemoryDataAccess<DelegatedUserTokenRecord>(), protector, TimeProvider.System);
        var resolver = new SlotCredentialResolver(
            new InMemoryDataAccess<CoreRunResolutionRecord>(), connectors, tokens,
            exchange ?? new NullDelegatedTokenExchange(),
            new AuditLog(new InMemoryDataAccess<AuditRecord>(), TimeProvider.System),
            TimeProvider.System, Options.Create(new CoreApiSettings()));
        return (resolver, connectors, tokens);
    }

    private static (string PublicKey, RSA Rsa) NewKeyPair()
    {
        var rsa = RSA.Create(2048);
        return (Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()), rsa);
    }

    private static Dictionary<string, string> Decrypt(RSA rsa, string encryptedBase64)
    {
        var plain = rsa.Decrypt(Convert.FromBase64String(encryptedBase64), RSAEncryptionPadding.OaepSHA256);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(plain))!;
    }

    [Test]
    public async Task Resolve_ConnectorBackedSlot_ReturnsCiphertextDecryptingToConnectorSettings()
    {
        var (resolver, connectors, _) = New();
        var connector = await connectors.CreateAsync(
            new CreateConnector("gh", "github", new Dictionary<string, string> { ["token"] = "secret-xyz" }),
            ownerPrincipalId: null, CancellationToken.None);

        var runId = Guid.NewGuid();
        var token = Guid.NewGuid().ToString("N");
        await resolver.StashAsync(runId, token,
            new List<SlotBinding> { new("sc", "github", connector.Id) }, triggeredBy: null, CancellationToken.None);

        var (publicKey, rsa) = NewKeyPair();
        using (rsa)
        {
            var (success, error, credential) = await resolver.ResolveAsync(
                runId, token, "sc", publicKey, CancellationToken.None);

            Assert.That(success, Is.True, error);
            Assert.That(credential!.ProviderType, Is.EqualTo("github"));
            Assert.That(Decrypt(rsa, credential.EncryptedSettings)["token"], Is.EqualTo("secret-xyz"));
        }
    }

    [Test]
    public async Task Resolve_WrongToken_Fails()
    {
        var (resolver, _, _) = New();
        var runId = Guid.NewGuid();
        await resolver.StashAsync(runId, "the-real-token",
            new List<SlotBinding> { new("sc", "local", Settings: new Dictionary<string, string>()) },
            triggeredBy: null, CancellationToken.None);

        var (publicKey, rsa) = NewKeyPair();
        using (rsa)
        {
            var (success, error, _) = await resolver.ResolveAsync(
                runId, "a-different-token", "sc", publicKey, CancellationToken.None);

            Assert.That(success, Is.False);
            Assert.That(error, Is.EqualTo("invalid resolution token"));
        }
    }

    [Test]
    public async Task Resolve_UnknownSlot_Fails()
    {
        var (resolver, _, _) = New();
        var runId = Guid.NewGuid();
        var token = Guid.NewGuid().ToString("N");
        await resolver.StashAsync(runId, token,
            new List<SlotBinding> { new("sc", "local", Settings: new Dictionary<string, string>()) },
            triggeredBy: null, CancellationToken.None);

        var (publicKey, rsa) = NewKeyPair();
        using (rsa)
        {
            var (success, _, _) = await resolver.ResolveAsync(
                runId, token, "does-not-exist", publicKey, CancellationToken.None);
            Assert.That(success, Is.False);
        }
    }

    [Test]
    public async Task Resolve_UnknownRun_Fails()
    {
        var (resolver, _, _) = New();
        var (publicKey, rsa) = NewKeyPair();
        using (rsa)
        {
            var (success, error, _) = await resolver.ResolveAsync(
                Guid.NewGuid(), "tok", "sc", publicKey, CancellationToken.None);
            Assert.That(success, Is.False);
            Assert.That(error, Is.EqualTo("unknown run"));
        }
    }

    [Test]
    public async Task Resolve_Rejection_IsAudited()
    {
        var auditStore = new InMemoryDataAccess<AuditRecord>();
        var protector = new AesGcmSettingsProtector(RandomNumberGenerator.GetBytes(32));
        var connectors = new ConnectorService(
            new InMemoryDataAccess<CoreConnectorRecord>(), protector, TimeProvider.System);
        var tokens = new DelegatedTokenStore(
            new InMemoryDataAccess<DelegatedUserTokenRecord>(), protector, TimeProvider.System);
        var resolver = new SlotCredentialResolver(
            new InMemoryDataAccess<CoreRunResolutionRecord>(), connectors, tokens, new NullDelegatedTokenExchange(),
            new AuditLog(auditStore, TimeProvider.System), TimeProvider.System,
            Options.Create(new CoreApiSettings()));

        var (publicKey, rsa) = NewKeyPair();
        using (rsa)
        {
            // Unknown run — a rejection that must still be audited.
            await resolver.ResolveAsync(Guid.NewGuid(), "tok", "sc", publicKey, CancellationToken.None);
        }

        var records = await auditStore.ReadAsync();
        Assert.That(records.Any(r =>
                r.Action == "workflow.slot-credential.rejected" && r.Outcome == "unknown-run"),
            Is.True, "Every resolution rejection must be audited.");
    }

    [Test]
    public async Task Resolve_InlineBinding_UsesInlineSettings()
    {
        var (resolver, _, _) = New();
        var runId = Guid.NewGuid();
        var token = Guid.NewGuid().ToString("N");
        await resolver.StashAsync(runId, token,
            new List<SlotBinding> { new("sc", "local", Settings: new Dictionary<string, string> { ["path"] = "/repos/x" }) },
            triggeredBy: null, CancellationToken.None);

        var (publicKey, rsa) = NewKeyPair();
        using (rsa)
        {
            var (success, error, credential) = await resolver.ResolveAsync(
                runId, token, "sc", publicKey, CancellationToken.None);

            Assert.That(success, Is.True, error);
            Assert.That(credential!.ProviderType, Is.EqualTo("local"));
            Assert.That(Decrypt(rsa, credential.EncryptedSettings)["path"], Is.EqualTo("/repos/x"));
        }
    }

    [Test]
    public async Task Resolve_DelegatedSlot_ExchangesTheUsersTokenOnBehalfOf()
    {
        var principalId = Guid.NewGuid();
        var (resolver, _, tokens) = New(new StubExchange("downstream-ado-token"));
        await tokens.RetainAsync(principalId, "user-token", DateTimeOffset.MaxValue, CancellationToken.None);

        var runId = Guid.NewGuid();
        var token = Guid.NewGuid().ToString("N");
        await resolver.StashAsync(runId, token,
            new List<SlotBinding> { new("sc", "ado", DelegatedResource: "499b/.default") },
            triggeredBy: principalId, CancellationToken.None);

        var (publicKey, rsa) = NewKeyPair();
        using (rsa)
        {
            var (success, error, credential) = await resolver.ResolveAsync(
                runId, token, "sc", publicKey, CancellationToken.None);

            Assert.That(success, Is.True, error);
            Assert.That(credential!.ProviderType, Is.EqualTo("ado"));
            Assert.That(Decrypt(rsa, credential.EncryptedSettings)["accessToken"], Is.EqualTo("downstream-ado-token"),
                "The delegated slot delivers the OBO-exchanged token, minted just-in-time.");
        }
    }

    [Test]
    public async Task Resolve_DelegatedSlot_WithoutARetainedToken_Fails()
    {
        var (resolver, _, _) = New(new StubExchange("x"));
        var runId = Guid.NewGuid();
        var token = Guid.NewGuid().ToString("N");
        await resolver.StashAsync(runId, token,
            new List<SlotBinding> { new("sc", "ado", DelegatedResource: "r") },
            triggeredBy: Guid.NewGuid(), CancellationToken.None);

        var (publicKey, rsa) = NewKeyPair();
        using (rsa)
        {
            var (success, error, _) = await resolver.ResolveAsync(
                runId, token, "sc", publicKey, CancellationToken.None);

            Assert.That(success, Is.False);
            Assert.That(error, Is.EqualTo("delegated access requires a recent interactive sign-in"));
        }
    }
}
