using System.Security.Claims;
using Auxilia.Core.Api.Auth;

namespace Auxilia.Core.Api.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public sealed class CoreClaimsTests
{
    private static ClaimsPrincipal PrincipalWith(params Claim[] claims)
        => new(new ClaimsIdentity(claims, "test"));

    [Test]
    public void HasGroupOverage_IsTrue_WhenHasGroupsClaimSet()
        => Assert.That(CoreClaims.HasGroupOverage(PrincipalWith(new Claim("hasgroups", "true"))), Is.True);

    [Test]
    public void HasGroupOverage_IsTrue_WhenClaimNamesPointAtGroups()
        => Assert.That(CoreClaims.HasGroupOverage(PrincipalWith(
            new Claim("_claim_names", "{\"groups\":\"src1\"}"))), Is.True);

    [Test]
    public void HasGroupOverage_IsFalse_WhenGroupsAreCarriedInline()
        => Assert.That(CoreClaims.HasGroupOverage(PrincipalWith(
            new Claim("groups", "group-a"), new Claim("groups", "group-b"))), Is.False);

    [Test]
    public void HasGroupOverage_IsFalse_WhenNoGroupSignalsPresent()
        => Assert.That(CoreClaims.HasGroupOverage(PrincipalWith(new Claim("name", "Ada"))), Is.False);
}
