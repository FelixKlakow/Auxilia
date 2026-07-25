using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Auxilia.Core.Contracts;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// Component tests for the Core-side JIT credential resolution endpoint: a credentialed slot is
/// resolved and RSA-encrypted by the Core (never the runner), authorized only by the run-scoped
/// resolution token, and plaintext secrets never leave the Core.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class SlotResolutionTests : CoreApiComponentTestBase
{
    private const string Secret = "super-secret-value";

    /// <summary>Creates a connector + a config bound to it, dispatches, and returns (runId, token).</summary>
    private async Task<(Guid RunId, string Token)> DispatchCredentialedRunAsync(HttpClient authed)
    {
        var connResp = await authed.PostAsJsonAsync("/api/connectors",
            new CreateConnector("gh", "github", new Dictionary<string, string> { ["token"] = Secret }));
        var connector = await connResp.Content.ReadFromJsonAsync<Connector>();

        var cfgResp = await authed.PostAsJsonAsync("/api/configurations", new CreateRunConfiguration(
            Name: "cfg-" + Guid.NewGuid().ToString("N"),
            WorkflowType: "credentialed-wf",
            PackageUri: "docker://img",
            SlotBindings: new List<SlotBinding> { new("sc", "github", connector!.Id) }));
        var config = await cfgResp.Content.ReadFromJsonAsync<RunConfiguration>();

        var runResp = await authed.PostAsync($"/api/configurations/{config!.Id}/run", null);
        Assert.That(runResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var command = MessageBus.PublishedMessages
            .Where(m => m.Topic == "workflow.run-commands")
            .Select(m => m.Message).OfType<RunWorkflowCommand>().Single();
        Assert.That(command.ResolutionToken, Is.Not.Null.And.Not.Empty);
        return (command.CommandId, command.ResolutionToken!);
    }

    private static async Task<HttpResponseMessage> ResolveAsync(
        HttpClient client, Guid runId, string token, string slotName, string publicKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/internal/runs/{runId}/resolve-slot")
        {
            Content = JsonContent.Create(new ResolveSlotRequest(slotName, publicKey))
        };
        request.Headers.Add("X-Resolution-Token", token);
        return await client.SendAsync(request);
    }

    [Test]
    public async Task ResolveSlot_WithRunToken_ReturnsCiphertext_AndNeverLeaksPlaintext()
    {
        var (runId, token) = await DispatchCredentialedRunAsync(CreateClient());

        using var rsa = RSA.Create(2048);
        var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

        // Deliberately anonymous — the runner authenticates with the run-scoped token, not a principal key.
        var response = await ResolveAsync(CreateAnonymousClient(), runId, token, "sc", publicKey);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var raw = await response.Content.ReadAsStringAsync();
        Assert.That(raw, Does.Not.Contain(Secret), "Only ciphertext may leave the Core.");

        var credential = JsonSerializer.Deserialize<ResolvedSlotCredential>(
            raw, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.That(credential.ProviderType, Is.EqualTo("github"));

        var plain = rsa.Decrypt(Convert.FromBase64String(credential.EncryptedSettings), RSAEncryptionPadding.OaepSHA256);
        var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(plain))!;
        Assert.That(settings["token"], Is.EqualTo(Secret));
    }

    [Test]
    public async Task ResolveSlot_WrongToken_Returns403()
    {
        var (runId, _) = await DispatchCredentialedRunAsync(CreateClient());

        using var rsa = RSA.Create(2048);
        var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

        var response = await ResolveAsync(CreateAnonymousClient(), runId, "not-the-token", "sc", publicKey);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task ResolveSlot_UnknownRun_Returns404()
    {
        using var rsa = RSA.Create(2048);
        var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

        var response = await ResolveAsync(CreateAnonymousClient(), Guid.NewGuid(), "tok", "sc", publicKey);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}
