using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
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

    private List<string> _journal = null!;
    private ManualTimeProvider _time = null!;
    private RecordingBus _bus = null!;
    private FakeMailboxClient _mailbox = null!;
    private IDataAccess<AuditRecord> _audit = null!;
    private EmailTaskSourceSettings _settings = null!;
    private EmailTaskSourceAdapter _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _journal = [];
        _time = new ManualTimeProvider();
        _bus = new RecordingBus(_journal);
        _mailbox = new FakeMailboxClient(_journal);
        _audit = new InMemoryDataAccess<AuditRecord>();
        _settings = new EmailTaskSourceSettings
        {
            Enabled = true,
            WorkflowType = "mail-triage",
            WorkflowPackageUri = "docker://mail-triage:test",
            RunAsPrincipalId = Guid.NewGuid(),
            CommandQueueName = "workflow.run-commands"
        };
        _sut = new EmailTaskSourceAdapter(
            _mailbox, _bus, new AuditLog(_audit, _time), _time,
            Options.Create(_settings), NullLogger<EmailTaskSourceAdapter>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _sut.Dispose();
        (_audit as IDisposable)?.Dispose();
    }

    private static InboundMail Mail(
        string messageId = "<msg-1@example.com>",
        string subject = "Please triage",
        string from = "alice@example.com",
        string body = "Hello, please look at this.",
        uint uid = 42)
        => new(messageId, subject, from, body, uid);

    [Test]
    public async Task OneUnseenMail_DispatchesOneCommandMarksSeenAndAudits()
    {
        var mail = Mail();
        _mailbox.Unseen.Add(mail);

        await _sut.PollOnceAsync(CancellationToken.None);

        var dispatches = _bus.Published
            .Where(p => p.Topic == _settings.CommandQueueName)
            .Select(p => p.Message).OfType<RunWorkflowCommand>().ToList();
        Assert.That(dispatches, Has.Count.EqualTo(1));
        Assert.That(_bus.Published, Has.Count.EqualTo(1), "Nothing besides the dispatch may be published.");

        var command = dispatches[0];
        Assert.Multiple(() =>
        {
            Assert.That(command.WorkflowType, Is.EqualTo(_settings.WorkflowType));
            Assert.That(command.WorkflowPackageUri, Is.EqualTo(_settings.WorkflowPackageUri));
            Assert.That(command.Context["WorkItemId"], Is.EqualTo(EmailTaskSourceAdapter.WorkItemIdFor(mail.MessageId)));
            Assert.That(command.Context["Title"], Is.EqualTo(mail.Subject));
            Assert.That(command.Context["From"], Is.EqualTo(mail.From));
            Assert.That(command.Context["Body"], Is.EqualTo(mail.BodyText));
            Assert.That(command.RequestedBy, Is.EqualTo(_settings.RunAsPrincipalId));
            Assert.That(_mailbox.MarkedSeen, Is.EqualTo(new[] { mail.Uid }));
        });

        var auditRecords = await _audit.ReadAsync();
        var record = auditRecords.SingleOrDefault(a => a.Action == "trigger.mail-dispatch");
        Assert.That(record, Is.Not.Null, "The dispatch must be audited.");
        Assert.That(record!.Subject, Is.EqualTo(EmailTaskSourceAdapter.WorkItemIdFor(mail.MessageId)));
    }

    [Test]
    public async Task MultipleUnseenMails_OneDispatchEachWithDistinctWorkItemIds()
    {
        _mailbox.Unseen.Add(Mail(messageId: "<a@example.com>", uid: 1));
        _mailbox.Unseen.Add(Mail(messageId: "<b@example.com>", uid: 2));
        _mailbox.Unseen.Add(Mail(messageId: "<c@example.com>", uid: 3));

        await _sut.PollOnceAsync(CancellationToken.None);

        var workItemIds = _bus.Published
            .Where(p => p.Topic == _settings.CommandQueueName)
            .Select(p => p.Message).OfType<RunWorkflowCommand>()
            .Select(c => c.Context["WorkItemId"]).ToList();
        Assert.That(workItemIds, Has.Count.EqualTo(3));
        Assert.That(workItemIds, Is.Unique);
        Assert.That(_mailbox.MarkedSeen, Is.EquivalentTo(new uint[] { 1, 2, 3 }));
    }

    [Test]
    public async Task NoUnseenMail_NothingPublishedAndNothingMarkedSeen()
    {
        await _sut.PollOnceAsync(CancellationToken.None);

        Assert.That(_bus.Published, Is.Empty);
        Assert.That(_mailbox.MarkedSeen, Is.Empty);
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
        var mail = Mail(uid: 7);
        _mailbox.Unseen.Add(mail);

        await _sut.PollOnceAsync(CancellationToken.None);

        Assert.That(_journal, Is.EqualTo(new[]
        {
            $"publish:{_settings.CommandQueueName}",
            $"seen:{mail.Uid}"
        }), "The \\Seen flag may only be set after the dispatch is on the bus.");
    }
}
