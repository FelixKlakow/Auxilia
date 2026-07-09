using Auxilia.Slots.GitHub;

namespace Auxilia.Slots.GitHub.Tests.UnitTests;

[TestFixture, Category("Unit")]
public sealed class GitHubRepositoryOptionsTests
{
    [Test]
    public void FromSettings_ParsesOwnerAndRepositoryFromUrl()
    {
        var options = GitHubRepositoryOptions.FromSettings(new Dictionary<string, string>
        {
            ["RepositoryUrl"] = "https://github.com/acme/widget"
        });

        Assert.Multiple(() =>
        {
            Assert.That(options.Owner, Is.EqualTo("acme"));
            Assert.That(options.Repository, Is.EqualTo("widget"));
        });
    }

    [TestCase("https://github.com/acme/widget.git")]
    [TestCase("https://github.com/acme/widget/")]
    public void FromSettings_NormalizesUrlVariants(string url)
    {
        var options = GitHubRepositoryOptions.FromSettings(new Dictionary<string, string>
        {
            ["RepositoryUrl"] = url
        });

        Assert.Multiple(() =>
        {
            Assert.That(options.Owner, Is.EqualTo("acme"));
            Assert.That(options.Repository, Is.EqualTo("widget"));
        });
    }

    [Test]
    public void FromSettings_AppliesDefaultsAndOverrides()
    {
        var defaults = GitHubRepositoryOptions.FromSettings(new Dictionary<string, string>
        {
            ["RepositoryUrl"] = "https://github.com/acme/widget"
        });
        var overridden = GitHubRepositoryOptions.FromSettings(new Dictionary<string, string>
        {
            ["RepositoryUrl"] = "https://github.com/acme/widget",
            ["Branch"] = "develop",
            ["Token"] = "github_pat_test"
        });

        Assert.Multiple(() =>
        {
            Assert.That(defaults.Branch, Is.EqualTo("main"));
            Assert.That(defaults.Token, Is.Null);
            Assert.That(overridden.Branch, Is.EqualTo("develop"));
            Assert.That(overridden.Token, Is.EqualTo("github_pat_test"));
        });
    }

    [Test]
    public void FromSettings_MissingRepositoryUrl_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => GitHubRepositoryOptions.FromSettings(new Dictionary<string, string>()));
    }

    [TestCase("not a url")]
    [TestCase("https://github.com/acme")]
    public void FromSettings_UnparseableUrl_Throws(string url)
    {
        Assert.Throws<InvalidOperationException>(
            () => GitHubRepositoryOptions.FromSettings(new Dictionary<string, string>
            {
                ["RepositoryUrl"] = url
            }));
    }
}
