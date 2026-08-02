using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Auxilia.SystemTestSuite.EmailAdapter;

/// <summary>
/// Shared environment for email-adapter system tests: one GreenMail container (public image,
/// no local docker build) offering plain IMAP (3143) and SMTP (3025). Auth is disabled so any
/// login works and recipient accounts are auto-created on delivery.
/// </summary>
[SetUpFixture]
public class EmailAdapterEnvironment
{
    private const string GreenMailImage = "greenmail/standalone:2.1.3";
    private const int ImapPort = 3143;
    private const int SmtpPort = 3025;

    private IContainer _greenMail = null!;

    public static string Host { get; private set; } = null!;
    public static int MappedImap { get; private set; }
    public static int MappedSmtp { get; private set; }

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _greenMail = new ContainerBuilder(GreenMailImage)
            // hostname=0.0.0.0 is essential: without it GreenMail binds to the container's
            // loopback only and the mapped host ports never reach it.
            .WithEnvironment("GREENMAIL_OPTS",
                "-Dgreenmail.setup.test.all -Dgreenmail.hostname=0.0.0.0 " +
                "-Dgreenmail.auth.disabled -Dgreenmail.verbose")
            .WithPortBinding(ImapPort, assignRandomHostPort: true)
            .WithPortBinding(SmtpPort, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(ImapPort))
            .Build();
        await _greenMail.StartAsync();

        Host = _greenMail.Hostname;
        MappedImap = _greenMail.GetMappedPublicPort(ImapPort);
        MappedSmtp = _greenMail.GetMappedPublicPort(SmtpPort);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_greenMail is not null)
        {
            await _greenMail.StopAsync();
            await _greenMail.DisposeAsync();
        }
    }
}
