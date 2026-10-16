using Auxilia.Core.Api.Services;

namespace Auxilia.Core.Api.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public sealed class TerminalProxyCredentialTests
{
    [Test]
    public void ACredentialedEndpoint_BecomesTheUpstreamAuthorization()
    {
        var (address, authorization) = TerminalProxyService.SplitCredential("auxilia:s3cret@127.0.0.1:49152");

        Assert.That(address, Is.EqualTo("127.0.0.1:49152"));
        Assert.That(authorization, Is.EqualTo("Basic " + Convert.ToBase64String("auxilia:s3cret"u8.ToArray())));
    }

    [Test]
    public void APlainEndpoint_HasNoAuthorization()
        => Assert.That(TerminalProxyService.SplitCredential("auxilia-session-x:7681"),
            Is.EqualTo(("auxilia-session-x:7681", (string?)null)));
}
