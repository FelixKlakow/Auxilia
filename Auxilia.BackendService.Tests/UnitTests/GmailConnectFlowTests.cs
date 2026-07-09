using Auxilia.BackendService.Dashboard.Connect;

namespace Auxilia.BackendService.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class GmailConnectFlowTests
{
    private readonly GmailConnectFlow _sut = new();

    [Test]
    public async Task Begin_PointsAtGoogleAppPasswords_AndExpectsAPasteBack()
    {
        var start = await _sut.BeginAsync();

        Assert.Multiple(() =>
        {
            Assert.That(start.AuthorizeUrl, Is.EqualTo("https://myaccount.google.com/apppasswords"));
            Assert.That(start.PasteRequired, Is.True);
        });
    }

    [Test]
    public async Task Complete_StripsTheDisplayGrouping_GoogleShowsAppPasswordsWithSpaces()
    {
        var start = await _sut.BeginAsync();

        var password = await _sut.CompleteAsync(start.State, "abcd efgh ijkl mnop");

        Assert.That(password, Is.EqualTo("abcdefghijklmnop"));
    }

    [Test]
    public async Task Complete_WithoutAPaste_IsPending_NotAnError()
    {
        var start = await _sut.BeginAsync();

        Assert.ThrowsAsync<ConnectPendingException>(() => _sut.CompleteAsync(start.State, "  "));
    }
}
