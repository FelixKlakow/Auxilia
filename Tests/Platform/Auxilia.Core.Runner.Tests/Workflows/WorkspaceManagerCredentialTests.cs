using Auxilia.Core.Runner.Workflows;

namespace Auxilia.Core.Runner.Tests.Workflows;

/// <summary>
/// The warm cache's credential handling (ARCHITECTURE §9): the cache key ignores the
/// credential and the credential rides only as per-command git configuration.
/// </summary>
[TestFixture]
[Category("Unit")]
public class WorkspaceManagerCredentialTests
{
    [Test]
    public void CacheKeyFor_IgnoresTheCredential_SoATokenRotationReusesTheEntry()
    {
        var plain = WorkspaceManager.CacheKeyFor("https://dev.azure.com/org/project/_git/repo");
        var tokenOne = WorkspaceManager.CacheKeyFor("https://user:token-1@dev.azure.com/org/project/_git/repo");
        var tokenTwo = WorkspaceManager.CacheKeyFor("https://user:token-2@dev.azure.com/org/project/_git/repo");

        Assert.Multiple(() =>
        {
            Assert.That(tokenOne, Is.EqualTo(plain));
            Assert.That(tokenTwo, Is.EqualTo(plain));
            Assert.That(WorkspaceManager.CacheKeyFor("https://dev.azure.com/org/project/_git/other"),
                Is.Not.EqualTo(plain));
        });
    }

    [Test]
    public void CredentialConfigFor_TokenedUrl_RewritesTheStrippedUrlPerInvocation()
    {
        var config = WorkspaceManager.CredentialConfigFor("https://user:t0k@github.com/org/repo.git");

        Assert.That(config, Is.EqualTo(new[]
        {
            "-c", "url.https://user:t0k@github.com/org/repo.git.insteadOf=https://github.com/org/repo.git"
        }));
    }

    [Test]
    public void CredentialConfigFor_CredentialLessUrl_IsEmpty()
    {
        Assert.Multiple(() =>
        {
            Assert.That(WorkspaceManager.CredentialConfigFor("https://github.com/org/repo.git"), Is.Empty);
            Assert.That(WorkspaceManager.CredentialConfigFor(@"C:\repos\origin"), Is.Empty);
        });
    }
}
