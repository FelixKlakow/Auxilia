using System.Text.Json;
using Auxilia.Adapters.Email;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess.Implementations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Auxilia.WorkflowStudio.Tests.UnitTests;

/// <summary>
/// When Studio hosts the mailbox trigger adapter, a matched mail must reach the platform through the
/// Core Run API (not the bus): a configured trigger runs its stored configuration on behalf of the
/// trigger's principal, carrying the mail context.
/// </summary>
[TestFixture]
[Category("Unit")]
public class EmailAdapterCoreDispatchTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeMailboxClient : IMailboxClient
    {
        public List<InboundMail> Unseen { get; } = [];
        public List<uint> MarkedSeen { get; } = [];

        public Task<IReadOnlyList<InboundMail>> FetchUnseenAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<InboundMail>>(Unseen.ToList());

        public Task MarkSeenAsync(uint uid, CancellationToken ct = default)
        {
            MarkedSeen.Add(uid);
            return Task.CompletedTask;
        }

        public Task SendReplyAsync(
            string to, string subject, string body, string inReplyToMessageId, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class SingleMailboxFactory(IMailboxClient client) : IMailboxClientFactory
    {
        public IMailboxClient Create(EmailTaskSourceSettings settings) => client;
    }

    [Test]
    public async Task UnseenMail_DispatchesViaCoreRunConfiguration_WithOnBehalfOfAndMailContext()
    {
        var time = new ManualTimeProvider();
        var protector = new NullSettingsProtector();
        using var triggers = new InMemoryDataAccess<MailboxTriggerRecord>();
        using var instances = new InMemoryDataAccess<SlotInstanceRecord>();
        using var health = new InMemoryDataAccess<TriggerHealthRecord>();
        using var audit = new InMemoryDataAccess<AuditRecord>();

        var instance = new SlotInstanceRecord
        {
            Id = SlotInstanceRecord.IdFor("team-mailbox"),
            Name = "team-mailbox",
            DisplayName = "team-mailbox",
            ProviderType = "email-work-items",
            ProtectedSettingsJson = protector.Protect(JsonSerializer.Serialize(
                new Dictionary<string, string>
                {
                    ["ImapHost"] = "imap.example.org",
                    ["Username"] = "workflows@localhost",
                    ["Password"] = "pw"
                }))
        };
        await instances.SaveAsync(instance);

        var configId = Guid.NewGuid();
        var runAs = Guid.NewGuid();
        await triggers.SaveAsync(new MailboxTriggerRecord
        {
            Id = Guid.NewGuid(),
            WorkflowConfigurationId = configId,
            SlotInstanceId = instance.Id,
            PollIntervalSeconds = 15,
            Enabled = true,
            RunAsPrincipalId = runAs
        });

        var mailbox = new FakeMailboxClient();
        mailbox.Unseen.Add(new InboundMail(
            "<msg-1@example.com>", "Please triage", "alice@example.com", "Hello, please look at this.", 42));

        var core = new FakeCoreClient();
        using var adapter = new EmailTaskSourceAdapter(
            triggers, instances, health, protector, new SingleMailboxFactory(mailbox),
            new CoreClientRunDispatcher(core), new AuditLog(audit, time), time,
            Options.Create(new MailboxTriggerAdapterSettings()),
            NullLogger<EmailTaskSourceAdapter>.Instance);

        await adapter.PollDueTriggersAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(core.RunConfigurationIds, Is.EqualTo(new[] { configId }),
                "a configured trigger dispatches its stored configuration via the Core Run API");
            Assert.That(core.RunConfigurationOnBehalfOf, Is.EqualTo(new Guid?[] { runAs }),
                "the run is dispatched on behalf of the trigger's principal");
            Assert.That(core.RunRequests, Is.Empty, "the ad-hoc run path is not used for a configured trigger");
            Assert.That(mailbox.MarkedSeen, Is.EqualTo(new uint[] { 42 }));
        });

        var context = core.RunConfigurationContexts.Single()!;
        Assert.Multiple(() =>
        {
            Assert.That(context["Title"], Is.EqualTo("Please triage"));
            Assert.That(context["From"], Is.EqualTo("alice@example.com"));
            Assert.That(context["MailUid"], Is.EqualTo("42"));
            Assert.That(context["WorkItemId"],
                Is.EqualTo(EmailTaskSourceAdapter.WorkItemIdFor("<msg-1@example.com>")));
        });
    }
}
