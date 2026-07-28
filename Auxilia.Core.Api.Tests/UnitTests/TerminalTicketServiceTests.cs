using Auxilia.Core.Api.Services;

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

    [Test]
    public void Issue_ProducesATicket_ValidOnlyForItsRun()
    {
        var clock = new FakeTimeProvider();
        var service = new TerminalTicketService(clock);
        var runId = Guid.NewGuid();

        var (ticket, expires) = service.Issue(runId);

        Assert.Multiple(() =>
        {
            Assert.That(ticket, Has.Length.EqualTo(64), "32 random bytes, hex-encoded");
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
        var service = new TerminalTicketService(clock);
        var runId = Guid.NewGuid();
        var (ticket, _) = service.Issue(runId);

        Assert.That(service.Validate(ticket, runId), Is.True);
        Assert.That(service.Validate(ticket, runId), Is.True,
            "page, assets, and websocket are separate requests — the ticket must serve them all");

        clock.Advance(TerminalTicketService.TimeToLive + TimeSpan.FromSeconds(1));
        Assert.That(service.Validate(ticket, runId), Is.False);
    }

    [Test]
    public void Issue_PrunesExpiredTickets()
    {
        var clock = new FakeTimeProvider();
        var service = new TerminalTicketService(clock);
        var (expired, _) = service.Issue(Guid.NewGuid());

        clock.Advance(TerminalTicketService.TimeToLive + TimeSpan.FromSeconds(1));
        service.Issue(Guid.NewGuid());

        Assert.That(service.Validate(expired, Guid.NewGuid()), Is.False);
    }
}
