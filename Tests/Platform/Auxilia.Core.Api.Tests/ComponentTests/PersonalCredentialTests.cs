using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Auxilia.Governance;
using Auxilia.Core.Contracts;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// A run works on the account of the person it runs for: every signed-in person owns their
/// personal connectors, a run binds its slot to one, and only that connector's owner (or a
/// grantee) may bind it. Two people's runs at the same time therefore each resolve their OWN
/// token; a token the owner replaces while the run goes on is what the run's next resolution
/// delivers (the agent re-reads its slot); and no token is stored with the run.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class PersonalCredentialTests : CoreApiComponentTestBase
{
    private const string WorkflowType = "personal-agent-wf";
    private const string Slot = "coding-agent";
    private const string ProviderType = "github-copilot-cli";

    [SetUp]
    public Task RegisterTypeAsync()
        => RegisterActiveTypeAsync(CreateClient(), WorkflowType, "docker://img@sha256:" + new string('b', 64));

    [Test]
    public async Task TwoPeoplesRunsAtTheSameTime_EachResolveTheirOwnToken()
    {
        var alice = await PersonAsync();
        var bob = await PersonAsync();
        var aliceConnector = await PersonalConnectorAsync(alice, "alice-gho-token");
        var bobConnector = await PersonalConnectorAsync(bob, "bob-gho-token");

        var aliceRun = await StartRunAsync(alice, aliceConnector);
        var bobRun = await StartRunAsync(bob, bobConnector);

        // Both runs are live at once; each resolves its own slot, and gets its own person's token.
        var tokens = await Task.WhenAll(ResolveTokenAsync(bobRun), ResolveTokenAsync(aliceRun));
        Assert.That(tokens, Is.EqualTo(new[] { "bob-gho-token", "alice-gho-token" }));
    }

    [Test]
    public async Task NobodyRunsOnSomeoneElsesPersonalConnector()
    {
        var alice = await PersonAsync();
        var bob = await PersonAsync();
        var aliceConnector = await PersonalConnectorAsync(alice, "alice-gho-token");

        var response = await bob.PostAsJsonAsync("/api/runs", RunBinding(aliceConnector));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(MessageBus.PublishedMessages.Select(m => m.Message).OfType<RunWorkflowCommand>(), Is.Empty,
            "no run is dispatched on a connector its starter may not use");
    }

    [Test]
    public async Task ATokenTheOwnerReplacesDuringTheRun_IsWhatTheRunResolvesNext()
    {
        var alice = await PersonAsync();
        var connector = await PersonalConnectorAsync(alice, "token-at-start");
        var run = await StartRunAsync(alice, connector);
        Assert.That(await ResolveTokenAsync(run), Is.EqualTo("token-at-start"));

        var replaced = await alice.PutAsJsonAsync($"/api/connectors/{connector}", new UpdateConnector(
            Settings: new Dictionary<string, string>
            {
                ["token"] = "token-replaced-mid-run",
                ["token_expires_at"] = DateTimeOffset.UtcNow.AddHours(8).ToString("O"),
            }));
        Assert.That(replaced.StatusCode, Is.EqualTo(HttpStatusCode.OK), await replaced.Content.ReadAsStringAsync());

        var settings = await ResolveSettingsAsync(run);
        Assert.That(settings["token"], Is.EqualTo("token-replaced-mid-run"));
        Assert.That(settings, Does.ContainKey("token_expires_at"), "the declared expiry travels with the token");
    }

    [Test]
    public async Task NoTokenIsStoredWithTheRun_OrSentInItsDispatch()
    {
        var alice = await PersonAsync();
        var connector = await PersonalConnectorAsync(alice, "never-stored-token");
        var run = await StartRunAsync(alice, connector);
        await ResolveTokenAsync(run);

        var stored = new[]
        {
            await alice.GetStringAsync($"/api/runs/{run.RunId}"),
            await alice.GetStringAsync($"/api/runs/{run.RunId}/views"),
            await alice.GetStringAsync($"/api/connectors/{connector}"),
            JsonSerializer.Serialize(MessageBus.PublishedMessages.Select(m => m.Message).OfType<RunWorkflowCommand>()),
            await alice.GetStringAsync("/api/audit?take=500"),
        };
        Assert.That(stored, Has.None.Contains("never-stored-token"));
    }

    // ------------------------------------------------------------------ helpers

    private sealed record LiveRun(Guid RunId, Guid CommandId, string ResolutionToken);

    /// <summary>A signed-in person (administrators bind any catalogued provider; personal connectors stay their owner's).</summary>
    private async Task<HttpClient> PersonAsync()
    {
        var directory = Factory.Services.GetRequiredService<PrincipalDirectory>();
        var (principal, apiKey) = await directory.CreateApiKeyPrincipalAsync($"person-{Guid.NewGuid():N}");
        await directory.AssignRoleAsync(principal.Id, "Administrator");
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    private static async Task<Guid> PersonalConnectorAsync(HttpClient owner, string token)
    {
        var response = await owner.PostAsJsonAsync("/api/connectors",
            new CreateConnector("copilot", ProviderType, new Dictionary<string, string> { ["token"] = token }));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        var connector = (await response.Content.ReadFromJsonAsync<Connector>())!;
        Assert.That(connector.Scope, Is.EqualTo(ResourceScope.Personal));
        return connector.Id;
    }

    private static RunRequest RunBinding(Guid connectorId)
        => new(WorkflowType, SlotBindings: [new SlotBinding(Slot, ProviderType, connectorId)]);

    private async Task<LiveRun> StartRunAsync(HttpClient starter, Guid connectorId)
    {
        var response = await starter.PostAsJsonAsync("/api/runs", RunBinding(connectorId));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        var accepted = (await response.Content.ReadFromJsonAsync<RunAccepted>())!;
        var command = MessageBus.PublishedMessages.Select(m => m.Message).OfType<RunWorkflowCommand>()
            .Single(c => c.CommandId == accepted.CommandId);
        return new LiveRun(accepted.RunId, command.CommandId, command.ResolutionToken!);
    }

    private async Task<string> ResolveTokenAsync(LiveRun run) => (await ResolveSettingsAsync(run))["token"];

    /// <summary>What the runner asks for at slot activation: the slot's settings, encrypted for this instance.</summary>
    private async Task<Dictionary<string, string>> ResolveSettingsAsync(LiveRun run)
    {
        using var rsa = RSA.Create(2048);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/internal/runs/{run.CommandId}/resolve-slot")
        {
            Content = JsonContent.Create(new ResolveSlotRequest(Slot, Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo())))
        };
        request.Headers.Add("X-Resolution-Token", run.ResolutionToken);
        var response = await CreateAnonymousClient().SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        var credential = (await response.Content.ReadFromJsonAsync<ResolvedSlotCredential>())!;
        var plain = rsa.Decrypt(Convert.FromBase64String(credential.EncryptedSettings), RSAEncryptionPadding.OaepSHA256);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(plain))!;
    }
}
