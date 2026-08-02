using Auxilia.Core.Runner.Workflows;

namespace Auxilia.Core.Runner.Tests.Workflows;

[TestFixture]
[Category("Unit")]
public sealed class RepositoryCloneUrlTests
{
    [Test]
    public void WithCredentials_TokenOnly_InjectsTheTokenAsUserinfo()
        => Assert.That(
            RepositoryCloneUrl.WithCredentials("https://dev.azure.com/org/proj/_git/repo", null, "PAT"),
            Is.EqualTo("https://PAT@dev.azure.com/org/proj/_git/repo"));

    [Test]
    public void WithCredentials_UsernameAndToken_InjectsBoth()
        => Assert.That(
            RepositoryCloneUrl.WithCredentials("https://tfs.contoso.com/coll/_git/repo", "user", "PAT"),
            Is.EqualTo("https://user:PAT@tfs.contoso.com/coll/_git/repo"));

    [Test]
    public void WithCredentials_EscapesSpecialCharacters()
        => Assert.That(
            RepositoryCloneUrl.WithCredentials("https://host/repo", "dom\\usr", "p@ss:word"),
            Is.EqualTo("https://dom%5Cusr:p%40ss%3Aword@host/repo"));

    [Test]
    public void WithCredentials_ReplacesAnyExistingUserinfo()
        => Assert.That(
            RepositoryCloneUrl.WithCredentials("https://old:creds@host/repo", null, "PAT"),
            Is.EqualTo("https://PAT@host/repo"));

    [Test]
    public void WithCredentials_NonHttpUrl_IsLeftUnchanged()
        => Assert.That(
            RepositoryCloneUrl.WithCredentials("ssh://git@github.com/org/repo.git", "user", "PAT"),
            Is.EqualTo("ssh://git@github.com/org/repo.git"));
}
