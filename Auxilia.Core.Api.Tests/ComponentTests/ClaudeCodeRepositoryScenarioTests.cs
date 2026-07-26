using System.Net;
using System.Text.Json;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// The supported scenario end to end through the typed client: an app dynamically configures TFS/git
/// authentication (a connector holding only the PAT), then starts a Claude Code session against
/// exactly the repositories from its own config, with a prompt — driving the hub over REST. The
/// credential stays in the Core; the dispatched command carries only the repo URLs + the auth-slot
/// references the runner resolves just-in-time.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class ClaudeCodeRepositoryScenarioTests : CoreApiComponentTestBase
{
    private const string ClaudeCode = "claude-code";
    private const string Package = "docker://auxilia-claude-code:latest";

    [SetUp]
    public Task RegisterClaudeCodeTypeAsync() => RegisterActiveTypeAsync(CreateClient(), ClaudeCode, Package);


    [Test]
    public async Task Client_ConfiguresTfsAuth_ThenStartsClaudeCodeSessionAgainstMultipleRepos()
    {
        ICoreClient core = new CoreClient(CreateClient());

        // 1. Dynamically configure TFS auth: a personal connector holding ONLY the credential.
        var auth = await core.CreateConnectorAsync(new CreateConnector(
            "tfs-auth", "azure-devops",
            new Dictionary<string, string> { ["username"] = "build", ["token"] = "the-pat" },
            ConnectorScope.Personal));

        // 2. Start a Claude Code session against exactly two repos on that server, with a prompt from
        //    the app's configuration.
        const string prompt = "Add a CHANGELOG entry for the latest release.";
        await core.RunAsync(new RunRequest(
            ClaudeCode,
            Context: new Dictionary<string, string> { ["Title"] = prompt },
            Repositories:
            [
                new RepositorySpec("app", "https://dev.azure.com/contoso/app/_git/app", "main", auth.Id),
                new RepositorySpec("docs", "https://dev.azure.com/contoso/docs/_git/docs", AuthConnectorId: auth.Id)
            ]));

        // 3. The dispatched command carries the prompt + both repos by URL and their auth-slot
        //    references — and never the credential itself.
        var command = MessageBus.PublishedMessages
            .Select(m => m.Message).OfType<RunWorkflowCommand>().Single();

        Assert.Multiple(() =>
        {
            Assert.That(command.WorkflowType, Is.EqualTo(ClaudeCode));
            Assert.That(command.Context["Title"], Is.EqualTo(prompt));
            Assert.That(command.Repositories, Has.Count.EqualTo(2));
            Assert.That(command.Repositories!.Select(r => r.CloneUrl), Is.EquivalentTo(new[]
            {
                "https://dev.azure.com/contoso/app/_git/app",
                "https://dev.azure.com/contoso/docs/_git/docs"
            }));
            Assert.That(command.Repositories.All(r => r.AuthSlotName is not null), Is.True,
                "Each repo references its auth slot so the runner resolves the PAT JIT at dispatch.");
            Assert.That(JsonSerializer.Serialize(command), Does.Not.Contain("the-pat"),
                "The credential must never ride the dispatch command.");
        });
    }

    [Test]
    public async Task StartingASession_WithAnotherPrincipalsAuthConnector_IsDenied()
    {
        // A personal auth connector owned by someone else — the caller may not borrow its credential.
        var connectorId = Guid.NewGuid();
        await Factory.Services.GetRequiredService<IDataAccess<CoreConnectorRecord>>().SaveAsync(new CoreConnectorRecord
        {
            Id = connectorId,
            Name = "not-mine",
            ProviderType = "azure-devops",
            Scope = ConnectorScope.Personal,
            OwnerPrincipalId = Guid.NewGuid()
        }, CancellationToken.None);

        ICoreClient core = new CoreClient(CreateClient());

        var ex = Assert.CatchAsync<CoreApiException>(() => core.RunAsync(new RunRequest(
            ClaudeCode,
            Repositories: [new RepositorySpec("app", "https://dev.azure.com/contoso/app/_git/app", AuthConnectorId: connectorId)])));

        Assert.That(ex!.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden),
            "Repository auth is gated like any connector — you cannot use another principal's credential.");
    }
}
