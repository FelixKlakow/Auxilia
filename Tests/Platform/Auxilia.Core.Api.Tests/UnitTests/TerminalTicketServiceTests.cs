using System.Security.Cryptography;
using Auxilia.Core.Api.Services;
using Auxilia.PlatformData;

namespace Auxilia.Core.Api.Tests.UnitTests;

[TestFixture, Category("Unit")]
public sealed class TerminalTicketServiceTests
{
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static readonly string SharedKey =
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static TerminalTicketService Service(FakeTimeProvider clock, string? protectionKey = null)
        => new(clock, new PlatformDataSettings { ProtectionKeyBase64 = protectionKey ?? SharedKey });

    [Test]
    public void Issue_ProducesATicket_ValidOnlyForItsRun()
    {
        var clock = new FakeTimeProvider();
        var service = Service(clock);
        var runId = Guid.NewGuid();

        var (ticket, expires) = service.Issue(runId);

        Assert.Multiple(() =>
        {
            Assert.That(expires, Is.EqualTo(clock.GetUtcNow() + TerminalTicketService.TimeToLive));
            Assert.That(service.Validate(ticket, runId), Is.True);
            Assert.That(service.Validate(ticket, Guid.NewGuid()), Is.False,
                "a ticket is bound to exactly one run");
            Assert.That(service.Validate("forged", runId), Is.False);
            Assert.That(service.Validate(null, runId), Is.False);
        });
    }

    [Test]
    public void Validate_IsMultiUseWithinTheWindow_AndDeadAfterExpiry()
    {
        var clock = new FakeTimeProvider();
        var service = Service(clock);
        var runId = Guid.NewGuid();
        var (ticket, _) = service.Issue(runId);

        Assert.That(service.Validate(ticket, runId), Is.True);
        Assert.That(service.Validate(ticket, runId), Is.True,
            "page, assets, and websocket are separate requests — the ticket must serve them all");

        clock.Advance(TerminalTicketService.TimeToLive + TimeSpan.FromSeconds(1));
        Assert.That(service.Validate(ticket, runId), Is.False);
    }

    [Test]
    public void Ticket_MintedOnOneNode_ValidatesOnAnother_WithTheSharedKey()
    {
        // Two service instances = two Core.Api nodes behind a load balancer sharing the
        // settings-protection key. The ticket must be portable between them.
        var clock = new FakeTimeProvider();
        var nodeA = Service(clock);
        var nodeB = Service(clock);
        var runId = Guid.NewGuid();

        var (ticket, _) = nodeA.Issue(runId);

        Assert.That(nodeB.Validate(ticket, runId), Is.True,
            "a ticket minted on node A must validate on node B — no node-local state");
    }

    [Test]
    public void Ticket_DoesNotValidate_AcrossDifferentKeys()
    {
        var clock = new FakeTimeProvider();
        var nodeA = Service(clock);
        var stranger = Service(clock,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var runId = Guid.NewGuid();

        var (ticket, _) = nodeA.Issue(runId);

        Assert.That(stranger.Validate(ticket, runId), Is.False,
            "a foreign deployment's key must reject the ticket");
    }

    [Test]
    public void TamperedExpiry_IsRejected()
    {
        var clock = new FakeTimeProvider();
        var service = Service(clock);
        var runId = Guid.NewGuid();
        var (ticket, _) = service.Issue(runId);

        var parts = ticket.Split('.');
        var extended = $"{parts[0]}.{long.Parse(parts[1]) + TimeSpan.TicksPerDay}.{parts[2]}";

        Assert.That(service.Validate(extended, runId), Is.False,
            "extending the expiry must break the MAC");
    }
}
