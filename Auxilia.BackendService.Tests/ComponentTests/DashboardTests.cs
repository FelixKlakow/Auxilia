using System.Net;
using System.Net.Http.Json;
using Auxilia.Messaging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Auxilia.BackendService.Tests.ComponentTests;

/// <summary>
/// Boots the full BackendService via WebApplicationFactory: InMemory platform data,
/// fake message bus (registered before the lazy RabbitMqClient singleton is resolved),
/// and a bootstrap administrator seeded through configuration.
/// </summary>
[TestFixture]
[Category("Component")]
public class DashboardTests
{
    private const string AdminUsername = "bootstrap-admin";
    private const string AdminPassword = "bootstrap-pw-1";

    private WebApplicationFactory<Program> _factory = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // UseSetting (not ConfigureAppConfiguration) so the values are already visible
            // while Program.cs binds settings during the WebApplicationBuilder phase.
            builder.UseSetting("PlatformData:Backend", "InMemory");
            builder.UseSetting("Governance:BootstrapAdminUsername", AdminUsername);
            builder.UseSetting("Governance:BootstrapAdminPassword", AdminPassword);
            builder.ConfigureServices(services =>
            {
                // Program registers IMessageBusClient as a lazy factory lambda connecting to
                // RabbitMQ; replacing the descriptor here means it is never resolved.
                services.RemoveAll<IMessageBusClient>();
                services.AddSingleton<IMessageBusClient>(new FakeMessageBusClient());
            });
        });
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => _factory.Dispose();

    private HttpClient CreateClient() => _factory.CreateClient(
        new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<string> LoginAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/auth/login",
            new { Username = AdminUsername, Password = AdminPassword });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var setCookie = response.Headers.GetValues("Set-Cookie").First();
        return setCookie.Split(';')[0];
    }

    [Test]
    public async Task Login_WithBootstrapAdmin_Returns200AndSessionCookie()
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync("/auth/login",
            new { Username = AdminUsername, Password = AdminPassword });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Headers.TryGetValues("Set-Cookie", out var cookies), Is.True,
            "Login must set the session cookie.");
        Assert.That(cookies!.Any(c => c.StartsWith("auxilia.session=")), Is.True);
    }

    [Test]
    public async Task Login_WithWrongPassword_Returns401()
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync("/auth/login",
            new { Username = AdminUsername, Password = "wrong-password" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Health_IsStillAvailable()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/health");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Root_Unauthenticated_Returns401()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task LoginPage_Unauthenticated_Returns200()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/login");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task FormLogin_WithBootstrapAdmin_SetsCookieAndRedirectsToRoot()
    {
        using var client = CreateClient();

        var response = await client.PostAsync("/auth/login-form", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["username"] = AdminUsername,
                ["password"] = AdminPassword
            }));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        Assert.That(response.Headers.Location?.OriginalString, Is.EqualTo("/"));
        Assert.That(response.Headers.TryGetValues("Set-Cookie", out var cookies), Is.True);
        Assert.That(cookies!.Any(c => c.StartsWith("auxilia.session=")), Is.True);
    }

    [Test]
    public async Task FormLogin_WithWrongPassword_RedirectsBackToLoginWithError()
    {
        using var client = CreateClient();

        var response = await client.PostAsync("/auth/login-form", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["username"] = AdminUsername,
                ["password"] = "wrong-password"
            }));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        Assert.That(response.Headers.Location?.OriginalString, Is.EqualTo("/login?error=1"));
    }

    [Test]
    public async Task Root_AfterLogin_Returns200Html()
    {
        using var client = CreateClient();
        var sessionCookie = await LoginAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Cookie", sessionCookie);
        var response = await client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var html = await response.Content.ReadAsStringAsync();
        Assert.That(html, Does.Contain("<html"));
        Assert.That(html, Does.Contain("Runs"));
    }
}
