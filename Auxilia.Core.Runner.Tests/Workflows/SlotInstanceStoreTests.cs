using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.Core.Runner.Workflows.Storage;
using Auxilia.UniversalDataAccess.Implementations;

namespace Auxilia.Core.Runner.Tests.Workflows;

[TestFixture]
[Category("Unit")]
public class SlotInstanceStoreTests
{
    private InMemoryDataAccess<SlotInstanceRecord> _records = null!;
    private InMemoryDataAccess<AuditRecord> _auditRecords = null!;
    private SlotInstanceStore _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _records = new InMemoryDataAccess<SlotInstanceRecord>();
        _auditRecords = new InMemoryDataAccess<AuditRecord>();
        _sut = new SlotInstanceStore(
            _records, new NullSettingsProtector(),
            new AuditLog(_auditRecords, TimeProvider.System), TimeProvider.System);
    }

    [TearDown]
    public void TearDown()
    {
        _records.Dispose();
        _auditRecords.Dispose();
    }

    private static StoredSlotInstance AnyInstance(
        string name = "team-mailbox", string scope = SlotInstanceScope.Company,
        Guid? owner = null, IReadOnlyList<Guid>? assigned = null)
        => new(name, "Team mailbox", "email-work-items",
            new Dictionary<string, string> { ["Password"] = "pw-value" },
            scope, owner, assigned ?? []);

    [Test]
    public async Task Upsert_RoundTripsAllFields()
    {
        var owner = Guid.NewGuid();
        var assignee = Guid.NewGuid();

        await _sut.UpsertAsync(AnyInstance(scope: SlotInstanceScope.Personal, owner: owner, assigned: [assignee]));

        var stored = await _sut.GetAsync(SlotInstanceRecord.IdFor("team-mailbox"));
        Assert.Multiple(() =>
        {
            Assert.That(stored!.DisplayName, Is.EqualTo("Team mailbox"));
            Assert.That(stored.ProviderType, Is.EqualTo("email-work-items"));
            Assert.That(stored.Settings["Password"], Is.EqualTo("pw-value"));
            Assert.That(stored.Scope, Is.EqualTo(SlotInstanceScope.Personal));
            Assert.That(stored.OwnerPrincipalId, Is.EqualTo(owner));
            Assert.That(stored.AssignedPrincipalIds, Is.EqualTo(new[] { assignee }));
        });
    }

    [Test]
    public async Task Upsert_WithRealProtector_SettingsAreNotPlaintextAtRest()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var sut = new SlotInstanceStore(
            _records, new AesGcmSettingsProtector(key),
            new AuditLog(_auditRecords, TimeProvider.System), TimeProvider.System);

        await sut.UpsertAsync(AnyInstance());

        var record = await _records.ReadAsync(SlotInstanceRecord.IdFor("team-mailbox"));
        Assert.That(record!.ProtectedSettingsJson, Does.Not.Contain("pw-value"));
        var stored = await sut.GetAsync(record.Id);
        Assert.That(stored!.Settings["Password"], Is.EqualTo("pw-value"));
    }

    [Test]
    public void Upsert_UnknownScope_IsRejected()
    {
        var exception = Assert.ThrowsAsync<ArgumentException>(
            () => _sut.UpsertAsync(AnyInstance(scope: "Team")));

        Assert.That(exception!.Message, Does.Contain("scope"));
    }

    [Test]
    public async Task Upsert_AuditsTopologyWithoutSettingsValues()
    {
        await _sut.UpsertAsync(AnyInstance());

        var audit = (await _auditRecords.ReadAsync()).Single(a => a.Action == "slot-instance.upserted");
        Assert.That(audit.DetailJson, Does.Not.Contain("pw-value"));
    }

    [Test]
    public async Task Remove_DeletesAndAudits()
    {
        await _sut.UpsertAsync(AnyInstance());

        Assert.That(await _sut.RemoveAsync("team-mailbox"), Is.True);
        Assert.Multiple(async () =>
        {
            Assert.That(await _sut.GetAsync(SlotInstanceRecord.IdFor("team-mailbox")), Is.Null);
            Assert.That((await _auditRecords.ReadAsync()).Any(a => a.Action == "slot-instance.removed"), Is.True);
        });
    }
}
