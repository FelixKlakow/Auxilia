using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.Core.Runner.Workflows.Storage;
using Auxilia.UniversalDataAccess.Implementations;

namespace Auxilia.Core.Runner.Tests;

[TestFixture]
[Category("Unit")]
public class WorkflowConfigurationStoreTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 6, 12, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private ManualTimeProvider _time = null!;
    private InMemoryDataAccess<WorkflowConfigurationRecord> _records = null!;
    private InMemoryDataAccess<AuditRecord> _auditRecords = null!;
    private SlotInstanceStore _slotInstances = null!;
    private WorkflowConfigurationStore _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider();
        _records = new InMemoryDataAccess<WorkflowConfigurationRecord>();
        _auditRecords = new InMemoryDataAccess<AuditRecord>();
        _slotInstances = TestStores.NewSlotInstanceStore();
        _sut = new WorkflowConfigurationStore(
            _records, _slotInstances, new NullSettingsProtector(), new AuditLog(_auditRecords, _time), _time);
    }

    [TearDown]
    public void TearDown()
    {
        _records.Dispose();
        _auditRecords.Dispose();
    }

    private static StoredWorkflowConfiguration AnyConfiguration(
        string name = "alpha",
        bool enabled = true,
        IReadOnlyList<StoredSlotBinding>? bindings = null)
        => new(name, "Alpha", "test-wf", "docker://test-wf:1", enabled,
            bindings ?? [new StoredSlotBinding("repo", "git",
                new Dictionary<string, string> { ["ApiKey"] = "s3cret-value" })]);

    // ------------------------------------------------------------------ CRUD

    [Test]
    public async Task UpsertAsync_NewConfiguration_RoundTrips()
    {
        await _sut.UpsertAsync(AnyConfiguration());

        var stored = await _sut.GetByNameAsync("alpha");
        Assert.That(stored, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(stored!.Name, Is.EqualTo("alpha"));
            Assert.That(stored.DisplayName, Is.EqualTo("Alpha"));
            Assert.That(stored.WorkflowType, Is.EqualTo("test-wf"));
            Assert.That(stored.PackageUri, Is.EqualTo("docker://test-wf:1"));
            Assert.That(stored.Enabled, Is.True);
            Assert.That(stored.SlotBindings, Has.Count.EqualTo(1));
            Assert.That(stored.SlotBindings[0].SlotName, Is.EqualTo("repo"));
            Assert.That(stored.SlotBindings[0].ProviderType, Is.EqualTo("git"));
            Assert.That(stored.SlotBindings[0].Settings["ApiKey"], Is.EqualTo("s3cret-value"));
            Assert.That(stored.CreatedUtc, Is.EqualTo(_time.Now));
            Assert.That(stored.UpdatedUtc, Is.EqualTo(_time.Now));
        });
    }

    [Test]
    public async Task UpsertAsync_ExistingConfiguration_PreservesCreatedUtcAndAdvancesUpdatedUtc()
    {
        var created = _time.Now;
        await _sut.UpsertAsync(AnyConfiguration());

        _time.Now = created.AddHours(2);
        await _sut.UpsertAsync(AnyConfiguration(enabled: false));

        var stored = await _sut.GetByNameAsync("alpha");
        Assert.Multiple(() =>
        {
            Assert.That(stored!.Enabled, Is.False);
            Assert.That(stored.CreatedUtc, Is.EqualTo(created));
            Assert.That(stored.UpdatedUtc, Is.EqualTo(created.AddHours(2)));
        });
    }

    [Test]
    public async Task GetAsync_UnknownId_ReturnsNull()
        => Assert.That(await _sut.GetAsync(Guid.NewGuid()), Is.Null);

    [Test]
    public async Task RemoveAsync_RemovesConfiguration()
    {
        await _sut.UpsertAsync(AnyConfiguration());

        var removed = await _sut.RemoveAsync("alpha");

        Assert.That(removed, Is.True);
        Assert.That(await _sut.GetByNameAsync("alpha"), Is.Null);
    }

    [Test]
    public async Task GetAllAsync_ReturnsAllConfigurations()
    {
        await _sut.UpsertAsync(AnyConfiguration("alpha"));
        await _sut.UpsertAsync(AnyConfiguration("beta"));

        var all = await _sut.GetAllAsync();
        Assert.That(all.Select(c => c.Name), Is.EquivalentTo(new[] { "alpha", "beta" }));
    }

    // ------------------------------------------------------------------ Record ID determinism

    [Test]
    public async Task UpsertAsync_SameName_ConvergesOnOneRecordWithDeterministicId()
    {
        await _sut.UpsertAsync(AnyConfiguration());
        await _sut.UpsertAsync(AnyConfiguration());

        var query = await _records.ReadAsync();
        var records = query.ToList();
        Assert.That(records, Has.Count.EqualTo(1));
        Assert.That(records[0].Id, Is.EqualTo(WorkflowConfigurationRecord.IdFor("alpha")));
    }

    // ------------------------------------------------------------------ Validation

    [Test]
    public void UpsertAsync_EmptyName_Throws()
        => Assert.ThrowsAsync<ArgumentException>(
            () => _sut.UpsertAsync(AnyConfiguration(name: "  ")));

    [Test]
    public void UpsertAsync_EmptyWorkflowType_Throws()
        => Assert.ThrowsAsync<ArgumentException>(
            () => _sut.UpsertAsync(AnyConfiguration() with { WorkflowType = "" }));

    [Test]
    public void UpsertAsync_EmptyPackageUri_Throws()
        => Assert.ThrowsAsync<ArgumentException>(
            () => _sut.UpsertAsync(AnyConfiguration() with { PackageUri = " " }));

    [Test]
    public void UpsertAsync_BindingWithoutSlotName_Throws()
        => Assert.ThrowsAsync<ArgumentException>(
            () => _sut.UpsertAsync(AnyConfiguration(
                bindings: [new StoredSlotBinding("", "git", new Dictionary<string, string>())])));

    [Test]
    public void UpsertAsync_BindingWithoutProviderType_Throws()
        => Assert.ThrowsAsync<ArgumentException>(
            () => _sut.UpsertAsync(AnyConfiguration(
                bindings: [new StoredSlotBinding("repo", "", new Dictionary<string, string>())])));

    [Test]
    public void UpsertAsync_DuplicateSlotNames_Throws()
        => Assert.ThrowsAsync<ArgumentException>(
            () => _sut.UpsertAsync(AnyConfiguration(bindings:
            [
                new StoredSlotBinding("repo", "git", new Dictionary<string, string>()),
                new StoredSlotBinding("repo", "svn", new Dictionary<string, string>())
            ])));

    [Test]
    public async Task UpsertAsync_InvalidConfiguration_IsNotAudited()
    {
        Assert.ThrowsAsync<ArgumentException>(() => _sut.UpsertAsync(AnyConfiguration(name: "")));

        var query = await _auditRecords.ReadAsync();
        Assert.That(query.ToList(), Is.Empty);
    }

    // ------------------------------------------------------------------ Audit

    [Test]
    public async Task UpsertAsync_AuditsMutation_WithoutSettingsValues()
    {
        await _sut.UpsertAsync(AnyConfiguration());

        var query = await _auditRecords.ReadAsync();
        var entries = query.Where(r => r.Action == "workflow-configuration.upserted").ToList();
        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(entries[0].Subject, Is.EqualTo("alpha"));
            Assert.That(entries[0].Outcome, Is.EqualTo("created"));
            Assert.That(entries[0].DetailJson, Does.Contain("git"));
            Assert.That(entries[0].DetailJson, Does.Not.Contain("s3cret-value"),
                "Settings values must never appear in the audit log.");
        });
    }

    [Test]
    public async Task UpsertAsync_SecondTime_AuditsAsUpdated()
    {
        await _sut.UpsertAsync(AnyConfiguration());
        await _sut.UpsertAsync(AnyConfiguration());

        var query = await _auditRecords.ReadAsync();
        var outcomes = query
            .Where(r => r.Action == "workflow-configuration.upserted")
            .Select(r => r.Outcome).ToList();
        Assert.That(outcomes, Is.EqualTo(new[] { "created", "updated" }));
    }

    [Test]
    public async Task RemoveAsync_AuditsRemoval()
    {
        await _sut.UpsertAsync(AnyConfiguration());
        await _sut.RemoveAsync("alpha");

        var query = await _auditRecords.ReadAsync();
        var entries = query.Where(r => r.Action == "workflow-configuration.removed").ToList();
        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].Subject, Is.EqualTo("alpha"));
    }

    [Test]
    public async Task RemoveAsync_UnknownName_DoesNotAudit()
    {
        var removed = await _sut.RemoveAsync("ghost");

        Assert.That(removed, Is.False);
        var query = await _auditRecords.ReadAsync();
        Assert.That(query.ToList(), Is.Empty);
    }

    // ------------------------------------------------------------------ Encryption at rest

    [Test]
    public async Task UpsertAsync_WithRealProtector_SettingsAreNotPlaintextAtRest()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var sut = new WorkflowConfigurationStore(
            _records, _slotInstances, new AesGcmSettingsProtector(key), new AuditLog(_auditRecords, _time), _time);

        await sut.UpsertAsync(AnyConfiguration());

        var record = await _records.ReadAsync(WorkflowConfigurationRecord.IdFor("alpha"));
        Assert.That(record!.SlotBindingsJson, Does.Not.Contain("s3cret-value"),
            "Binding settings must be protected at rest.");

        // And the same store decrypts them back.
        var stored = await sut.GetByNameAsync("alpha");
        Assert.That(stored!.SlotBindings[0].Settings["ApiKey"], Is.EqualTo("s3cret-value"));
    }

    // ------------------------------------------------------------------ Slot-instance bindings

    [Test]
    public async Task UpsertAsync_InstanceBackedBinding_ResolvesProviderAndSettingsAtReadTime()
    {
        var instance = await _slotInstances.UpsertAsync(new StoredSlotInstance(
            "team-mailbox", "Team mailbox", "email-work-items",
            new Dictionary<string, string> { ["ImapHost"] = "imap.example.org" },
            SlotInstanceScope.Company, null, []));

        await _sut.UpsertAsync(AnyConfiguration(bindings:
            [new StoredSlotBinding("work-items", "", new Dictionary<string, string>(), instance.Id)]));

        var stored = await _sut.GetByNameAsync("alpha");
        var binding = stored!.SlotBindings.Single();
        Assert.Multiple(() =>
        {
            Assert.That(binding.ProviderType, Is.EqualTo("email-work-items"));
            Assert.That(binding.Settings["ImapHost"], Is.EqualTo("imap.example.org"));
            Assert.That(binding.SlotInstanceId, Is.EqualTo(instance.Id));
            Assert.That(binding.SlotInstanceResolved, Is.True);
        });
    }

    [Test]
    public async Task InstanceUpdate_FlowsIntoEveryConfigurationReferencingIt()
    {
        var instance = await _slotInstances.UpsertAsync(new StoredSlotInstance(
            "team-mailbox", "Team mailbox", "email-work-items",
            new Dictionary<string, string> { ["ImapHost"] = "imap.old.org" },
            SlotInstanceScope.Company, null, []));
        await _sut.UpsertAsync(AnyConfiguration(bindings:
            [new StoredSlotBinding("work-items", "", new Dictionary<string, string>(), instance.Id)]));

        await _slotInstances.UpsertAsync(instance with
        {
            Settings = new Dictionary<string, string> { ["ImapHost"] = "imap.new.org" }
        });

        var stored = await _sut.GetByNameAsync("alpha");
        Assert.That(stored!.SlotBindings.Single().Settings["ImapHost"], Is.EqualTo("imap.new.org"));
    }

    [Test]
    public void UpsertAsync_UnknownInstanceReference_IsRejected()
    {
        var exception = Assert.ThrowsAsync<ArgumentException>(() => _sut.UpsertAsync(
            AnyConfiguration(bindings:
                [new StoredSlotBinding("work-items", "", new Dictionary<string, string>(), Guid.NewGuid())])));

        Assert.That(exception!.Message, Does.Contain("unknown slot instance"));
    }

    [Test]
    public async Task DeletedInstance_MarksTheBindingUnresolved()
    {
        var instance = await _slotInstances.UpsertAsync(new StoredSlotInstance(
            "team-mailbox", "Team mailbox", "email-work-items",
            new Dictionary<string, string>(), SlotInstanceScope.Company, null, []));
        await _sut.UpsertAsync(AnyConfiguration(bindings:
            [new StoredSlotBinding("work-items", "", new Dictionary<string, string>(), instance.Id)]));

        await _slotInstances.RemoveAsync("team-mailbox");

        var stored = await _sut.GetByNameAsync("alpha");
        Assert.That(stored!.SlotBindings.Single().SlotInstanceResolved, Is.False);
    }

    [Test]
    public async Task UpsertAsync_InstanceBackedBinding_StoresNoInlineSettings()
    {
        var instance = await _slotInstances.UpsertAsync(new StoredSlotInstance(
            "team-mailbox", "Team mailbox", "email-work-items",
            new Dictionary<string, string> { ["Password"] = "super-secret" },
            SlotInstanceScope.Company, null, []));

        await _sut.UpsertAsync(AnyConfiguration(bindings:
            [new StoredSlotBinding("work-items", "", new Dictionary<string, string>(), instance.Id)]));

        var record = await _records.ReadAsync(WorkflowConfigurationRecord.IdFor("alpha"));
        Assert.That(record!.SlotBindingsJson, Does.Not.Contain("super-secret"),
            "instance settings must live only on the instance record");
    }
}
