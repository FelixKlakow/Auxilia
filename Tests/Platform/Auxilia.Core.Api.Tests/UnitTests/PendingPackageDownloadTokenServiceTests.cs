using System.Security.Cryptography;
using Auxilia.Core.Api.Services;
using Auxilia.PlatformData;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Tests.UnitTests;

/// <summary>
/// The approval pipeline's scoped package-download token: bound to ONE workflow type, valid for
/// the approval evaluation window, self-validating across Core.Api nodes sharing the
/// settings-protection key.
/// </summary>
[TestFixture, Category("Unit")]
public sealed class PendingPackageDownloadTokenServiceTests
{
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static readonly string SharedKey =
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static PendingPackageDownloadTokenService Service(
        FakeTimeProvider clock, string? protectionKey = null, int timeoutSeconds = 900)
        => new(clock,
            new PlatformDataSettings { ProtectionKeyBase64 = protectionKey ?? SharedKey },
            Options.Create(new CoreApiSettings
            {
                ApprovalVerdictWorkflow = new ApprovalVerdictWorkflowSettings { TimeoutSeconds = timeoutSeconds }
            }));

    [Test]
    public void Issue_ProducesAToken_ValidOnlyForItsType()
    {
        var clock = new FakeTimeProvider();
        var service = Service(clock);

        var token = service.Issue("under-review");

        Assert.Multiple(() =>
        {
            Assert.That(service.Validate(token, "under-review"), Is.True);
            Assert.That(service.Validate(token, "other-type"), Is.False,
                "the token may only fetch THE pending package it was minted for");
            Assert.That(service.Validate("forged", "under-review"), Is.False);
            Assert.That(service.Validate(null, "under-review"), Is.False);
        });
    }

    [Test]
    public void Token_ExpiresWithTheEvaluationWindow()
    {
        var clock = new FakeTimeProvider();
        var service = Service(clock, timeoutSeconds: 900);
        var token = service.Issue("under-review");

        clock.Advance(TimeSpan.FromSeconds(900));
        Assert.That(service.Validate(token, "under-review"), Is.True,
            "the token must outlive the verdict timeout (grace included)");

        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.That(service.Validate(token, "under-review"), Is.False,
            "past timeout + grace the token is dead");
    }

    [Test]
    public void Token_MintedOnOneNode_ValidatesOnAnother_WithTheSharedKey()
    {
        var clock = new FakeTimeProvider();
        var nodeA = Service(clock);
        var nodeB = Service(clock);

        Assert.That(nodeB.Validate(nodeA.Issue("under-review"), "under-review"), Is.True,
            "no node-local state — the pipeline may mint on one node, the runner hit another");
    }

    [Test]
    public void Token_DoesNotValidate_AcrossDifferentKeys()
    {
        var clock = new FakeTimeProvider();
        var stranger = Service(clock, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

        Assert.That(stranger.Validate(Service(clock).Issue("under-review"), "under-review"), Is.False);
    }

    [Test]
    public void TamperedExpiry_IsRejected()
    {
        var clock = new FakeTimeProvider();
        var service = Service(clock);
        var parts = service.Issue("under-review").Split('.');
        var extended = $"{long.Parse(parts[0]) + TimeSpan.TicksPerDay}.{parts[1]}";

        Assert.That(service.Validate(extended, "under-review"), Is.False,
            "extending the expiry must break the MAC");
    }
}
