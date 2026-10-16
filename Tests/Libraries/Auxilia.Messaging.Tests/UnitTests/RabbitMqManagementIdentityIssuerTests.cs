using System.Net;
using System.Text;

namespace Auxilia.Messaging.Tests.UnitTests;

/// <summary>
/// The management-API issuer against a recording HTTP handler: the exact calls, rollback of a
/// half-provisioned user, idempotent revoke, and retry of transient failures.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class RabbitMqManagementIdentityIssuerTests
{
    private static readonly BrokerPermissions Permissions = new(
        "^own$", "^workflow\\.ingress$", "^own$",
        [new BrokerTopicPermission("workflow.views", "^id$", "^$")]);

    private sealed class RecordingHandler(Func<HttpRequestMessage, int, HttpStatusCode> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Body, string? Auth)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method, request.RequestUri!.AbsolutePath, body, request.Headers.Authorization?.ToString()));
            return new HttpResponseMessage(respond(request, Calls.Count)) { Content = new StringContent("") };
        }
    }

    private static RabbitMqManagementIdentityIssuer NewIssuer(RecordingHandler handler)
        => new(new HttpClient(handler),
            new RabbitMqManagementSettings(new Uri("http://broker:15672"), "admin", "secret"));

    [Test]
    public async Task IssueAsync_CreatesAnUntaggedUser_ThenPermissions_ThenTopicPermissions()
    {
        var handler = new RecordingHandler((_, _) => HttpStatusCode.Created);

        var password = await NewIssuer(handler).IssueAsync("auxilia-wf-abc", Permissions);

        Assert.That(password, Has.Length.GreaterThanOrEqualTo(24));
        Assert.That(handler.Calls.Select(c => (c.Method, c.Path)), Is.EqualTo(new[]
        {
            (HttpMethod.Put, "/api/users/auxilia-wf-abc"),
            (HttpMethod.Put, "/api/permissions/%2F/auxilia-wf-abc"),
            (HttpMethod.Put, "/api/topic-permissions/%2F/auxilia-wf-abc"),
        }));
        Assert.That(handler.Calls[0].Body, Does.Contain("\"tags\":\"\"").And.Contain(password),
            "the user gets NO tags — no management-UI access");
        Assert.That(handler.Calls[1].Body, Does.Contain("\"configure\":\"^own$\""));
        Assert.That(handler.Calls[2].Body, Does.Contain("\"exchange\":\"workflow.views\""));
        Assert.That(handler.Calls[0].Auth,
            Is.EqualTo("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:secret"))));
    }

    [Test]
    public async Task IssueAsync_EveryCallMintsAFreshPassword()
    {
        var handler = new RecordingHandler((_, _) => HttpStatusCode.NoContent);
        var issuer = NewIssuer(handler);

        var first = await issuer.IssueAsync("u", Permissions);
        var second = await issuer.IssueAsync("u", Permissions);

        Assert.That(first, Is.Not.EqualTo(second));
    }

    [Test]
    public void IssueAsync_PermissionFailure_RemovesTheHalfProvisionedUser()
    {
        var handler = new RecordingHandler((request, _) =>
            request.RequestUri!.AbsolutePath.StartsWith("/api/permissions") ? HttpStatusCode.BadRequest
            : HttpStatusCode.NoContent);

        Assert.ThrowsAsync<InvalidOperationException>(() => NewIssuer(handler).IssueAsync("u", Permissions));
        Assert.That(handler.Calls.Last().Method, Is.EqualTo(HttpMethod.Delete),
            "a failed issue must never leave a usable credential behind");
        Assert.That(handler.Calls.Last().Path, Is.EqualTo("/api/users/u"));
    }

    [Test]
    public async Task IssueAsync_TransientServerErrors_AreRetried()
    {
        var handler = new RecordingHandler((_, n) => n == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NoContent);

        await NewIssuer(handler).IssueAsync("u", Permissions);

        Assert.That(handler.Calls.Count(c => c.Path == "/api/users/u"), Is.EqualTo(2));
    }

    [Test]
    public async Task RevokeAsync_ReportsWhetherTheUserExisted()
    {
        var gone = new RecordingHandler((_, _) => HttpStatusCode.NotFound);
        var present = new RecordingHandler((_, _) => HttpStatusCode.NoContent);

        Assert.That(await NewIssuer(gone).RevokeAsync("u"), Is.False);
        Assert.That(await NewIssuer(present).RevokeAsync("u"), Is.True);
        Assert.That(present.Calls.Single().Method, Is.EqualTo(HttpMethod.Delete));
    }
}
