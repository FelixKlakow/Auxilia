using System.Security.Cryptography;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess;
using Auxilia.UniversalDataAccess.Implementations;

namespace Auxilia.Governance.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class AccountBundleStoreTests
{
    private const string Actor = "admin-principal";

    private IDataAccess<AccountBundleRecord> _bundles = null!;
    private IDataAccess<AuditRecord> _auditRecords = null!;
    private AccountBundleStore _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _bundles = new InMemoryDataAccess<AccountBundleRecord>();
        _auditRecords = new InMemoryDataAccess<AuditRecord>();
        _sut = new AccountBundleStore(
            _bundles,
            new AesGcmSettingsProtector(RandomNumberGenerator.GetBytes(32)),
            new AuditLog(_auditRecords, TimeProvider.System));
    }

    [TearDown]
    public void TearDown()
    {
        (_bundles as IDisposable)?.Dispose();
        (_auditRecords as IDisposable)?.Dispose();
    }

    private Task<AccountBundleSummary> CreateBundleAsync(string name = "github-bot")
        => _sut.CreateAsync(Actor, name, "SourceControl", Guid.NewGuid(),
            new Dictionary<string, string> { ["pat"] = "super-secret-token" });

    [Test]
    public async Task Create_ProtectsSecretsAtRest()
    {
        var summary = await CreateBundleAsync();

        var record = (await _bundles.ReadAsync(summary.Id))!;
        Assert.Multiple(() =>
        {
            Assert.That(record.ProtectedSecretsJson, Does.StartWith("enc1:"));
            Assert.That(record.ProtectedSecretsJson, Does.Not.Contain("super-secret-token"));
        });
    }

    [Test]
    public async Task Create_SummaryExposesKeyNamesButNeverValues()
    {
        var summary = await CreateBundleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(summary.SecretKeys, Is.EqualTo(new[] { "pat" }));
            Assert.That(summary.ToString(), Does.Not.Contain("super-secret-token"));
        });
    }

    [Test]
    public async Task Create_AuditsWithoutSecretMaterial()
    {
        await CreateBundleAsync();

        var entry = (await _auditRecords.ReadAsync()).Single(a => a.Action == "bundle.created");
        Assert.Multiple(() =>
        {
            Assert.That(entry.Actor, Is.EqualTo(Actor));
            Assert.That(entry.DetailJson, Does.Contain("pat"));
            Assert.That(entry.DetailJson, Does.Not.Contain("super-secret-token"));
        });
    }

    [TestCase("", "SourceControl")]
    [TestCase("name", " ")]
    public void Create_MissingNameOrType_IsRejected(string name, string bundleType)
        => Assert.That(
            () => _sut.CreateAsync(Actor, name, bundleType, Guid.NewGuid(), new Dictionary<string, string>()),
            Throws.ArgumentException);

    [Test]
    public async Task UpdateSecrets_AddsReplacesAndRemovesKeys()
    {
        var created = await CreateBundleAsync();

        var updated = await _sut.UpdateSecretsAsync(Actor, created.Id,
            new Dictionary<string, string> { ["username"] = "bot" }, ["pat"]);

        Assert.Multiple(async () =>
        {
            Assert.That(updated.SecretKeys, Is.EqualTo(new[] { "username" }));
            var audit = await _auditRecords.ReadAsync();
            var entry = audit.Single(a => a.Action == "bundle.updated");
            Assert.That(entry.DetailJson, Does.Not.Contain("bot"));
        });
    }

    [Test]
    public void UpdateSecrets_UnknownBundle_Throws()
        => Assert.That(
            () => _sut.UpdateSecretsAsync(Actor, Guid.NewGuid(), new Dictionary<string, string>(), []),
            Throws.InvalidOperationException);

    [Test]
    public async Task Delete_RemovesBundleAndAudits()
    {
        var created = await CreateBundleAsync();

        var removed = await _sut.DeleteAsync(Actor, created.Id);

        Assert.Multiple(async () =>
        {
            Assert.That(removed, Is.True);
            Assert.That(await _bundles.ReadAsync(created.Id), Is.Null);
            Assert.That(
                (await _auditRecords.ReadAsync()).Count(a => a.Action == "bundle.deleted"),
                Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Delete_UnknownBundle_ReturnsFalse()
        => Assert.That(await _sut.DeleteAsync(Actor, Guid.NewGuid()), Is.False);

    [Test]
    public async Task List_ReturnsBundlesOrderedByName()
    {
        await CreateBundleAsync("zeta");
        await CreateBundleAsync("alpha");

        var list = await _sut.ListAsync();

        Assert.That(list.Select(b => b.Name), Is.EqualTo(new[] { "alpha", "zeta" }));
    }
}
