using Auxilia.Core.Api.Services;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Core.Api.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public sealed class ResolutionTokensTests
{
    [Test]
    public void Matches_AcceptsTheOriginalToken_AndRejectsAnyOther()
    {
        var hash = ResolutionTokens.Hash("the-token");
        Assert.Multiple(() =>
        {
            Assert.That(hash, Is.Not.EqualTo("the-token"), "the digest must not be the token");
            Assert.That(ResolutionTokens.Matches(hash, "the-token"), Is.True);
            Assert.That(ResolutionTokens.Matches(hash, "the-token2"), Is.False);
            Assert.That(ResolutionTokens.Matches(hash, ""), Is.False);
        });
    }

    [Test]
    public void Redacted_DropsTheToken_AndScrubsThePackageUrlQuery()
    {
        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "wf",
            "https://core.example/api/workflow-types/wf/package?runId=abc&token=secret-token",
            new Dictionary<string, string> { ["k"] = "v" },
            ResolutionToken: "secret-token");

        var redacted = ResolutionTokens.Redacted(command);
        Assert.Multiple(() =>
        {
            Assert.That(redacted.ResolutionToken, Is.Null);
            Assert.That(redacted.WorkflowPackageUri, Does.Not.Contain("secret-token"));
            Assert.That(redacted.WorkflowPackageUri, Does.Contain("?runId=abc&token=redacted"),
                "the URL shape survives; only the capability is gone");
            Assert.That(redacted.CommandId, Is.EqualTo(command.CommandId));
            Assert.That(redacted.Context["k"], Is.EqualTo("v"));
        });
    }

    [Test]
    public void Redacted_LeavesAPlainPackageUriAlone()
    {
        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "wf", "docker://image:tag",
            new Dictionary<string, string>(), ResolutionToken: "t");

        Assert.That(ResolutionTokens.Redacted(command).WorkflowPackageUri, Is.EqualTo("docker://image:tag"));
    }
}
