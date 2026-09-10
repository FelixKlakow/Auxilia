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

    private sealed record Fixture(
        SlotCredentialResolver Resolver,
        ConnectorService Connectors,
        DelegatedTokenStore Tokens,
        InMemoryDataAccess<CoreRunRecord> Runs,
        InMemoryDataAccess<AuditRecord> Audit,
        ProviderCatalogService Catalog,
        SlotBindingSecrets Secrets);

    private static (SlotCredentialResolver Resolver, ConnectorService Connectors, DelegatedTokenStore Tokens) New(
        IDelegatedTokenExchange? exchange = null)
    {
        var fixture = NewFixture(exchange);
        return (fixture.Resolver, fixture.Connectors, fixture.Tokens);
    }

    private static Fixture NewFixture(IDelegatedTokenExchange? exchange = null)
    {
        var protector = new AesGcmSettingsProtector(RandomNumberGenerator.GetBytes(32));
        var grants = new AccessGrantEvaluator(
            new InMemoryDataAccess<PrincipalRecord>(), new InMemoryDataAccess<GroupMembershipRecord>());
        var connectors = new ConnectorService(
            new InMemoryDataAccess<CoreConnectorRecord>(), protector, grants, TimeProvider.System);
        var tokens = new DelegatedTokenStore(
            new InMemoryDataAccess<DelegatedUserTokenRecord>(), protector, TimeProvider.System);
        var audit = new InMemoryDataAccess<AuditRecord>();
        var catalog = new ProviderCatalogService(
            new InMemoryDataAccess<SlotProviderRecord>(),
            new InMemoryDataAccess<ProviderCatalogRecord>(),
            new AuditLog(new InMemoryDataAccess<AuditRecord>(), TimeProvider.System));
        var secrets = new SlotBindingSecrets(
            catalog, connectors,
            new WorkspaceResourceService(new InMemoryDataAccess<CoreWorkspaceRecord>(), grants, TimeProvider.System),
            protector);
        var runs = new InMemoryDataAccess<CoreRunRecord>();
        var resolver = new SlotCredentialResolver(
            new InMemoryDataAccess<CoreRunResolutionRecord>(), runs, connectors,
            NewPassthroughRefresher(connectors, catalog), tokens,
            exchange ?? new NullDelegatedTokenExchange(),
            secrets,
            new AuditLog(audit, TimeProvider.System),
            TimeProvider.System, Options.Create(new CoreApiSettings()));
        return new Fixture(resolver, connectors, tokens, runs, audit, catalog, secrets);
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
            new List<SlotBinding> { new("sc", "github", connector.Id) }, triggeredBy: null, ct: CancellationToken.None);

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
            triggeredBy: null, ct: CancellationToken.None);

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
            triggeredBy: null, ct: CancellationToken.None);

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
        var fixture = NewFixture();
        var resolver = fixture.Resolver;
        var auditStore = fixture.Audit;

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
            triggeredBy: null, ct: CancellationToken.None);

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
            triggeredBy: principalId, ct: CancellationToken.None);

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
            triggeredBy: Guid.NewGuid(), ct: CancellationToken.None);

        var (publicKey, rsa) = NewKeyPair();
        using (rsa)
        {
            var (success, error, _) = await resolver.ResolveAsync(
                runId, token, "sc", publicKey, CancellationToken.None);

            Assert.That(success, Is.False);
            Assert.That(error, Is.EqualTo("delegated access requires a recent interactive sign-in"));
        }
    }

    private static Task RegisterSecretProviderAsync(ProviderCatalogService catalog)
        => catalog.RegisterAsync("test", new RegisterSlotProvider(
            "local-agent", "coding-agent", null, ["ICodingAgent"],
            [
                new RegisterProviderSetting("apiKey", "API key", "Secret", Required: true),
                new RegisterProviderSetting("model", "Model", "Text"),
            ]), CancellationToken.None);

    [Test]
    public async Task Resolve_AfterTheRunEnded_IsRefused_AndAudited()
    {
        var fixture = NewFixture();
        var runId = Guid.NewGuid();
        var token = Guid.NewGuid().ToString("N");
        await fixture.Resolver.StashAsync(runId, token,
            new List<SlotBinding> { new("sc", "local", Settings: new Dictionary<string, string> { ["path"] = "/x" }) },
            triggeredBy: null, ct: CancellationToken.None);
        // The runner's claim rekeyed the row to its instance id; the dispatch id survives as the alias.
        await fixture.Runs.SaveAsync(new CoreRunRecord
        {
            Id = Guid.NewGuid(), WorkflowType = "wt", State = RunStates.Success, CommandId = runId
        });

        var (publicKey, rsa) = NewKeyPair();
        using (rsa)
        {
            var (success, error, _) = await fixture.Resolver.ResolveAsync(
                runId, token, "sc", publicKey, CancellationToken.None);

            Assert.That(success, Is.False,
                "the stash is retained for rerun, but the token authorizes a LIVE run only");
            Assert.That(error, Is.EqualTo(SlotCredentialResolver.RunEndedError));
        }
        Assert.That((await fixture.Audit.ReadAsync()).Any(r =>
                r.Action == "workflow.slot-credential.rejected" && r.Outcome == "run-ended"),
            Is.True, "the refusal is audited like every other rejection");
        Assert.That((await fixture.Resolver.AuthorizeAsync(runId, token, CancellationToken.None)).Ok, Is.False,
            "the shared gate (package / layer downloads) refuses the same token");
    }

    [Test]
    public async Task Resolve_WhileTheRunIsActive_StillResolves()
    {
        var fixture = NewFixture();
        var runId = Guid.NewGuid();
        var token = Guid.NewGuid().ToString("N");
        await fixture.Resolver.StashAsync(runId, token,
            new List<SlotBinding> { new("sc", "local", Settings: new Dictionary<string, string> { ["path"] = "/x" }) },
            triggeredBy: null, ct: CancellationToken.None);
        await fixture.Runs.SaveAsync(new CoreRunRecord
        {
            Id = runId, WorkflowType = "wt", State = RunStates.Running, CommandId = runId
        });

        var (publicKey, rsa) = NewKeyPair();
        using (rsa)
        {
            var (success, error, _) = await fixture.Resolver.ResolveAsync(
                runId, token, "sc", publicKey, CancellationToken.None);
            Assert.That(success, Is.True, error);
        }
    }

    [Test]
    public async Task Resolve_InlineSecretSetting_IsDecryptedOnlyIntoTheEnvelope()
    {
        var fixture = NewFixture();
        await RegisterSecretProviderAsync(fixture.Catalog);
        var runId = Guid.NewGuid();
        var token = Guid.NewGuid().ToString("N");
        // The binding enters the Core protected (as a configuration write / inline run does it).
        var stashed = await fixture.Secrets.ProtectAsync(
            [new SlotBinding("sc", "local-agent", Settings: new Dictionary<string, string>
            {
                ["apiKey"] = "sk-plain", ["model"] = "m1"
            })], CancellationToken.None);
        Assert.That(stashed[0].Settings!["apiKey"], Is.Not.EqualTo("sk-plain"), "protected at rest");
        await fixture.Resolver.StashAsync(runId, token, stashed, triggeredBy: null, ct: CancellationToken.None);

        var (publicKey, rsa) = NewKeyPair();
        using (rsa)
        {
            var (success, error, credential) = await fixture.Resolver.ResolveAsync(
                runId, token, "sc", publicKey, CancellationToken.None);

            Assert.That(success, Is.True, error);
            var delivered = Decrypt(rsa, credential!.EncryptedSettings);
            Assert.That(delivered["apiKey"], Is.EqualTo("sk-plain"), "the resolver is the one place a secret is unprotected");
            Assert.That(delivered["model"], Is.EqualTo("m1"), "non-secret settings pass through");
        }
    }

    private static ConnectorTokenRefresher NewPassthroughRefresher(
        ConnectorService connectors, ProviderCatalogService catalog)
        => new(
            connectors, catalog,
            new StubHttpClientFactory(new StubHttpMessageHandler(
                _ => throw new InvalidOperationException("no refresh expected"))),
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ConnectorTokenRefresher>.Instance);
}
