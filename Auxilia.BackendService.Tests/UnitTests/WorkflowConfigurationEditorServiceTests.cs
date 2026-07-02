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
    private IDataAccess<WorkflowPackageRecord> _packages = null!;
    private IDataAccess<WorkflowSchemaRecord> _schemas = null!;
    private IDataAccess<SlotInstanceRecord> _slotInstances = null!;
    private IDataAccess<MailboxTriggerRecord> _mailboxTriggerRecords = null!;
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
        _packages = new InMemoryDataAccess<WorkflowPackageRecord>();
        _schemas = new InMemoryDataAccess<WorkflowSchemaRecord>();
        _slotInstances = new InMemoryDataAccess<SlotInstanceRecord>();
        _mailboxTriggerRecords = new InMemoryDataAccess<MailboxTriggerRecord>();
        _audit = new InMemoryDataAccess<AuditRecord>();
        _protector = new NullSettingsProtector();
        var auditLog = new AuditLog(_audit, TimeProvider.System);
        _sut = new WorkflowConfigurationEditorService(
            _configurations, _instances, _schedules, _chains, _slots, _providers,
            _packages, _schemas, _slotInstances, _mailboxTriggerRecords,
            new ProviderCatalogService(_providers, new InMemoryDataAccess<ProviderCatalogRecord>(), auditLog),
            _protector, _bus, auditLog);
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
        (_packages as IDisposable)?.Dispose();
        (_schemas as IDisposable)?.Dispose();
        (_slotInstances as IDisposable)?.Dispose();
        (_mailboxTriggerRecords as IDisposable)?.Dispose();
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
        IReadOnlyDictionary<string, string>? settings = null, bool enabled = true,
        string workflowType = "pull-request-code-review")
        => _configurations.SaveAsync(new WorkflowConfigurationRecord
        {
            Id = WorkflowConfigurationRecord.IdFor(name),
            Name = name,
            DisplayName = displayName,
            WorkflowType = workflowType,
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

    // ------------------------------------------------------------------ registry

    private Task SeedPackageAsync(
        string workflowType = "pull-request-code-review", string packageUri = "docker://review:1",
        string displayName = "Pull-request code review")
        => _packages.SaveAsync(new WorkflowPackageRecord
        {
            Id = WorkflowPackageRecord.IdFor(workflowType),
            WorkflowType = workflowType,
            PackageUri = packageUri,
            DisplayName = displayName
        });

    private Task SeedSchemaAsync(string workflowType = "pull-request-code-review", params SlotDefinition[] slots)
        => _schemas.SaveAsync(new WorkflowSchemaRecord
        {
            Id = WorkflowSchemaRecord.IdFor(workflowType),
            WorkflowType = workflowType,
            SchemaJson = JsonSerializer.Serialize(new WorkflowSchema(workflowType, slots, []) { Version = "1.2" })
        });

    [Test]
    public async Task RegisteredWorkflows_JoinPackagesWithStoredSchemas()
    {
        await SeedPackageAsync();
        await SeedSchemaAsync(slots:
        [
            new SlotDefinition("work-items", null, "where work comes from")
                { Contract = "Auxilia.Workflows.TaskSource.IWorkItemAccess" },
            new SlotDefinition("reviewer", null) { Optional = true }
        ]);

        var workflows = await _sut.RegisteredWorkflowsAsync();

        var workflow = workflows.Single();
        Assert.Multiple(() =>
        {
            Assert.That(workflow.DisplayName, Is.EqualTo("Pull-request code review"));
            Assert.That(workflow.PackageUri, Is.EqualTo("docker://review:1"));
            Assert.That(workflow.SchemaKnown, Is.True);
            Assert.That(workflow.Version, Is.EqualTo("1.2"), "the schema's version wins over the registry's");
            Assert.That(workflow.Slots.Select(s => s.SlotName), Is.EqualTo(new[] { "work-items", "reviewer" }));
            Assert.That(workflow.Slots[0].Contract, Is.EqualTo("Auxilia.Workflows.TaskSource.IWorkItemAccess"));
            Assert.That(workflow.Slots[0].Optional, Is.False);
            Assert.That(workflow.Slots[1].Optional, Is.True);
        });
    }

    [Test]
    public async Task RegisteredWorkflows_WithoutSchema_AreOfferedWithUnknownSlots()
    {
        await SeedPackageAsync(displayName: "");

        var workflow = (await _sut.RegisteredWorkflowsAsync()).Single();
        Assert.Multiple(() =>
        {
            Assert.That(workflow.DisplayName, Is.EqualTo("pull-request-code-review"));
            Assert.That(workflow.SchemaKnown, Is.False);
            Assert.That(workflow.Slots, Is.Empty);
        });
    }

    [Test]
    public async Task Save_SchemaKnown_MissingRequiredSlot_IsRejected()
    {
        await SeedProviderAsync();
        await SeedSchemaAsync(slots:
        [
            new SlotDefinition("work-items", null),
            new SlotDefinition("repository", null)
        ]);

        // NewDraft binds only "work-items" — "repository" stays unbound.
        var exception = Assert.ThrowsAsync<ArgumentException>(() => _sut.SaveAsync("actor", null, NewDraft()));

        Assert.That(exception!.Message, Does.Contain("Slot 'repository' is required"));
    }

    [Test]
    public async Task Save_SchemaKnown_UnboundOptionalSlot_IsAccepted()
    {
        await SeedProviderAsync();
        await SeedSchemaAsync(slots:
        [
            new SlotDefinition("work-items", null),
            new SlotDefinition("reviewer", null) { Optional = true }
        ]);

        var name = await _sut.SaveAsync("actor", null, NewDraft());

        Assert.That(name, Is.EqualTo("team-review"));
    }

    // ------------------------------------------------------------------ slot instances

    private Task SeedInstanceAsync(
        string name = "team-mailbox", string scope = "Company",
        Guid? owner = null, IReadOnlyList<Guid>? assigned = null)
        => _slotInstances.SaveAsync(new SlotInstanceRecord
        {
            Id = SlotInstanceRecord.IdFor(name),
            Name = name,
            DisplayName = "Team mailbox",
            ProviderType = "email-work-items",
            ProtectedSettingsJson = _protector.Protect("{}"),
            Scope = scope,
            OwnerPrincipalId = owner,
            AssignedPrincipalIdsJson = JsonSerializer.Serialize(assigned ?? [])
        });

    [Test]
    public async Task Save_InstanceBackedBinding_PublishesTheReferenceWithoutInlineSettings()
    {
        await SeedProviderAsync();
        await SeedInstanceAsync();

        var draft = NewDraft();
        draft.Bindings.Clear();
        draft.Bindings.Add(new SlotBindingDraft
        {
            SlotName = "work-items",
            SlotInstanceId = SlotInstanceRecord.IdFor("team-mailbox")
        });

        await _sut.SaveAsync("actor", null, draft);

        var binding = PublishedUpsert().SlotBindings.Single();
        Assert.Multiple(() =>
        {
            Assert.That(binding.SlotInstanceId, Is.EqualTo(SlotInstanceRecord.IdFor("team-mailbox")));
            Assert.That(binding.Settings, Is.Empty);
        });
    }

    [Test]
    public async Task Save_BindingWithUnknownInstance_IsRejected()
    {
        await SeedProviderAsync();
        var draft = NewDraft();
        draft.Bindings.Clear();
        draft.Bindings.Add(new SlotBindingDraft { SlotName = "work-items", SlotInstanceId = Guid.NewGuid() });

        var exception = Assert.ThrowsAsync<ArgumentException>(() => _sut.SaveAsync("actor", null, draft));

        Assert.That(exception!.Message, Does.Contain("no longer exists"));
    }

    [Test]
    public async Task Save_BindingWithForeignPersonalInstance_IsRejected()
    {
        await SeedProviderAsync();
        var owner = Guid.NewGuid();
        var actor = Guid.NewGuid();
        await SeedInstanceAsync(scope: "Personal", owner: owner);

        var draft = NewDraft();
        draft.Bindings.Clear();
        draft.Bindings.Add(new SlotBindingDraft
        {
            SlotName = "work-items",
            SlotInstanceId = SlotInstanceRecord.IdFor("team-mailbox")
        });

        var exception = Assert.ThrowsAsync<ArgumentException>(() => _sut.SaveAsync(actor.ToString("D"), actor, draft));

        Assert.That(exception!.Message, Does.Contain("no access"));
    }

    [Test]
    public async Task Save_BindingWithAssignedPersonalInstance_IsAccepted()
    {
        await SeedProviderAsync();
        var owner = Guid.NewGuid();
        var actor = Guid.NewGuid();
        await SeedInstanceAsync(scope: "Personal", owner: owner, assigned: [actor]);

        var draft = NewDraft();
        draft.Bindings.Clear();
        draft.Bindings.Add(new SlotBindingDraft
        {
            SlotName = "work-items",
            SlotInstanceId = SlotInstanceRecord.IdFor("team-mailbox")
        });

        var name = await _sut.SaveAsync(actor.ToString("D"), actor, draft);

        Assert.That(name, Is.EqualTo("team-review"));
    }

    [Test]
    public async Task LoadDraft_InstanceBackedBinding_RoundTripsTheReference()
    {
        await SeedProviderAsync();
        await SeedInstanceAsync();
        await _configurations.SaveAsync(new WorkflowConfigurationRecord
        {
            Id = WorkflowConfigurationRecord.IdFor("instance-bound"),
            Name = "instance-bound",
            DisplayName = "Instance bound",
            WorkflowType = "pull-request-code-review",
            PackageUri = "docker://review:1",
            Enabled = true,
            SlotBindingsJson = JsonSerializer.Serialize(new List<WorkflowConfigurationSlotBinding>
            {
                new()
                {
                    SlotName = "work-items",
                    ProviderType = "email-work-items",
                    ProtectedSettingsJson = _protector.Protect("{}"),
                    SlotInstanceId = SlotInstanceRecord.IdFor("team-mailbox")
                }
            })
        });

        var draft = await _sut.LoadDraftAsync(WorkflowConfigurationRecord.IdFor("instance-bound"));

        var binding = draft!.Bindings.Single();
        Assert.Multiple(() =>
        {
            Assert.That(binding.SlotInstanceId, Is.EqualTo(SlotInstanceRecord.IdFor("team-mailbox")));
            Assert.That(binding.Settings, Is.Empty, "instance-backed bindings carry no inline settings");
        });
    }

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
            Assert.That(exception.Message, Does.Contain("Pick the workflow"));
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
        draft.Triggers.Add(new TriggerDraft { Kind = TriggerKind.Schedule, IntervalSeconds = 600 });

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
    public async Task Save_WithSeveralTriggers_WiresThemAll()
    {
        await SeedProviderAsync();
        await SeedInstanceAsync();
        var draft = NewDraft();
        draft.Triggers.Add(new TriggerDraft { Kind = TriggerKind.Schedule, IntervalSeconds = 600 });
        draft.Triggers.Add(new TriggerDraft { Kind = TriggerKind.Schedule, IntervalSeconds = 86400 });
        draft.Triggers.Add(new TriggerDraft { Kind = TriggerKind.ArtifactChain, ArtifactType = "code-review-result" });
        draft.Triggers.Add(new TriggerDraft
        {
            Kind = TriggerKind.Mailbox,
            MailboxInstanceId = SlotInstanceRecord.IdFor("team-mailbox"),
            PollIntervalSeconds = 30,
            SubjectContains = "[review]",
            FromContains = "@team.example"
        });

        var name = await _sut.SaveAsync("actor", null, draft);

        Assert.Multiple(async () =>
        {
            Assert.That((await _schedules.ReadAsync()).Select(t => t.IntervalSeconds),
                Is.EquivalentTo(new[] { 600, 86400 }), "one configuration can carry several schedules");
            Assert.That((await _chains.ReadAsync()).Single().ArtifactType, Is.EqualTo("code-review-result"));
            var mailbox = (await _mailboxTriggerRecords.ReadAsync()).Single();
            Assert.That(mailbox.WorkflowConfigurationId, Is.EqualTo(WorkflowConfigurationRecord.IdFor(name)));
            Assert.That(mailbox.SlotInstanceId, Is.EqualTo(SlotInstanceRecord.IdFor("team-mailbox")));
            Assert.That(mailbox.PollIntervalSeconds, Is.EqualTo(30));
            Assert.That(mailbox.SubjectContains, Is.EqualTo("[review]"),
                "mail filters persist on the trigger record");
            Assert.That(mailbox.FromContains, Is.EqualTo("@team.example"));
        });
    }

    [Test]
    public async Task Save_RemovedTrigger_DeletesItsRecord_AndKeptOneSurvivesWithLastDispatch()
    {
        await SeedProviderAsync();
        var draft = NewDraft();
        draft.Triggers.Add(new TriggerDraft { Kind = TriggerKind.Schedule, IntervalSeconds = 600 });
        draft.Triggers.Add(new TriggerDraft { Kind = TriggerKind.ArtifactChain, ArtifactType = "result" });
        var name = await _sut.SaveAsync("actor", null, draft);
        await SeedConfigurationAsync(); // the SI has applied the upsert by now

        // Simulate the scheduler having dispatched once.
        var stored = (await _schedules.ReadAsync()).Single();
        var lastDispatched = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _schedules.SaveAsync(stored with { LastDispatchedUtc = lastDispatched });

        var edit = NewDraft();
        edit.ExistingName = name;
        edit.Triggers.Add(new TriggerDraft
        {
            ExistingId = stored.Id,
            Kind = TriggerKind.Schedule,
            IntervalSeconds = 1200
        }); // the artifact chain is gone
        await _sut.SaveAsync("actor", null, edit);

        Assert.Multiple(async () =>
        {
            var schedule = (await _schedules.ReadAsync()).Single();
            Assert.That(schedule.IntervalSeconds, Is.EqualTo(1200));
            Assert.That(schedule.LastDispatchedUtc, Is.EqualTo(lastDispatched),
                "editing must never reset the dispatch clock — no surprise runs");
            Assert.That(await _chains.ReadAsync(), Is.Empty);
            var audit = await _audit.ReadAsync();
            Assert.That(audit.Any(a => a.Action == "trigger.artifact.deleted"), Is.True);
        });
    }

    [Test]
    public async Task Save_InvalidScheduleInterval_IsRejected()
    {
        await SeedProviderAsync();
        var draft = NewDraft();
        draft.Triggers.Add(new TriggerDraft { Kind = TriggerKind.Schedule, IntervalSeconds = 0 });

        Assert.ThrowsAsync<ArgumentException>(() => _sut.SaveAsync("actor", null, draft));
    }

    [Test]
    public async Task Save_MailboxTriggerWithoutInstance_IsRejected()
    {
        await SeedProviderAsync();
        var draft = NewDraft();
        draft.Triggers.Add(new TriggerDraft { Kind = TriggerKind.Mailbox });

        var exception = Assert.ThrowsAsync<ArgumentException>(() => _sut.SaveAsync("actor", null, draft));

        Assert.That(exception!.Message, Does.Contain("mailbox"));
    }

    // ------------------------------------------------------------------ flow view

    private Task SeedOutputSchemaAsync(string workflowType = "pull-request-code-review")
        => _schemas.SaveAsync(new WorkflowSchemaRecord
        {
            Id = WorkflowSchemaRecord.IdFor(workflowType),
            WorkflowType = workflowType,
            SchemaJson = JsonSerializer.Serialize(new WorkflowSchema(workflowType, [], [])
            {
                Outputs = [new WorkflowOutputDescriptor("code-review-result", "result.json", "Findings")]
            })
        });

    [Test]
    public async Task Flow_AssemblesTriggersOutputsAndConsumers()
    {
        await SeedProviderAsync();
        await SeedInstanceAsync();
        await SeedOutputSchemaAsync();
        await SeedConfigurationAsync(); // "team-review"
        await SeedConfigurationAsync("follow-up", "Follow up");
        var id = WorkflowConfigurationRecord.IdFor("team-review");
        await _schedules.SaveAsync(new ScheduledTriggerRecord
        {
            Id = Guid.NewGuid(),
            WorkflowType = "pull-request-code-review",
            WorkflowPackageUri = "docker://review:1",
            IntervalSeconds = 3600,
            WorkflowConfigurationId = id
        });
        await _mailboxTriggerRecords.SaveAsync(new MailboxTriggerRecord
        {
            Id = Guid.NewGuid(),
            WorkflowConfigurationId = id,
            SlotInstanceId = SlotInstanceRecord.IdFor("team-mailbox"),
            PollIntervalSeconds = 15
        });
        await _chains.SaveAsync(new ArtifactTriggerRecord
        {
            Id = Guid.NewGuid(),
            ArtifactType = "code-review-result",
            WorkflowType = "pull-request-code-review",
            WorkflowPackageUri = "docker://review:1",
            WorkflowConfigurationId = WorkflowConfigurationRecord.IdFor("follow-up")
        });

        var flow = await _sut.FlowAsync(id);

        Assert.Multiple(() =>
        {
            Assert.That(flow!.Triggers.Select(t => t.Kind),
                Is.EquivalentTo(new[] { TriggerKind.Schedule, TriggerKind.Mailbox }));
            Assert.That(flow.Triggers.Single(t => t.Kind == TriggerKind.Mailbox).Label,
                Is.EqualTo("Team mailbox"));
            var output = flow.Outputs.Single();
            Assert.That(output.Name, Is.EqualTo("code-review-result"));
            Assert.That(output.Consumers.Single().DisplayName, Is.EqualTo("Follow up"),
                "a chaining record on the output's artifact type makes its configuration a consumer");
            Assert.That(output.Candidates.Select(c => c.DisplayName), Does.Contain("Follow up"));
        });
    }

    [Test]
    public async Task Flow_ChainCandidates_HonourDeclaredConsumptionCriteria()
    {
        await SeedProviderAsync();
        await SeedOutputSchemaAsync(); // source outputs "code-review-result", declares no consumption
        await SeedConfigurationAsync(); // source "team-review"
        // A workflow that DECLARES it consumes the output type ...
        await _schemas.SaveAsync(new WorkflowSchemaRecord
        {
            Id = WorkflowSchemaRecord.IdFor("implementation-workflow"),
            WorkflowType = "implementation-workflow",
            SchemaJson = JsonSerializer.Serialize(new WorkflowSchema("implementation-workflow", [], [])
            {
                ConsumedArtifacts = ["code-review-result"]
            })
        });
        await SeedConfigurationAsync("implementer", "Implementer", workflowType: "implementation-workflow");
        // ... one that declares consuming only something ELSE ...
        await _schemas.SaveAsync(new WorkflowSchemaRecord
        {
            Id = WorkflowSchemaRecord.IdFor("report-workflow"),
            WorkflowType = "report-workflow",
            SchemaJson = JsonSerializer.Serialize(new WorkflowSchema("report-workflow", [], [])
            {
                ConsumedArtifacts = ["weekly-report"]
            })
        });
        await SeedConfigurationAsync("reporter", "Reporter", workflowType: "report-workflow");
        // ... and one whose workflow declares nothing (unconstrained).
        await SeedConfigurationAsync("generic", "Generic", workflowType: "unconstrained-workflow");

        var flow = await _sut.FlowAsync(WorkflowConfigurationRecord.IdFor("team-review"));

        var candidates = flow!.Outputs.Single().Candidates;
        Assert.Multiple(() =>
        {
            Assert.That(candidates.Single(c => c.DisplayName == "Implementer").MatchesCriteria, Is.True,
                "declared consumption of the output type is the matching criteria");
            Assert.That(candidates.Select(c => c.DisplayName), Does.Not.Contain("Reporter"),
                "a workflow declaring OTHER consumed types is not offered for this output");
            Assert.That(candidates.Single(c => c.DisplayName == "Generic").MatchesCriteria, Is.False,
                "workflows without declared criteria stay chainable but unmatched");
            Assert.That(candidates[0].DisplayName, Is.EqualTo("Implementer"),
                "criteria matches rank first");
        });
    }

    [Test]
    public async Task Chain_CreatesAnArtifactTriggerForTheTarget_AndAudits()
    {
        await SeedProviderAsync();
        await SeedConfigurationAsync(); // source "team-review"
        await SeedConfigurationAsync("follow-up", "Follow up");
        var actor = Guid.NewGuid();

        await _sut.ChainAsync(actor.ToString("D"), actor,
            WorkflowConfigurationRecord.IdFor("team-review"), "code-review-result",
            WorkflowConfigurationRecord.IdFor("follow-up"));

        var trigger = (await _chains.ReadAsync()).Single();
        Assert.Multiple(async () =>
        {
            Assert.That(trigger.ArtifactType, Is.EqualTo("code-review-result"));
            Assert.That(trigger.WorkflowConfigurationId,
                Is.EqualTo(WorkflowConfigurationRecord.IdFor("follow-up")));
            Assert.That(trigger.RunAsPrincipalId, Is.EqualTo(actor));
            var audit = await _audit.ReadAsync();
            Assert.That(audit.Any(a => a.Action == "trigger.artifact.created"), Is.True);
        });
    }

    [Test]
    public async Task Unchain_RemovesTheTriggerAndAudits()
    {
        await SeedProviderAsync();
        await SeedConfigurationAsync();
        await SeedConfigurationAsync("follow-up", "Follow up");
        await _sut.ChainAsync("actor", null,
            WorkflowConfigurationRecord.IdFor("team-review"), "code-review-result",
            WorkflowConfigurationRecord.IdFor("follow-up"));
        var trigger = (await _chains.ReadAsync()).Single();

        await _sut.UnchainAsync("actor", trigger.Id);

        Assert.Multiple(async () =>
        {
            Assert.That(await _chains.ReadAsync(), Is.Empty);
            var audit = await _audit.ReadAsync();
            Assert.That(audit.Any(a => a.Action == "trigger.artifact.deleted"), Is.True);
        });
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
