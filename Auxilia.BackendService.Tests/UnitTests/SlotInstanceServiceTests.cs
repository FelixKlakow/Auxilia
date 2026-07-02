using System.Text.Json;
using Auxilia.BackendService.Dashboard;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.Workflows;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.BackendService.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class SlotInstanceServiceTests
{
    private sealed class RecordingBus : IMessageBusClient
    {
        public List<(string Topic, object Message)> Published { get; } = [];
        public Task DeclareQueueAsync(string q, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeclareExchangeAsync(string e, CancellationToken ct = default) => Task.CompletedTask;
        public Task PublishAsync<T>(string topic, T message, CancellationToken ct = default)
        {
            Published.Add((topic, message!));
            return Task.CompletedTask;
        }
        public Task PublishToExchangeAsync<T>(string e, T m, CancellationToken ct = default) => PublishAsync(e, m, ct);
        public Task<IAsyncDisposable> SubscribeAsync<T>(string q, Func<T, CancellationToken, Task> h, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable>(new Noop());
        public Task<IAsyncDisposable> SubscribeToExchangeAsync<T>(string e, Func<T, CancellationToken, Task> h, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable>(new Noop());
        private sealed class Noop : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }

    private RecordingBus _bus = null!;
    private IDataAccess<SlotInstanceRecord> _instances = null!;
    private IDataAccess<SlotProviderRecord> _providers = null!;
    private IDataAccess<ProviderCatalogRecord> _catalogRecords = null!;
    private IDataAccess<AuditRecord> _audit = null!;
    private NullSettingsProtector _protector = null!;
    private SlotInstanceService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _bus = new RecordingBus();
        _instances = new InMemoryDataAccess<SlotInstanceRecord>();
        _providers = new InMemoryDataAccess<SlotProviderRecord>();
        _catalogRecords = new InMemoryDataAccess<ProviderCatalogRecord>();
        _audit = new InMemoryDataAccess<AuditRecord>();
        _protector = new NullSettingsProtector();
        var auditLog = new AuditLog(_audit, TimeProvider.System);
        _sut = new SlotInstanceService(
            _instances,
            new ProviderCatalogService(_providers, _catalogRecords, auditLog),
            _protector, _bus, auditLog);
    }

    [TearDown]
    public void TearDown()
    {
        (_instances as IDisposable)?.Dispose();
        (_providers as IDisposable)?.Dispose();
        (_catalogRecords as IDisposable)?.Dispose();
        (_audit as IDisposable)?.Dispose();
    }

    private static readonly SettingDescriptor[] EmailDescriptors =
    [
        new("ImapHost", "IMAP host", SettingKind.Text, Required: true),
        new("Password", "Password", SettingKind.Secret, Required: true)
    ];

    private Task SeedProviderAsync()
        => _providers.SaveAsync(new SlotProviderRecord
        {
            Id = SlotProviderRecord.IdFor("email-work-items"),
            ProviderType = "email-work-items",
            DllPath = "/plugins/email.slothandler.dll",
            SettingDescriptorsJson = JsonSerializer.Serialize(EmailDescriptors),
            Category = "task-source"
        });

    /// <summary>Seeds an instance record the way the Steering Instance would persist it.</summary>
    private Task SeedInstanceAsync(
        string name = "team-mailbox", string scope = "Company",
        Guid? owner = null, IReadOnlyList<Guid>? assigned = null)
        => _instances.SaveAsync(new SlotInstanceRecord
        {
            Id = SlotInstanceRecord.IdFor(name),
            Name = name,
            DisplayName = "Team mailbox",
            ProviderType = "email-work-items",
            ProtectedSettingsJson = _protector.Protect(JsonSerializer.Serialize(
                new Dictionary<string, string>
                {
                    ["ImapHost"] = "imap.example.org",
                    ["Password"] = "stored-secret"
                })),
            Scope = scope,
            OwnerPrincipalId = owner,
            AssignedPrincipalIdsJson = JsonSerializer.Serialize(assigned ?? [])
        });

    private SlotInstanceDraft NewDraft()
    {
        var draft = new SlotInstanceDraft { DisplayName = "Team mailbox", ProviderType = "email-work-items" };
        draft.Settings["ImapHost"] = "imap.example.org";
        draft.Settings["Password"] = "hunter2";
        return draft;
    }

    private UpsertSlotInstanceCommand PublishedUpsert()
        => _bus.Published
            .Where(p => p.Topic == SlotInstanceService.SeedExchangeName)
            .Select(p => p.Message).OfType<UpsertSlotInstanceCommand>().Single();

    [Test]
    public async Task Save_New_PublishesUpsertWithSluggedNameAndOwner()
    {
        await SeedProviderAsync();
        var actor = Guid.NewGuid();

        var name = await _sut.SaveAsync(actor.ToString("D"), actor, NewDraft());

        var command = PublishedUpsert();
        Assert.Multiple(() =>
        {
            Assert.That(name, Is.EqualTo("team-mailbox"));
            Assert.That(command.ProviderType, Is.EqualTo("email-work-items"));
            Assert.That(command.OwnerPrincipalId, Is.EqualTo(actor));
            Assert.That(command.Settings["Password"], Is.EqualTo("hunter2"));
        });

        var audit = (await _audit.ReadAsync()).Single(a => a.Action == "slot-instance.saved");
        Assert.That(audit.DetailJson, Does.Not.Contain("hunter2"), "settings values must never be audited");
    }

    [Test]
    public async Task Save_MissingRequiredDescriptor_IsRejected()
    {
        await SeedProviderAsync();
        var draft = NewDraft();
        draft.Settings["ImapHost"] = "";

        var exception = Assert.ThrowsAsync<ArgumentException>(() => _sut.SaveAsync("actor", null, draft));

        Assert.That(exception!.Message, Does.Contain("IMAP host"));
    }

    [Test]
    public async Task LoadDraft_MasksStoredSecrets()
    {
        await SeedProviderAsync();
        await SeedInstanceAsync();

        var draft = await _sut.LoadDraftAsync(SlotInstanceRecord.IdFor("team-mailbox"));

        Assert.Multiple(() =>
        {
            Assert.That(draft!.Settings["Password"], Is.Empty, "secret values are never read back");
            Assert.That(draft.StoredSecretKeys, Does.Contain("Password"));
            Assert.That(draft.Settings["ImapHost"], Is.EqualTo("imap.example.org"));
        });
    }

    [Test]
    public async Task Save_EmptySecretOnEdit_KeepsTheStoredValue()
    {
        await SeedProviderAsync();
        await SeedInstanceAsync();

        var draft = (await _sut.LoadDraftAsync(SlotInstanceRecord.IdFor("team-mailbox")))!;
        draft.Settings["ImapHost"] = "imap.changed.org";

        await _sut.SaveAsync("actor", null, draft);

        var command = PublishedUpsert();
        Assert.Multiple(() =>
        {
            Assert.That(command.Settings["Password"], Is.EqualTo("stored-secret"));
            Assert.That(command.Settings["ImapHost"], Is.EqualTo("imap.changed.org"));
        });
    }

    [Test]
    public async Task Accessible_FiltersByScopeOwnerAndAssignment()
    {
        await SeedProviderAsync();
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        await SeedInstanceAsync("company-wide", "Company");
        await SeedInstanceAsync("my-own", "Personal", owner: me);
        await SeedInstanceAsync("assigned-to-me", "Personal", owner: other, assigned: [me]);
        await SeedInstanceAsync("someone-elses", "Personal", owner: other);

        var accessible = await _sut.AccessibleAsync(me);

        Assert.That(accessible.Select(i => i.Name),
            Is.EquivalentTo(new[] { "company-wide", "my-own", "assigned-to-me" }));
    }

    [Test]
    public async Task List_ExposesKeyNamesButNeverValues()
    {
        await SeedProviderAsync();
        await SeedInstanceAsync();

        var overview = (await _sut.ListAsync()).Single();

        Assert.Multiple(() =>
        {
            Assert.That(overview.SettingKeys, Is.EquivalentTo(new[] { "ImapHost", "Password" }));
            Assert.That(overview.Category, Is.EqualTo("task-source"));
        });
    }

    [Test]
    public async Task Delete_PublishesRemovalAndAudits()
    {
        await SeedInstanceAsync();

        await _sut.DeleteAsync("actor", SlotInstanceRecord.IdFor("team-mailbox"));

        Assert.Multiple(async () =>
        {
            var removal = _bus.Published.Select(p => p.Message)
                .OfType<RemoveSlotInstanceCommand>().Single();
            Assert.That(removal.Name, Is.EqualTo("team-mailbox"));
            Assert.That((await _audit.ReadAsync()).Any(a => a.Action == "slot-instance.deleted"), Is.True);
        });
    }
}
