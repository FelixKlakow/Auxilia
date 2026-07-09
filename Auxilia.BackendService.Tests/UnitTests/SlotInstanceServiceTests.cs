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
    private IDataAccess<WorkflowConfigurationRecord> _configurations = null!;
    private IDataAccess<MailboxTriggerRecord> _mailboxTriggers = null!;
    private IDataAccess<SlotProviderRecord> _providers = null!;
    private IDataAccess<ProviderCatalogRecord> _catalogRecords = null!;
    private IDataAccess<AuditRecord> _audit = null!;
    private IDataAccess<ConnectorRecord> _connectors = null!;
    private NullSettingsProtector _protector = null!;
    private SlotInstanceService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _bus = new RecordingBus();
        _instances = new InMemoryDataAccess<SlotInstanceRecord>();
        _configurations = new InMemoryDataAccess<WorkflowConfigurationRecord>();
        _mailboxTriggers = new InMemoryDataAccess<MailboxTriggerRecord>();
        _providers = new InMemoryDataAccess<SlotProviderRecord>();
        _catalogRecords = new InMemoryDataAccess<ProviderCatalogRecord>();
        _audit = new InMemoryDataAccess<AuditRecord>();
        _connectors = new InMemoryDataAccess<ConnectorRecord>();
        _protector = new NullSettingsProtector();
        var auditLog = new AuditLog(_audit, TimeProvider.System);
        _sut = new SlotInstanceService(
            _instances, _configurations, _mailboxTriggers,
            new ProviderCatalogService(_providers, _catalogRecords, auditLog),
            new ConnectorService(
                _connectors, new Auxilia.BackendService.Dashboard.Connect.ConnectFlowRegistry([]),
                _protector, auditLog, TimeProvider.System),
            _protector, _bus, auditLog);
    }

    [TearDown]
    public void TearDown()
    {
        (_instances as IDisposable)?.Dispose();
        (_configurations as IDisposable)?.Dispose();
        (_mailboxTriggers as IDisposable)?.Dispose();
        (_providers as IDisposable)?.Dispose();
        (_catalogRecords as IDisposable)?.Dispose();
        (_audit as IDisposable)?.Dispose();
        (_connectors as IDisposable)?.Dispose();
    }

    private Task SeedConnectorAsync(
        string name = "my-claude", string scope = "Company", Guid? owner = null)
        => _connectors.SaveAsync(new ConnectorRecord
        {
            Id = ConnectorRecord.IdFor(name),
            Name = name,
            DisplayName = "My Claude account",
            FlowKey = "anthropic-claude",
            ProtectedToken = _protector.Protect("oauth-token-value"),
            Scope = scope,
            OwnerPrincipalId = owner
        });

    [Test]
    public async Task Save_WithConnectorReference_EmbedsTheResolvedToken()
    {
        await SeedProviderAsync();
        await SeedConnectorAsync();
        var draft = NewDraft();
        draft.Settings["Password"] = ConnectorService.ReferenceFor(ConnectorRecord.IdFor("my-claude"));

        await _sut.SaveAsync("tester", Guid.NewGuid(), draft);

        Assert.That(PublishedUpsert().Settings["Password"], Is.EqualTo("oauth-token-value"),
            "the picked connector's credential must travel instead of the reference");
    }

    [Test]
    public async Task Save_WithForeignPersonalConnector_IsRefused()
    {
        await SeedProviderAsync();
        await SeedConnectorAsync(scope: "Personal", owner: Guid.NewGuid());
        var draft = NewDraft();
        draft.Settings["Password"] = ConnectorService.ReferenceFor(ConnectorRecord.IdFor("my-claude"));

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.SaveAsync("tester", Guid.NewGuid(), draft));
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

    // ------------------------------------------------------------------ usage & masking

    private Task SeedConfigurationUsingInstanceAsync(
        string name = "team-review", Guid? viaBinding = null, string displayName = "Team review")
        => _configurations.SaveAsync(new WorkflowConfigurationRecord
        {
            Id = WorkflowConfigurationRecord.IdFor(name),
            Name = name,
            DisplayName = displayName,
            WorkflowType = "pull-request-code-review",
            PackageUri = "docker://review:1",
            SlotBindingsJson = JsonSerializer.Serialize(new List<WorkflowConfigurationSlotBinding>
            {
                new()
                {
                    SlotName = "work-items",
                    ProviderType = "email-work-items",
                    ProtectedSettingsJson = "{}",
                    SlotInstanceId = viaBinding
                }
            })
        });

    [Test]
    public async Task List_ShowsPlainValues_AndMasksOnlySecrets()
    {
        await SeedProviderAsync();
        await SeedInstanceAsync();

        var overview = (await _sut.ListAsync()).Single();

        Assert.Multiple(() =>
        {
            Assert.That(overview.Settings.Single(s => s.Key == "ImapHost").Value,
                Is.EqualTo("imap.example.org"), "declared-plain settings show their value in the list");
            Assert.That(overview.Settings.Single(s => s.Key == "Password").Value,
                Is.Null, "secret values never leave the service");
        });
    }

    [Test]
    public async Task List_MasksEverySetting_WhenTheProviderDeclaresNoDescriptors()
    {
        // No provider record at all: nothing is declared plain, so nothing may render.
        await SeedInstanceAsync();

        var overview = (await _sut.ListAsync()).Single();

        Assert.That(overview.Settings.Select(s => s.Value), Is.All.Null,
            "unknown keys are never guessed to be safe");
    }

    [Test]
    public async Task List_ReportsWhichConfigurationsUseTheInstance()
    {
        await SeedProviderAsync();
        await SeedInstanceAsync();
        var instanceId = SlotInstanceRecord.IdFor("team-mailbox");
        await SeedConfigurationUsingInstanceAsync("via-binding", viaBinding: instanceId, displayName: "Via binding");
        await SeedConfigurationUsingInstanceAsync("via-trigger", displayName: "Via trigger");
        await _mailboxTriggers.SaveAsync(new MailboxTriggerRecord
        {
            Id = Guid.NewGuid(),
            WorkflowConfigurationId = WorkflowConfigurationRecord.IdFor("via-trigger"),
            SlotInstanceId = instanceId
        });

        var overview = (await _sut.ListAsync()).Single();

        Assert.That(overview.UsedByConfigurations,
            Is.EquivalentTo(new[] { "Via binding", "Via trigger" }),
            "both slot bindings and mailbox triggers count as usage");
    }

    // ------------------------------------------------------------------ self-service (my slots)

    [Test]
    public async Task Mine_ListsOwnedAndAssignedPersonalInstances_Only()
    {
        await SeedProviderAsync();
        var me = Guid.NewGuid();
        var someoneElse = Guid.NewGuid();
        await SeedInstanceAsync("company-mailbox");
        await SeedInstanceAsync("own-seat", scope: "Personal", owner: me);
        await SeedInstanceAsync("assigned-seat", scope: "Personal", owner: someoneElse, assigned: [me]);
        await SeedInstanceAsync("foreign-seat", scope: "Personal", owner: someoneElse);

        var mine = await _sut.MineAsync(me);

        Assert.That(mine.Select(i => i.Name), Is.EquivalentTo(new[] { "own-seat", "assigned-seat" }),
            "company instances and other people's personal instances stay out");
    }

    [Test]
    public async Task SaveOwn_ForcesPersonalScopeAndOwnership()
    {
        await SeedProviderAsync();
        var me = Guid.NewGuid();
        var draft = NewDraft();
        draft.Scope = "Company"; // the caller cannot escalate to a company instance

        await _sut.SaveOwnAsync(me, draft);

        var command = _bus.Published.Select(p => p.Item2).OfType<UpsertSlotInstanceCommand>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(command.Scope, Is.EqualTo("Personal"));
            Assert.That(command.OwnerPrincipalId, Is.EqualTo(me));
        });
    }

    [Test]
    public async Task SaveOwn_OfSomeoneElsesInstance_IsRefused()
    {
        await SeedProviderAsync();
        await SeedInstanceAsync("foreign-seat", scope: "Personal", owner: Guid.NewGuid());
        var draft = NewDraft();
        draft.ExistingName = "foreign-seat";

        var exception = Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.SaveOwnAsync(Guid.NewGuid(), draft));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("Only the owner"));
            Assert.That(_bus.Published.Select(p => p.Item2).OfType<UpsertSlotInstanceCommand>(), Is.Empty);
        });
    }

    [Test]
    public async Task DeleteOwn_OfSomeoneElsesInstance_IsRefused()
    {
        await SeedProviderAsync();
        await SeedInstanceAsync("foreign-seat", scope: "Personal", owner: Guid.NewGuid());

        Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.DeleteOwnAsync(Guid.NewGuid(), SlotInstanceRecord.IdFor("foreign-seat")));
        Assert.That(_bus.Published.Select(p => p.Item2).OfType<RemoveSlotInstanceCommand>(), Is.Empty);
    }

    [Test]
    public async Task Delete_OfAnInstanceInUse_IsRefused()
    {
        await SeedProviderAsync();
        await SeedInstanceAsync();
        var instanceId = SlotInstanceRecord.IdFor("team-mailbox");
        await SeedConfigurationUsingInstanceAsync(viaBinding: instanceId);

        var exception = Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.DeleteAsync("actor", instanceId));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("still used by 1 workflow configuration")
                .And.Contain("Team review"));
            Assert.That(_bus.Published.OfType<(string, object)>()
                .Select(p => p.Item2).OfType<RemoveSlotInstanceCommand>(), Is.Empty,
                "no removal reaches the bus while the instance is referenced");
        });
    }
}
