using Auxilia.BackendService.Dashboard;
using Auxilia.BackendService.Dashboard.Connect;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess;
using Auxilia.UniversalDataAccess.Implementations;

namespace Auxilia.BackendService.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class ConnectorServiceTests
{
    private sealed class StubFlow : IConnectFlow
    {
        public string Key => "anthropic-claude";
        public string DisplayName => "Claude account";
        public string Instructions => "";
        public Task<ConnectStart> BeginAsync(CancellationToken ct = default)
            => Task.FromResult(new ConnectStart("https://example", "state"));
        public Task<string> CompleteAsync(string state, string? pastedCode, CancellationToken ct = default)
            => Task.FromResult("token");
    }

    private IDataAccess<ConnectorRecord> _records = null!;
    private IDataAccess<AuditRecord> _audit = null!;
    private NullSettingsProtector _protector = null!;
    private ConnectorService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _records = new InMemoryDataAccess<ConnectorRecord>();
        _audit = new InMemoryDataAccess<AuditRecord>();
        _protector = new NullSettingsProtector();
        _sut = new ConnectorService(
            _records, new ConnectFlowRegistry([new StubFlow()]),
            _protector, new AuditLog(_audit, TimeProvider.System), TimeProvider.System);
    }

    [TearDown]
    public void TearDown()
    {
        (_records as IDisposable)?.Dispose();
        (_audit as IDisposable)?.Dispose();
    }

    [Test]
    public async Task Save_ProtectsTheToken_AndAuditsWithoutIt()
    {
        var owner = Guid.NewGuid();

        var name = await _sut.SaveAsync(
            "tester", owner, existingName: null, "My Claude account",
            "anthropic-claude", "secret-token", SlotInstanceScope.Personal);

        var record = await _records.ReadAsync(ConnectorRecord.IdFor(name));
        Assert.Multiple(async () =>
        {
            Assert.That(record, Is.Not.Null);
            Assert.That(record!.ProtectedToken, Is.EqualTo(_protector.Protect("secret-token")));
            Assert.That(record.OwnerPrincipalId, Is.EqualTo(owner));
            var audits = (await _audit.ReadAsync()).ToList();
            Assert.That(audits, Has.Count.EqualTo(1));
            Assert.That(audits[0].DetailJson, Does.Not.Contain("secret-token"),
                "the credential must never reach the audit log");
        });
    }

    [Test]
    public void Save_NewConnectorWithoutToken_IsRefused()
        => Assert.ThrowsAsync<ArgumentException>(() => _sut.SaveAsync(
            "tester", Guid.NewGuid(), existingName: null, "Unfinished",
            "anthropic-claude", token: "", SlotInstanceScope.Personal));

    [Test]
    public void Save_UnknownFlow_IsRefused()
        => Assert.ThrowsAsync<ArgumentException>(() => _sut.SaveAsync(
            "tester", Guid.NewGuid(), existingName: null, "Mystery",
            "no-such-flow", "token", SlotInstanceScope.Personal));

    [Test]
    public async Task Save_UpdateWithEmptyToken_KeepsTheStoredCredential()
    {
        var name = await _sut.SaveAsync(
            "tester", Guid.NewGuid(), null, "My Claude account",
            "anthropic-claude", "original-token", SlotInstanceScope.Personal);

        await _sut.SaveAsync(
            "tester", null, name, "Renamed account",
            "anthropic-claude", token: "", SlotInstanceScope.Company);

        var record = await _records.ReadAsync(ConnectorRecord.IdFor(name));
        Assert.Multiple(() =>
        {
            Assert.That(record!.ProtectedToken, Is.EqualTo(_protector.Protect("original-token")));
            Assert.That(record.DisplayName, Is.EqualTo("Renamed account"));
            Assert.That(record.Scope, Is.EqualTo(SlotInstanceScope.Company));
        });
    }

    [Test]
    public async Task Accessible_OffersCompanyAndOwn_ButNotForeignPersonal()
    {
        var me = Guid.NewGuid();
        await _sut.SaveAsync("t", null, null, "Company bot", "anthropic-claude", "a", SlotInstanceScope.Company);
        await _sut.SaveAsync("t", me, null, "Mine", "anthropic-claude", "b", SlotInstanceScope.Personal);
        await _sut.SaveAsync("t", Guid.NewGuid(), null, "Foreign", "anthropic-claude", "c", SlotInstanceScope.Personal);

        var accessible = await _sut.AccessibleAsync(me, "anthropic-claude");

        Assert.That(accessible.Select(c => c.DisplayName), Is.EquivalentTo(new[] { "Company bot", "Mine" }));
    }

    [Test]
    public async Task ResolveToken_ChecksAccess()
    {
        var me = Guid.NewGuid();
        var name = await _sut.SaveAsync(
            "t", Guid.NewGuid(), null, "Foreign", "anthropic-claude", "secret", SlotInstanceScope.Personal);
        var id = ConnectorRecord.IdFor(name);

        Assert.ThrowsAsync<InvalidOperationException>(() => _sut.ResolveTokenAsync(id, me));
    }

    [Test]
    public void ReferenceRoundTrip_ParsesWhatItRenders()
    {
        var id = Guid.NewGuid();
        Assert.Multiple(() =>
        {
            Assert.That(ConnectorService.ReferencedId(ConnectorService.ReferenceFor(id)), Is.EqualTo(id));
            Assert.That(ConnectorService.ReferencedId("plain-token"), Is.Null);
            Assert.That(ConnectorService.ReferencedId(""), Is.Null);
            Assert.That(ConnectorService.ReferencedId(null), Is.Null);
        });
    }
}
