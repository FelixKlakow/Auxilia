using System.Text.Json;
using Auxilia.Adapters.Email;
using Auxilia.BackendService.Dashboard;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.Workflows;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Options;

namespace Auxilia.BackendService.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class WorkflowConfigurationEditorServiceTests
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
    private IDataAccess<WorkflowConfigurationRecord> _configurations = null!;
    private IDataAccess<WorkflowInstanceRecord> _instances = null!;
    private IDataAccess<ScheduledTriggerRecord> _schedules = null!;
    private IDataAccess<ArtifactTriggerRecord> _chains = null!;
    private IDataAccess<SlotConfigurationRecord> _slots = null!;
    private IDataAccess<SlotProviderRecord> _providers = null!;
    private IDataAccess<AuditRecord> _audit = null!;
    private NullSettingsProtector _protector = null!;
    private WorkflowConfigurationEditorService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _bus = new RecordingBus();
        _configurations = new InMemoryDataAccess<WorkflowConfigurationRecord>();
        _instances = new InMemoryDataAccess<WorkflowInstanceRecord>();
        _schedules = new InMemoryDataAccess<ScheduledTriggerRecord>();
        _chains = new InMemoryDataAccess<ArtifactTriggerRecord>();
        _slots = new InMemoryDataAccess<SlotConfigurationRecord>();
        _providers = new InMemoryDataAccess<SlotProviderRecord>();
        _audit = new InMemoryDataAccess<AuditRecord>();
        _protector = new NullSettingsProtector();
        var auditLog = new AuditLog(_audit, TimeProvider.System);
        _sut = new WorkflowConfigurationEditorService(
            _configurations, _instances, _schedules, _chains, _slots, _providers,
            new ProviderCatalogService(_providers, new InMemoryDataAccess<ProviderCatalogRecord>(), auditLog),
            _protector, _bus, auditLog,
            Options.Create(new EmailTaskSourceSettings()));
    }

    [TearDown]
    public void TearDown()
    {
        (_configurations as IDisposable)?.Dispose();
        (_instances as IDisposable)?.Dispose();
        (_schedules as IDisposable)?.Dispose();
        (_chains as IDisposable)?.Dispose();
        (_slots as IDisposable)?.Dispose();
        (_providers as IDisposable)?.Dispose();
        (_audit as IDisposable)?.Dispose();
    }

    private static readonly SettingDescriptor[] EmailDescriptors =
    [
        new("ImapHost", "IMAP host", SettingKind.Text, Required: true),
        new("Password", "Password", SettingKind.Secret, Required: true),
        new("Folder", "Mail folder", SettingKind.Text, DefaultValue: "INBOX")
    ];

    private Task SeedProviderAsync(string providerType = "email-work-items")
        => _providers.SaveAsync(new SlotProviderRecord
        {
            Id = SlotProviderRecord.IdFor(providerType),
            ProviderType = providerType,
            DllPath = $"/plugins/{providerType}.slothandler.dll",
            SettingDescriptorsJson = JsonSerializer.Serialize(EmailDescriptors)
        });

    /// <summary>Seeds a stored configuration the way the Steering Instance would persist it.</summary>
    private Task SeedConfigurationAsync(
        string name = "team-review", string displayName = "Team review",
        IReadOnlyDictionary<string, string>? settings = null, bool enabled = true)
        => _configurations.SaveAsync(new WorkflowConfigurationRecord
        {
            Id = WorkflowConfigurationRecord.IdFor(name),
            Name = name,
            DisplayName = displayName,
            WorkflowType = "pull-request-code-review",
            PackageUri = "docker://review:1",
            Enabled = enabled,
            SlotBindingsJson = JsonSerializer.Serialize(new List<WorkflowConfigurationSlotBinding>
            {
                new()
                {
                    SlotName = "work-items",
                    ProviderType = "email-work-items",
                    ProtectedSettingsJson = _protector.Protect(JsonSerializer.Serialize(
                        settings ?? new Dictionary<string, string>
                        {
                            ["ImapHost"] = "imap.example.org",
                            ["Password"] = "stored-secret",
                            ["Folder"] = "INBOX"
                        }))
                }
            })
        });

    private static WorkflowConfigurationDraft NewDraft()
    {
        var draft = new WorkflowConfigurationDraft
        {
            DisplayName = "Team review",
            WorkflowType = "pull-request-code-review",
            PackageUri = "docker://review:1"
        };
        var binding = new SlotBindingDraft { SlotName = "work-items", ProviderType = "email-work-items" };
        binding.Settings["ImapHost"] = "imap.example.org";
        binding.Settings["Password"] = "hunter2";
        draft.Bindings.Add(binding);
        return draft;
    }

    private UpsertWorkflowConfigurationCommand PublishedUpsert()
        => _bus.Published
            .Where(p => p.Topic == WorkflowConfigurationEditorService.SeedExchangeName)
            .Select(p => p.Message).OfType<UpsertWorkflowConfigurationCommand>().Single();

    // ------------------------------------------------------------------ save & validation

    [Test]
    public async Task Save_NewConfiguration_PublishesUpsertWithSluggedNameAndAudits()
    {
        await SeedProviderAsync();
        var actor = Guid.NewGuid();

        var name = await _sut.SaveAsync(actor.ToString("D"), actor, NewDraft());

        var command = PublishedUpsert();
        Assert.Multiple(() =>
        {
            Assert.That(name, Is.EqualTo("team-review"), "the natural key derives from the display name");
            Assert.That(command.Name, Is.EqualTo("team-review"));
            Assert.That(command.WorkflowType, Is.EqualTo("pull-request-code-review"));
            Assert.That(command.OwnerPrincipalId, Is.EqualTo(actor));
            Assert.That(command.SlotBindings.Single().Settings["Password"], Is.EqualTo("hunter2"));
        });

        var audit = (await _audit.ReadAsync()).Single(a => a.Action == "workflow-configuration.saved");
        Assert.Multiple(() =>
        {
            Assert.That(audit.Outcome, Is.EqualTo("created"));
            Assert.That(audit.DetailJson, Does.Not.Contain("hunter2"),
                "settings values must never be audited");
        });
    }

    [Test]
    public async Task Save_NameCollision_GetsNumericSuffix()
    {
        await SeedProviderAsync();
        await SeedConfigurationAsync(); // occupies "team-review"

        var name = await _sut.SaveAsync("actor", null, NewDraft());

        Assert.That(name, Is.EqualTo("team-review-2"));
    }

    [Test]
    public async Task Save_MissingRequiredDescriptorField_IsRejected()
    {
        await SeedProviderAsync();
        var draft = NewDraft();
        draft.Bindings[0].Settings["ImapHost"] = "";

        var exception = Assert.ThrowsAsync<ArgumentException>(() => _sut.SaveAsync("actor", null, draft));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("IMAP host"));
            Assert.That(_bus.Published, Is.Empty, "nothing is published for an invalid draft");
        });
    }

    [Test]
    public async Task Save_DuplicateSlotNames_IsRejected()
    {
        await SeedProviderAsync();
        var draft = NewDraft();
        var duplicate = new SlotBindingDraft { SlotName = "work-items", ProviderType = "email-work-items" };
        duplicate.Settings["ImapHost"] = "imap.example.org";
        duplicate.Settings["Password"] = "pw";
        draft.Bindings.Add(duplicate);

        Assert.ThrowsAsync<ArgumentException>(() => _sut.SaveAsync("actor", null, draft));
    }

    [Test]
    public async Task Save_MissingBasics_IsRejectedWithAllErrors()
    {
        await SeedProviderAsync();
        var draft = new WorkflowConfigurationDraft();

        var exception = Assert.ThrowsAsync<ArgumentException>(() => _sut.SaveAsync("actor", null, draft));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("Display name"));
            Assert.That(exception.Message, Does.Contain("Workflow type"));
            Assert.That(exception.Message, Does.Contain("Package URI"));
        });
    }

    // ------------------------------------------------------------------ secret semantics

    [Test]
    public async Task LoadDraft_MasksStoredSecrets_ButShowsPlainValues()
    {
        await SeedProviderAsync();
        await SeedConfigurationAsync();

        var draft = await _sut.LoadDraftAsync(WorkflowConfigurationRecord.IdFor("team-review"));

        var binding = draft!.Bindings.Single();
        Assert.Multiple(() =>
        {
            Assert.That(binding.Settings["Password"], Is.Empty, "secret values are never read back");
            Assert.That(binding.StoredSecretKeys, Does.Contain("Password"));
            Assert.That(binding.Settings["ImapHost"], Is.EqualTo("imap.example.org"));
        });
    }

    [Test]
    public async Task Save_EmptySecretOnEdit_KeepsTheStoredValue()
    {
        await SeedProviderAsync();
        await SeedConfigurationAsync();
        var draft = (await _sut.LoadDraftAsync(WorkflowConfigurationRecord.IdFor("team-review")))!;
        draft.Bindings[0].Settings["ImapHost"] = "imap.changed.org"; // secret left empty

        await _sut.SaveAsync("actor", null, draft);

        var command = PublishedUpsert();
        Assert.Multiple(() =>
        {
            Assert.That(command.SlotBindings.Single().Settings["Password"], Is.EqualTo("stored-secret"),
                "an empty secret input keeps the stored value");
            Assert.That(command.SlotBindings.Single().Settings["ImapHost"], Is.EqualTo("imap.changed.org"));
        });
    }

    [Test]
    public async Task Save_TypedSecretOnEdit_ReplacesTheStoredValue()
    {
        await SeedProviderAsync();
        await SeedConfigurationAsync();
        var draft = (await _sut.LoadDraftAsync(WorkflowConfigurationRecord.IdFor("team-review")))!;
        draft.Bindings[0].Settings["Password"] = "brand-new-secret";

        await _sut.SaveAsync("actor", null, draft);

        Assert.That(PublishedUpsert().SlotBindings.Single().Settings["Password"],
            Is.EqualTo("brand-new-secret"));
    }

    // ------------------------------------------------------------------ duplicate

    [Test]
    public async Task Duplicate_CreatesDisabledCopyOfX_WithCarriedSettings()
    {
        await SeedProviderAsync();
        await SeedConfigurationAsync();

        var name = await _sut.DuplicateAsync("actor", WorkflowConfigurationRecord.IdFor("team-review"));

        var command = PublishedUpsert();
        Assert.Multiple(async () =>
        {
            Assert.That(name, Is.EqualTo("copy-of-team-review"));
            Assert.That(command.DisplayName, Is.EqualTo("Copy of Team review"));
            Assert.That(command.Enabled, Is.False, "duplicates start disabled so triggers never double-fire");
            Assert.That(command.SlotBindings.Single().Settings["Password"], Is.EqualTo("stored-secret"),
                "settings (incl. secrets) carry over server-side");
            var audit = await _audit.ReadAsync();
            Assert.That(audit.Any(a => a.Action == "workflow-configuration.duplicated"), Is.True);
        });
    }

    // ------------------------------------------------------------------ trigger wiring

    [Test]
    public async Task Save_WithScheduleTrigger_WiresTheScheduledTriggerRecord()
    {
        await SeedProviderAsync();
        var actor = Guid.NewGuid();
        var draft = NewDraft();
        draft.Trigger = TriggerKind.Schedule;
        draft.ScheduleIntervalSeconds = 600;

        var name = await _sut.SaveAsync(actor.ToString("D"), actor, draft);

        var trigger = (await _schedules.ReadAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(trigger.WorkflowConfigurationId, Is.EqualTo(WorkflowConfigurationRecord.IdFor(name)));
            Assert.That(trigger.IntervalSeconds, Is.EqualTo(600));
            Assert.That(trigger.WorkflowType, Is.EqualTo("pull-request-code-review"));
            Assert.That(trigger.RunAsPrincipalId, Is.EqualTo(actor),
                "scheduled dispatches run as the saving principal");
        });
    }

    [Test]
    public async Task Save_WithArtifactTrigger_WiresTheChainingRecord()
    {
        await SeedProviderAsync();
        var draft = NewDraft();
        draft.Trigger = TriggerKind.ArtifactChain;
        draft.ArtifactType = "code-review-result";

        var name = await _sut.SaveAsync("actor", null, draft);

        var trigger = (await _chains.ReadAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(trigger.WorkflowConfigurationId, Is.EqualTo(WorkflowConfigurationRecord.IdFor(name)));
            Assert.That(trigger.ArtifactType, Is.EqualTo("code-review-result"));
        });
    }

    [Test]
    public async Task Save_SwitchingTriggerToNone_RemovesThePreviousTriggerRecord()
    {
        await SeedProviderAsync();
        var draft = NewDraft();
        draft.Trigger = TriggerKind.Schedule;
        var name = await _sut.SaveAsync("actor", null, draft);
        await SeedConfigurationAsync(); // the SI has applied the upsert by now

        var edit = NewDraft();
        edit.ExistingName = name;
        edit.Trigger = TriggerKind.None;
        await _sut.SaveAsync("actor", null, edit);

        Assert.Multiple(async () =>
        {
            Assert.That(await _schedules.ReadAsync(), Is.Empty);
            var audit = await _audit.ReadAsync();
            Assert.That(audit.Any(a => a.Action == "trigger.scheduled.deleted"), Is.True);
        });
    }

    [Test]
    public async Task Save_InvalidScheduleInterval_IsRejected()
    {
        await SeedProviderAsync();
        var draft = NewDraft();
        draft.Trigger = TriggerKind.Schedule;
        draft.ScheduleIntervalSeconds = 0;

        Assert.ThrowsAsync<ArgumentException>(() => _sut.SaveAsync("actor", null, draft));
    }

    // ------------------------------------------------------------------ delete

    [Test]
    public async Task Delete_PublishesRemoveCommand_DeletesTriggers_AndAudits()
    {
        await SeedProviderAsync();
        await SeedConfigurationAsync();
        await _schedules.SaveAsync(new ScheduledTriggerRecord
        {
            Id = ScheduledTriggerRecord.IdForConfiguration("team-review"),
            WorkflowType = "pull-request-code-review",
            WorkflowPackageUri = "docker://review:1",
            IntervalSeconds = 60,
            WorkflowConfigurationId = WorkflowConfigurationRecord.IdFor("team-review")
        });

        await _sut.DeleteAsync("actor", WorkflowConfigurationRecord.IdFor("team-review"));

        var removal = _bus.Published
            .Select(p => p.Message).OfType<RemoveWorkflowConfigurationCommand>().Single();
        Assert.Multiple(async () =>
        {
            Assert.That(removal.Name, Is.EqualTo("team-review"));
            Assert.That(await _schedules.ReadAsync(), Is.Empty, "wired trigger records are removed");
            var audit = await _audit.ReadAsync();
            Assert.That(audit.Any(a => a.Action == "workflow-configuration.deleted"), Is.True);
        });
    }

    // ------------------------------------------------------------------ slug

    [TestCase("Code Review", "code-review")]
    [TestCase("  Übersicht & Review!  ", "bersicht-review")]
    [TestCase("---", "workflow-configuration")]
    public void Slugify_NormalizesDisplayNames(string displayName, string expected)
        => Assert.That(WorkflowConfigurationEditorService.Slugify(displayName), Is.EqualTo(expected));
}
