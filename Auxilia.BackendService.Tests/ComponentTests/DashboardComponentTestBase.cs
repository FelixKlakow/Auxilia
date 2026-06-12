using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Auxilia.Governance;
using Auxilia.Messaging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Auxilia.BackendService.Tests.ComponentTests;

/// <summary>
/// Shared factory for dashboard component tests: full BackendService via
/// WebApplicationFactory with InMemory platform data, AES-GCM settings protection,
/// <see cref="FakeMessageBusClient"/>, and a bootstrap administrator.
/// </summary>
public abstract class DashboardComponentTestBase
{
    protected const string AdminUsername = "bootstrap-admin";
    protected const string AdminPassword = "bootstrap-pw-1";

    protected WebApplicationFactory<Program> Factory = null!;
    protected FakeMessageBusClient MessageBus = null!;

    [OneTimeSetUp]
    public void CreateFactory()
    {
        MessageBus = new FakeMessageBusClient();
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // UseSetting (not ConfigureAppConfiguration) so the values are already visible
            // while Program.cs binds settings during the WebApplicationBuilder phase.
            builder.UseSetting("PlatformData:Backend", "InMemory");
            builder.UseSetting("PlatformData:ProtectionKeyBase64",
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting("Governance:BootstrapAdminUsername", AdminUsername);
            builder.UseSetting("Governance:BootstrapAdminPassword", AdminPassword);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMessageBusClient>();
                services.AddSingleton<IMessageBusClient>(MessageBus);
            });
        });
    }

    [OneTimeTearDown]
    public void DisposeFactory() => Factory.Dispose();

    protected HttpClient CreateClient() => Factory.CreateClient(
        new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>Logs in and returns the session cookie plus the platform principal ID.</summary>
    protected static async Task<(string Cookie, Guid PrincipalId)> LoginAsync(
        HttpClient client, string username, string password)
    {
        var response = await client.PostAsJsonAsync("/auth/login",
            new { Username = username, Password = password });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"Login of '{username}' must succeed.");

        var cookie = response.Headers.GetValues("Set-Cookie").First().Split(';')[0];
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var principalId = body.RootElement.GetProperty("principalId").GetGuid();
        return (cookie, principalId);
    }

    /// <summary>Creates a human principal with the given role; returns its ID and username.</summary>
    protected async Task<(Guid PrincipalId, string Username, string Password)> CreatePrincipalAsync(string role)
    {
        var directory = Factory.Services.GetRequiredService<PrincipalDirectory>();
        var username = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}";
        const string password = "test-pw-12345";
        var principal = await directory.CreateHumanAsync($"{role} test user", username, password);
        await directory.AssignRoleAsync(principal.Id, role);
        return (principal.Id, username, password);
    }

    protected static async Task<string> GetHtmlAsync(HttpClient client, string path, string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", cookie);
        var response = await client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"GET {path} must return 200.");
        return await response.Content.ReadAsStringAsync();
    }
}
