using Auxilia.Core.Api.Auth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Tests.UnitTests;

/// <summary>
/// Unit tests for the per-user bearer protect/unprotect + expiry logic, over an ephemeral
/// DataProtection provider (the same primitive that protects the session cookie).
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class UserBearerTokenServiceTests
{
    private static UserBearerTokenService NewService(int lifetimeMinutes = 30, IDataProtectionProvider? shared = null)
        => new(
            shared ?? new EphemeralDataProtectionProvider(),
            TimeProvider.System,
            Options.Create(new CoreSecuritySettings { UserTokenLifetimeMinutes = lifetimeMinutes }));

    [Test]
    public void Issue_ThenValidate_RoundTripsThePrincipal_WithFutureExpiry()
    {
        var service = NewService();
        var principalId = Guid.NewGuid();

        var (token, expiresUtc) = service.Issue(principalId);

        Assert.Multiple(() =>
        {
            Assert.That(service.Validate(token), Is.EqualTo(principalId));
            Assert.That(expiresUtc, Is.GreaterThan(DateTimeOffset.UtcNow));
            Assert.That(token, Does.StartWith("auxu_"));
            Assert.That(token, Does.Not.Contain(principalId.ToString("D")), "The principal id must not be readable.");
        });
    }

    [Test]
    public void Validate_ReturnsNull_ForAnExpiredToken()
    {
        var service = NewService();
        var expired = service.Protect(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.That(service.Validate(expired), Is.Null);
    }

    [Test]
    public void Validate_ReturnsNull_ForATamperedToken()
    {
        var service = NewService();
        var (token, _) = service.Issue(Guid.NewGuid());

        // Flip a character in the protected payload (past the auxu_ prefix).
        var i = token.Length - 2;
        var tampered = token[..i] + (token[i] == 'A' ? 'B' : 'A') + token[(i + 1)..];

        Assert.That(service.Validate(tampered), Is.Null);
    }

    [Test]
    public void Validate_ReturnsNull_ForANonUserToken()
    {
        var service = NewService();
        Assert.Multiple(() =>
        {
            Assert.That(service.Validate("aux_some-api-key"), Is.Null);
            Assert.That(service.Validate("not-a-token"), Is.Null);
            Assert.That(service.Validate(""), Is.Null);
            Assert.That(service.Validate(null), Is.Null);
        });
    }

    [Test]
    public void Validate_ReturnsNull_ForATokenProtectedUnderADifferentKey()
    {
        var issuer = NewService();
        var otherKeyValidator = NewService(); // a distinct EphemeralDataProtectionProvider → different keys
        var (token, _) = issuer.Issue(Guid.NewGuid());

        Assert.That(otherKeyValidator.Validate(token), Is.Null,
            "A token signed under a different DataProtection key ring must not validate.");
    }

    [Test]
    public void Issue_HonoursTheConfiguredLifetime()
    {
        var (_, expiresUtc) = NewService(lifetimeMinutes: 15).Issue(Guid.NewGuid());
        Assert.That(expiresUtc, Is.EqualTo(DateTimeOffset.UtcNow.AddMinutes(15)).Within(TimeSpan.FromSeconds(30)));
    }
}
