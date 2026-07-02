using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Auxilia.Adapters.Email.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class EmailTaskSourceAdapterTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 6, 12, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Records every publish; shares a journal with the fake mailbox so ordering is observable.</summary>
    private sealed class RecordingBus(List<string> journal) : IMessageBusClient
    {
        public List<(string Topic, object Message)> Published { get; } = [];

        public Task DeclareQueueAsync(string queueName, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeclareExchangeAsync(string exchangeName, CancellationToken ct = default) => Task.CompletedTask;

        public Task PublishAsync<T>(string topic, T message, CancellationToken ct = default)
        {
            Published.Add((topic, message!));
            journal.Add($"publish:{topic}");
            return Task.CompletedTask;
        }

        public Task PublishToExchangeAsync<T>(string exchangeName, T message, CancellationToken ct = default)
            => PublishAsync(exchangeName, message, ct);

        public Task<IAsyncDisposable> SubscribeAsync<T>(string queueName, Func<T, CancellationToken, Task> handler, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable>(new Noop());
        public Task<IAsyncDisposable> SubscribeToExchangeAsync<T>(string exchangeName, Func<T, CancellationToken, Task> handler, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable>(new Noop());

        private sealed class Noop : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeMailboxClient(List<string> journal) : IMailboxClient
    {
        public List<InboundMail> Unseen { get; } = [];
        public List<uint> MarkedSeen { get; } = [];

        public Task<IReadOnlyList<InboundMail>> FetchUnseenAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<InboundMail>>(Unseen.ToList());

        public Task MarkSeenAsync(uint uid, CancellationToken ct = default)
        {
            MarkedSeen.Add(uid);
            journal.Add($"seen:{uid}");
            return Task.CompletedTask;
        }

        public Task SendReplyAsync(
            string to, string subject, string body, string inReplyToMessageId,
            CancellationToken ct = default)
            => Task.CompletedTask;
    }

    /// <summary>One fake client per mailbox host, so multi-trigger routing is observable.</summary>
    private sealed class FakeMailboxClientFactory(List<string> journal) : IMailboxClientFactory
    {
        public Dictionary<string, FakeMailboxClient> ByHost { get; } = new(StringComparer.Ordinal);
        public List<EmailTaskSourceSettings> SeenSettings { get; } = [];

        public IMailboxClient Create(EmailTaskSourceSettings settings)
        {
            SeenSettings.Add(settings);
            if (!ByHost.TryGetValue(settings.ImapHost, out var client))
                ByHost[settings.ImapHost] = client = new FakeMailboxClient(journal);
            return client;
        }
    }

    private List<string> _journal = null!;
    private ManualTimeProvider _time = null!;
    private RecordingBus _bus = null!;
    private FakeMailboxClientFactory _factory = null!;
    private InMemoryDataAccess<MailboxTriggerRecord> _triggers = null!;
    private InMemoryDataAccess<SlotInstanceRecord> _instances = null!;
    private InMemoryDataAccess<TriggerHealthRecord> _health = null!;
    private IDataAccess<AuditRecord> _audit = null!;
    private NullSettingsProtector _protector = null!;
    private MailboxTriggerAdapterSettings _settings = null!;
    private EmailTaskSourceAdapter _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _journal = [];
        _time = new ManualTimeProvider();
        _bus = new RecordingBus(_journal);
        _factory = new FakeMailboxClientFactory(_journal);
        _triggers = new InMemoryDataAccess<MailboxTriggerRecord>();
        _instances = new InMemoryDataAccess<SlotInstanceRecord>();
        _health = new InMemoryDataAccess<TriggerHealthRecord>();
        _audit = new InMemoryDataAccess<AuditRecord>();
        _protector = new NullSettingsProtector();
        _settings = new MailboxTriggerAdapterSettings { CommandQueueName = "workflow.run-commands" };
        _sut = new EmailTaskSourceAdapter(
            _triggers, _instances, _health, _protector, _factory, _bus,
            new AuditLog(_audit, _time), _time,
            Options.Create(_settings), NullLogger<EmailTaskSourceAdapter>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _sut.Dispose();
        _triggers.Dispose();
        _instances.Dispose();
        _health.Dispose();
        (_audit as IDisposable)?.Dispose();
    }

    private async Task<SlotInstanceRecord> SeedInstanceAsync(string name = "team-mailbox", string host = "imap.example.org")
    {
        var record = new SlotInstanceRecord
        {
            Id = SlotInstanceRecord.IdFor(name),
            Name = name,
            DisplayName = name,
            ProviderType = "email-work-items",
            ProtectedSettingsJson = _protector.Protect(JsonSerializer.Serialize(
                new Dictionary<string, string>
                {
                    ["ImapHost"] = host,
                    ["Username"] = "workflows@localhost",
                    ["Password"] = "pw"
                }))
        };
        await _instances.SaveAsync(record);
        return record;
    }

    private async Task<MailboxTriggerRecord> SeedTriggerAsync(
        Guid instanceId, Guid? configurationId = null, int pollSeconds = 15,
        bool enabled = true, Guid? runAs = null,
        string subjectContains = "", string fromContains = "")
    {
        var record = new MailboxTriggerRecord
        {
            Id = Guid.NewGuid(),
            WorkflowConfigurationId = configurationId ?? Guid.NewGuid(),
            SlotInstanceId = instanceId,
            PollIntervalSeconds = pollSeconds,
            Enabled = enabled,
            RunAsPrincipalId = runAs,
            SubjectContains = subjectContains,
            FromContains = fromContains
        };
        await _triggers.SaveAsync(record);
        return record;
    }

    private static InboundMail Mail(
        string messageId = "<msg-1@example.com>",
        string subject = "Please triage",
        string from = "alice@example.com",
        string body = "Hello, please look at this.",
        uint uid = 42)
        => new(messageId, subject, from, body, uid);

    [Test]
    public async Task OneUnseenMail_DispatchesTheConfiguration_MarksSeen_AndAudits()
    {
        var runAs = Guid.NewGuid();
        var instance = await SeedInstanceAsync();
        var trigger = await SeedTriggerAsync(instance.Id, runAs: runAs);
        var mail = Mail();
        _factory.ByHost["imap.example.org"] = new FakeMailboxClient(_journal);
        _factory.ByHost["imap.example.org"].Unseen.Add(mail);

        await _sut.PollDueTriggersAsync(CancellationToken.None);

        var dispatches = _bus.Published
            .Where(p => p.Topic == _settings.CommandQueueName)
            .Select(p => p.Message).OfType<RunWorkflowCommand>().ToList();
        Assert.That(dispatches, Has.Count.EqualTo(1));

        var command = dispatches[0];
        Assert.Multiple(() =>
        {
            Assert.That(command.WorkflowConfigurationId, Is.EqualTo(trigger.WorkflowConfigurationId),
                "mail dispatches the trigger's workflow configuration — no type/URI coordinates");
            Assert.That(command.WorkflowType, Is.Null);
            Assert.That(command.Context["WorkItemId"], Is.EqualTo(EmailTaskSourceAdapter.WorkItemIdFor(mail.MessageId)));
            Assert.That(command.Context["Title"], Is.EqualTo(mail.Subject));
            Assert.That(command.RequestedBy, Is.EqualTo(runAs));
            Assert.That(_factory.ByHost["imap.example.org"].MarkedSeen, Is.EqualTo(new[] { mail.Uid }));
        });

        var auditRecords = await _audit.ReadAsync();
        Assert.That(auditRecords.Any(a => a.Action == "trigger.mail-dispatch"), Is.True);
    }

    [Test]
    public async Task PollInterval_IsHonouredPerTrigger()
    {
        var instance = await SeedInstanceAsync();
        await SeedTriggerAsync(instance.Id, pollSeconds: 60);

        await _sut.PollDueTriggersAsync(CancellationToken.None);
        _time.Now = _time.Now.AddSeconds(30);
        await _sut.PollDueTriggersAsync(CancellationToken.None);

        Assert.That(_factory.SeenSettings, Has.Count.EqualTo(1),
            "within the poll interval the mailbox must not be contacted again");

        _time.Now = _time.Now.AddSeconds(31);
        await _sut.PollDueTriggersAsync(CancellationToken.None);

        Assert.That(_factory.SeenSettings, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task DisabledTrigger_IsNeverPolled()
    {
        var instance = await SeedInstanceAsync();
        await SeedTriggerAsync(instance.Id, enabled: false);

        await _sut.PollDueTriggersAsync(CancellationToken.None);

        Assert.That(_factory.SeenSettings, Is.Empty);
    }

    [Test]
    public async Task Poll_WritesTheHealthSidecar_OnSuccessAndDispatch()
    {
        var instance = await SeedInstanceAsync();
        var trigger = await SeedTriggerAsync(instance.Id);
        _factory.ByHost["imap.example.org"] = new FakeMailboxClient(_journal);
        _factory.ByHost["imap.example.org"].Unseen.Add(Mail());

        await _sut.PollDueTriggersAsync(CancellationToken.None);

        var health = await _health.ReadAsync(trigger.Id);
        Assert.Multiple(() =>
        {
            Assert.That(health, Is.Not.Null);
            Assert.That(health!.LastPollUtc, Is.EqualTo(_time.Now));
            Assert.That(health.LastSuccessUtc, Is.EqualTo(_time.Now));
            Assert.That(health.LastDispatchUtc, Is.EqualTo(_time.Now), "a mail was dispatched this poll");
            Assert.That(health.LastError, Is.Null);
            Assert.That(health.FailingSinceUtc, Is.Null);
        });
    }

    [Test]
    public async Task Poll_FailureStreak_KeepsFailingSince_AndRecoveryClearsIt()
    {
        var trigger = await SeedTriggerAsync(Guid.NewGuid()); // instance missing → config error
        var firstFailure = _time.Now;

        await _sut.PollDueTriggersAsync(CancellationToken.None);
        _time.Now = _time.Now.AddSeconds(20);
        await _sut.PollDueTriggersAsync(CancellationToken.None);

        var failing = await _health.ReadAsync(trigger.Id);
        Assert.Multiple(() =>
        {
            Assert.That(failing!.LastError, Does.Contain("deleted slot instance"));
            Assert.That(failing.FailingSinceUtc, Is.EqualTo(firstFailure),
                "the streak start survives subsequent failing polls");
            Assert.That(failing.LastPollUtc, Is.EqualTo(_time.Now));
            Assert.That(failing.LastSuccessUtc, Is.Null);
        });

        // The instance appears (recovery): the next poll clears the failure.
        var record = await SeedInstanceAsync();
        await _triggers.SaveAsync((await _triggers.ReadAsync(trigger.Id))! with { SlotInstanceId = record.Id });
        _time.Now = _time.Now.AddSeconds(20);
        await _sut.PollDueTriggersAsync(CancellationToken.None);

        var recovered = await _health.ReadAsync(trigger.Id);
        Assert.Multiple(() =>
        {
            Assert.That(recovered!.LastError, Is.Null);
            Assert.That(recovered.FailingSinceUtc, Is.Null);
            Assert.That(recovered.LastSuccessUtc, Is.EqualTo(_time.Now));
        });
    }

    [Test]
    public async Task TriggerWithDeletedInstance_IsSkippedWithoutFailingTheSweep()
    {
        await SeedTriggerAsync(Guid.NewGuid()); // instance never existed
        var healthy = await SeedInstanceAsync("healthy", "imap.healthy.org");
        await SeedTriggerAsync(healthy.Id);

        await _sut.PollDueTriggersAsync(CancellationToken.None);

        Assert.That(_factory.SeenSettings.Select(s => s.ImapHost),
            Is.EqualTo(new[] { "imap.healthy.org" }),
            "the healthy trigger must still be polled");
    }

    [Test]
    public async Task TwoTriggers_PollTheirOwnMailboxes()
    {
        var first = await SeedInstanceAsync("first", "imap.first.org");
        var second = await SeedInstanceAsync("second", "imap.second.org");
        var firstTrigger = await SeedTriggerAsync(first.Id);
        var secondTrigger = await SeedTriggerAsync(second.Id);
        _factory.ByHost["imap.first.org"] = new FakeMailboxClient(_journal);
        _factory.ByHost["imap.second.org"] = new FakeMailboxClient(_journal);
        _factory.ByHost["imap.first.org"].Unseen.Add(Mail(messageId: "<a@x>", uid: 1));
        _factory.ByHost["imap.second.org"].Unseen.Add(Mail(messageId: "<b@x>", uid: 2));

        await _sut.PollDueTriggersAsync(CancellationToken.None);

        var configurations = _bus.Published
            .Select(p => p.Message).OfType<RunWorkflowCommand>()
            .Select(c => c.WorkflowConfigurationId).ToList();
        Assert.That(configurations, Is.EquivalentTo(new[]
        {
            firstTrigger.WorkflowConfigurationId, secondTrigger.WorkflowConfigurationId
        }));
    }

    [Test]
    public async Task Filters_NonMatchingMailIsMarkedSeenWithoutDispatch()
    {
        var instance = await SeedInstanceAsync();
        await SeedTriggerAsync(instance.Id, subjectContains: "[review]", fromContains: "@team.example");
        var client = new FakeMailboxClient(_journal);
        _factory.ByHost["imap.example.org"] = client;
        client.Unseen.Add(Mail(messageId: "<hit@x>", subject: "Please [review] PR-7",
            from: "alice@team.example", uid: 1));
        client.Unseen.Add(Mail(messageId: "<wrong-subject@x>", subject: "Lunch plans",
            from: "alice@team.example", uid: 2));
        client.Unseen.Add(Mail(messageId: "<wrong-sender@x>", subject: "Please [review] PR-8",
            from: "spam@elsewhere.example", uid: 3));

        await _sut.PollDueTriggersAsync(CancellationToken.None);

        var dispatches = _bus.Published.Select(p => p.Message).OfType<RunWorkflowCommand>().ToList();
        Assert.Multiple(() =>
        {
            Assert.That(dispatches, Has.Count.EqualTo(1), "only the matching mail dispatches");
            Assert.That(dispatches[0].Context["Title"], Is.EqualTo("Please [review] PR-7"));
            Assert.That(client.MarkedSeen, Is.EquivalentTo(new uint[] { 1, 2, 3 }),
                "filtered mails are marked seen so they are not re-evaluated forever");
        });
    }

    [TestCase("[REVIEW]", "please [review] this", true)]
    [TestCase("", "anything", true)]
    [TestCase("[review]", "unrelated", false)]
    public void MatchesFilters_SubjectIsCaseInsensitiveSubstring(
        string filter, string subject, bool expected)
    {
        var trigger = new MailboxTriggerRecord
        {
            Id = Guid.NewGuid(),
            WorkflowConfigurationId = Guid.NewGuid(),
            SlotInstanceId = Guid.NewGuid(),
            SubjectContains = filter
        };

        Assert.That(
            EmailTaskSourceAdapter.MatchesFilters(trigger, Mail(subject: subject)),
            Is.EqualTo(expected));
    }

    [Test]
    public void WorkItemIdFor_IsDeterministicDistinctAndWellFormed()
    {
        var first = EmailTaskSourceAdapter.WorkItemIdFor("<msg-1@example.com>");
        var second = EmailTaskSourceAdapter.WorkItemIdFor("<msg-1@example.com>");
        var other = EmailTaskSourceAdapter.WorkItemIdFor("<msg-2@example.com>");

        Assert.Multiple(() =>
        {
            Assert.That(second, Is.EqualTo(first), "Same Message-Id must map to the same work item.");
            Assert.That(other, Is.Not.EqualTo(first), "Different Message-Ids must map to different work items.");
            Assert.That(first, Does.Match("^mail-[0-9a-f]{16}$"));
            Assert.That(other, Does.Match("^mail-[0-9a-f]{16}$"));
        });
    }

    [Test]
    public async Task MarkSeen_HappensOnlyAfterTheDispatchWasPublished()
    {
        var instance = await SeedInstanceAsync();
        await SeedTriggerAsync(instance.Id);
        var mail = Mail(uid: 7);
        _factory.ByHost["imap.example.org"] = new FakeMailboxClient(_journal);
        _factory.ByHost["imap.example.org"].Unseen.Add(mail);

        await _sut.PollDueTriggersAsync(CancellationToken.None);

        Assert.That(_journal, Is.EqualTo(new[]
        {
            $"publish:{_settings.CommandQueueName}",
            $"seen:{mail.Uid}"
        }), "The \\Seen flag may only be set after the dispatch is on the bus.");
    }
}
