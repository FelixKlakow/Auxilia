using System.Text.Json;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Auxilia.Workflows.Testing.DummyWorkflows;

namespace Auxilia.SystemTestSuite.CoreClientSurface;

/// <summary>
/// Personal credentials, end to end (real Core, runner behind the Docker API guard, broker,
/// workflow SDK): two people run at the same time, each on their own personal connector, and
/// each run works with its own person's token; one person replaces their token while the run
/// goes on, and the running workflow's next read of its slot — a real activation through runner
/// and Core — gets the replacement while the other run keeps its own. No token appears in a
/// stored view, input or log of either run (the workflow reports fingerprints only).
/// </summary>
[TestFixture]
[Category("System")]
public sealed class PersonalCredentialSystemTests
{
    private static ICoreClient Admin => _admin ??= CoreClientEnvironment.CreateClient();
    private static ICoreClient? _admin;

    [Test]
    [CancelAfter(300_000)]
    public async Task TwoPeoplesRuns_EachUseTheirOwnToken_AndAReplacedTokenReachesTheRunningOne(CancellationToken ct)
    {
        var alice = await PersonAsync("alice", ct);
        var bob = await PersonAsync("bob", ct);
        var aliceToken = "alice-token-" + Guid.NewGuid().ToString("N");
        var bobToken = "bob-token-" + Guid.NewGuid().ToString("N");
        var aliceConnector = await ConnectorAsync(alice, aliceToken, ct);
        var bobConnector = await ConnectorAsync(bob, bobToken, ct);

        var aliceRun = await StartAsync(alice, aliceConnector, ct);
        var bobRun = await StartAsync(bob, bobConnector, ct);

        Assert.Multiple(async () =>
        {
            Assert.That(await ReadingAsync(alice, aliceRun, "start", ct), Is.EqualTo(CredentialRefreshWorkflow.Fingerprint(aliceToken)));
            Assert.That(await ReadingAsync(bob, bobRun, "start", ct), Is.EqualTo(CredentialRefreshWorkflow.Fingerprint(bobToken)));
        });

        // Alice replaces her token while both runs go on; each run then reads its slot again.
        var replacement = "alice-replaced-" + Guid.NewGuid().ToString("N");
        await alice.UpdateConnectorAsync(aliceConnector, new UpdateConnector(
            Settings: new Dictionary<string, string> { ["token"] = replacement }), ct);
        await alice.ProvideInputAsync(aliceRun, "{}", ct);
        await bob.ProvideInputAsync(bobRun, "{}", ct);

        Assert.Multiple(async () =>
        {
            Assert.That(await ReadingAsync(alice, aliceRun, "re-read", ct), Is.EqualTo(CredentialRefreshWorkflow.Fingerprint(replacement)),
                "the running workflow got the token its owner put in meanwhile");
            Assert.That(await ReadingAsync(bob, bobRun, "re-read", ct), Is.EqualTo(CredentialRefreshWorkflow.Fingerprint(bobToken)),
                "the other person's run keeps its own token");
        });

        var (aliceState, _) = await ClientStreamProbe.AwaitTerminalAsync(alice, aliceRun, ct);
        var (bobState, _) = await ClientStreamProbe.AwaitTerminalAsync(bob, bobRun, ct);
        Assert.That((aliceState, bobState), Is.EqualTo(("Success", "Success")));

        var tokens = new[] { aliceToken, bobToken, replacement };
        foreach (var (person, run) in new[] { (alice, aliceRun), (bob, bobRun) })
        {
            var stored = (await person.GetRunViewsAsync(run, take: 500, ct: ct)).Items.Select(v => v.PayloadJson).ToList();
            stored.Add(JsonSerializer.Serialize(await person.GetRunAsync(run, ct)));
            stored.Add(await ContainerLogAsync(person, run, ct));
            Assert.That(stored, Has.None.Matches<string>(text => tokens.Any(text.Contains)),
                "no token in a stored view, input or log");
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>A signed-in person with their own key (administrators may bind the uncatalogued test provider).</summary>
    private static async Task<ICoreClient> PersonAsync(string name, CancellationToken ct)
    {
        var created = await Admin.CreateApiKeyPrincipalAsync(new CreateApiKeyPrincipalRequest($"{name}-{Guid.NewGuid():N}"), ct);
        // Granting the Administrator role is a step-up action: re-prove the admin's own key first.
        await Admin.StepUpAsync(new StepUpRequest(CoreClientEnvironment.BootstrapApiKey), ct);
        await Admin.AssignPrincipalRoleAsync(created.Principal.Id, new AssignRoleRequest("Administrator"), ct);
        return CoreClientEnvironment.CreateClient(created.ApiKey);
    }

    private static async Task<Guid> ConnectorAsync(ICoreClient owner, string token, CancellationToken ct)
    {
        var connector = await owner.CreateConnectorAsync(new CreateConnector(
            "my agent account", CoreClientEnvironment.CredentialProbeProviderType,
            new Dictionary<string, string> { ["token"] = token }), ct);
        Assert.That(connector.Scope, Is.EqualTo(ResourceScope.Personal));
        return connector.Id;
    }

    private static async Task<Guid> StartAsync(ICoreClient person, Guid connectorId, CancellationToken ct)
        => (await person.RunAsync(new RunRequest(
            CoreClientEnvironment.CredentialRefreshWorkflowType,
            CoreClientEnvironment.ContextFor(CredentialRefreshWorkflow.WorkflowName),
            SlotBindings: [new SlotBinding(CredentialRefreshWorkflow.SlotName,
                CoreClientEnvironment.CredentialProbeProviderType, connectorId)]), ct)).RunId;

    /// <summary>The fingerprint the run reported for <paramref name="phase"/>, waiting for it to appear.</summary>
    private static async Task<string> ReadingAsync(ICoreClient person, Guid runId, string phase, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(120);
        while (true)
        {
            var reading = (await person.GetRunViewsAsync(runId, CredentialRefreshWorkflow.ViewName, take: 50, ct: ct)).Items
                .Select(v => JsonSerializer.Deserialize<CredentialRefreshWorkflow.CredentialReading>(
                    v.PayloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!)
                .FirstOrDefault(r => r.Phase == phase);
            if (reading is not null)
                return reading.Fingerprint;
            if (DateTimeOffset.UtcNow > deadline)
                Assert.Fail($"run {runId} reported no '{phase}' reading (state {(await person.GetRunAsync(runId, ct))?.State})");
            await Task.Delay(500, ct);
        }
    }

    /// <summary>The run's persisted container log (written when the container is collected).</summary>
    private static async Task<string> ContainerLogAsync(ICoreClient person, Guid runId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            var log = (await person.QueryArtifactsAsync(new ArtifactQuery(ArtifactType: "container-log", RunId: runId), ct)).Items
                .FirstOrDefault();
            if (log is not null && await person.OpenArtifactContentAsync(log.Id, ct) is { } content)
            {
                await using (content)
                using (var reader = new StreamReader(content))
                    return await reader.ReadToEndAsync(ct);
            }
            if (DateTimeOffset.UtcNow > deadline)
                Assert.Fail($"run {runId} left no container log");
            await Task.Delay(500, ct);
        }
    }
}
