using Auxilia.Adapters.Email;
using Auxilia.Slots.Email;
using Auxilia.Workflows.TaskSource;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Slots.Email.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
[NonParallelizable] // mutates WORKFLOW_CONTEXT__* process environment variables
public class EmailWorkItemsSlotHandlerTests
{
    private sealed class FakeMailboxClient : IMailboxClient
    {
        public List<(string To, string Subject, string Body, string InReplyToMessageId)> SentReplies { get; } = [];

        public Task<IReadOnlyList<InboundMail>> FetchUnseenAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<InboundMail>>([]);

        public Task MarkSeenAsync(uint uid, CancellationToken ct = default) => Task.CompletedTask;

        public Task SendReplyAsync(
            string to, string subject, string body, string inReplyToMessageId,
            CancellationToken ct = default)
        {
            SentReplies.Add((to, subject, body, inReplyToMessageId));
            return Task.CompletedTask;
        }
    }

    private static readonly string[] ContextVariables =
    [
        EmailWorkItemAccess.WorkItemIdVariable,
        EmailWorkItemAccess.TitleVariable,
        EmailWorkItemAccess.FromVariable,
        EmailWorkItemAccess.BodyVariable
    ];

    private FakeMailboxClient _mailbox = null!;
    private EmailWorkItemsSlotHandler _handler = null!;
    private EmailTaskSourceSettings? _factorySettings;

    [SetUp]
    public void SetUp()
    {
        _mailbox = new FakeMailboxClient();
        _factorySettings = null;
        _handler = new EmailWorkItemsSlotHandler
        {
            MailboxClientFactory = settings =>
            {
                _factorySettings = settings;
                return _mailbox;
            }
        };
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var name in ContextVariables)
            Environment.SetEnvironmentVariable(name, null);
    }

    private static SlotConfiguration Config(Dictionary<string, string>? settings = null)
        => new("email-work-items", settings ?? []);

    private IWorkItemAccess Resolve(SlotConfiguration? configuration = null)
    {
        var services = new ServiceCollection();
        _handler.Register(services, "work-items", typeof(IWorkItemAccess), configuration ?? Config());
        return services.BuildServiceProvider().GetRequiredService<IWorkItemAccess>();
    }

    // ── Registration ────────────────────────────────────────────────────────

    [Test]
    public void Register_WorkItemsSlot_RegistersNonKeyedIWorkItemAccess()
    {
        var services = new ServiceCollection();
        _handler.Register(services, "work-items", typeof(IWorkItemAccess), Config());

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService<IWorkItemAccess>();
        Assert.That(resolved, Is.Not.Null);
    }

    [Test]
    public void Register_UnknownSlot_Throws()
    {
        var services = new ServiceCollection();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            _handler.Register(services, "repository", typeof(object), Config()));
        Assert.That(ex!.Message, Does.Contain("repository"));
    }

    [Test]
    public void Register_MapsSlotSettingsToMailboxSettings()
    {
        var configuration = Config(new Dictionary<string, string>
        {
            ["ImapHost"] = "imap.example.test",
            ["ImapPort"] = "143",
            ["UseSsl"] = "false",
            ["Username"] = "reviewer@example.test",
            ["Password"] = "secret",
            ["SmtpHost"] = "smtp.example.test",
            ["SmtpPort"] = "2525",
            ["Folder"] = "Reviews"
        });

        _ = Resolve(configuration);

        Assert.That(_factorySettings, Is.Not.Null);
        Assert.That(_factorySettings!.ImapHost, Is.EqualTo("imap.example.test"));
        Assert.That(_factorySettings.ImapPort, Is.EqualTo(143));
        Assert.That(_factorySettings.UseSsl, Is.False);
        Assert.That(_factorySettings.Username, Is.EqualTo("reviewer@example.test"));
        Assert.That(_factorySettings.Password, Is.EqualTo("secret"));
        Assert.That(_factorySettings.SmtpHost, Is.EqualTo("smtp.example.test"));
        Assert.That(_factorySettings.SmtpPort, Is.EqualTo(2525));
        Assert.That(_factorySettings.Folder, Is.EqualTo("Reviews"));
    }

    [Test]
    public void Register_MissingOptionalSettings_FallBackToDefaults()
    {
        _ = Resolve(Config(new Dictionary<string, string> { ["ImapHost"] = "imap.example.test" }));

        Assert.That(_factorySettings, Is.Not.Null);
        Assert.That(_factorySettings!.ImapPort, Is.EqualTo(993));
        Assert.That(_factorySettings.UseSsl, Is.True);
        Assert.That(_factorySettings.SmtpPort, Is.EqualTo(587));
        Assert.That(_factorySettings.Folder, Is.EqualTo("INBOX"));
    }

    // ── PostCommentAsync ────────────────────────────────────────────────────

    [Test]
    public async Task PostCommentAsync_SendsReplyToOriginalSender()
    {
        Environment.SetEnvironmentVariable(EmailWorkItemAccess.FromVariable, "alice@example.test");
        Environment.SetEnvironmentVariable(EmailWorkItemAccess.TitleVariable, "Please review my change");
        Environment.SetEnvironmentVariable(EmailWorkItemAccess.WorkItemIdVariable, "mail-abc123");

        var access = Resolve();
        await access.PostCommentAsync("mail-abc123", "Looks good overall.");

        Assert.That(_mailbox.SentReplies, Has.Count.EqualTo(1));
        var reply = _mailbox.SentReplies[0];
        Assert.That(reply.To, Is.EqualTo("alice@example.test"));
        Assert.That(reply.Subject, Is.EqualTo("Re: Please review my change"));
        Assert.That(reply.Body, Does.StartWith("Looks good overall."));
        Assert.That(reply.Body, Does.Contain("mail-abc123"));
        Assert.That(reply.InReplyToMessageId, Is.Empty);
    }

    [Test]
    public void PostCommentAsync_WithoutFromContext_Throws()
    {
        var access = Resolve();
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await access.PostCommentAsync("mail-abc123", "comment"));
        Assert.That(ex!.Message, Does.Contain(EmailWorkItemAccess.FromVariable));
    }

    // ── GetWorkItemAsync / GetWorkItemsAsync ────────────────────────────────

    [Test]
    public async Task GetWorkItemAsync_AssemblesWorkItemFromContext()
    {
        Environment.SetEnvironmentVariable(EmailWorkItemAccess.WorkItemIdVariable, "mail-abc123");
        Environment.SetEnvironmentVariable(EmailWorkItemAccess.TitleVariable, "Please review my change");
        Environment.SetEnvironmentVariable(EmailWorkItemAccess.BodyVariable, "See the attached diff.");

        var access = Resolve();
        var item = await access.GetWorkItemAsync("mail-abc123");

        Assert.That(item, Is.Not.Null);
        Assert.That(item!.Id, Is.EqualTo("mail-abc123"));
        Assert.That(item.Title, Is.EqualTo("Please review my change"));
        Assert.That(item.Description, Is.EqualTo("See the attached diff."));
    }

    [Test]
    public async Task GetWorkItemsAsync_MapsOverGetWorkItemAsync()
    {
        Environment.SetEnvironmentVariable(EmailWorkItemAccess.WorkItemIdVariable, "mail-abc123");
        Environment.SetEnvironmentVariable(EmailWorkItemAccess.TitleVariable, "Please review my change");

        var access = Resolve();
        var items = await access.GetWorkItemsAsync(["mail-abc123", "mail-abc123"]);

        Assert.That(items, Has.Count.EqualTo(2));
        Assert.That(items, Has.All.Matches<WorkItem>(wi => wi.Id == "mail-abc123"));
    }
}
