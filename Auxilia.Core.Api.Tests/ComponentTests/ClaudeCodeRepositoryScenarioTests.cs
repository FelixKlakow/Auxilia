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
/// exactly the repositories from its own config — each repository is a GENERIC slot binding of a
/// workspace-mount provider (inline non-secret settings + the credential connector). The Core maps
/// the binding's settings to the provider's declared roles as pure data; the credential stays in the
/// Core and the dispatched command carries only role-keyed settings + auth-slot references the
/// runner resolves just-in-time.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class ClaudeCodeRepositoryScenarioTests : CoreApiComponentTestBase
{
    private const string ClaudeCode = "claude-code";
    private const string Package = "docker://auxilia-claude-code:latest";
    private const string RepositorySlot = "repository";

    [SetUp]
    public async Task RegisterClaudeCodeTypeAsync()
    {
        await RegisterActiveTypeAsync(CreateClient(), ClaudeCode, Package);
        // The mount provider is catalog DATA: role-tagged settings, a required credential
        // contract, and the workspace-mount flag — nothing about it is compiled anywhere.
        ICoreClient core = new CoreClient(CreateClient());
        await core.RegisterProviderAsync(new RegisterSlotProvider(
            "git-repository", "workspace", "A git repository mounted into the run's workspace.",
            Contracts: ["Auxilia.Workflows.SourceControl.ISourceControlAccess"],
            Settings:
            [
                new RegisterProviderSetting("CloneUrl", "Repository", "Text", Required: true,
                    Role: "clone-url", Browse: "repositories"),
                new RegisterProviderSetting("Branch", "Branch", "Text",
                    Role: "branch", Browse: "branches", BrowseDependsOn: "CloneUrl"),
            ],
            RequiredCredentialContract: "git-credential",
            MountsIntoWorkspace: true));
    }

    [Test]
    public async Task Client_ConfiguresTfsAuth_ThenStartsClaudeCodeSessionAgainstMultipleRepos()
    {
        ICoreClient core = new CoreClient(CreateClient());

        // 1. Dynamically configure TFS auth: a personal connector holding ONLY the credential.
        var auth = await core.CreateConnectorAsync(new CreateConnector(
            "tfs-auth", "azure-devops",
            new Dictionary<string, string> { ["username"] = "build", ["token"] = "the-pat" },
            ConnectorScope.Personal));

        // 2. Start a Claude Code session against exactly two repos on that server, with a prompt
        //    from the app's configuration — one binding per repository on the SAME slot.
        const string prompt = "Add a CHANGELOG entry for the latest release.";
        await core.RunAsync(new RunRequest(
            ClaudeCode,
            Context: new Dictionary<string, string> { ["Title"] = prompt },
            SlotBindings:
            [
                new SlotBinding(RepositorySlot, "git-repository", auth.Id,
                    new Dictionary<string, string>
                    {
                        ["CloneUrl"] = "https://dev.azure.com/contoso/app/_git/app",
                        ["Branch"] = "main"
                    }),
                new SlotBinding(RepositorySlot, "git-repository", auth.Id,
                    new Dictionary<string, string>
                    {
                        ["CloneUrl"] = "https://dev.azure.com/contoso/docs/_git/docs"
                    })
            ]));

        // 3. The dispatched command carries the prompt + both mounts with ROLE-keyed settings and
        //    their auth-slot references — and never the credential itself.
        var command = MessageBus.PublishedMessages
            .Select(m => m.Message).OfType<RunWorkflowCommand>().Single();

        Assert.Multiple(() =>
        {
            Assert.That(command.WorkflowType, Is.EqualTo(ClaudeCode));
            Assert.That(command.Context["Title"], Is.EqualTo(prompt));
            Assert.That(command.WorkspaceMounts, Has.Count.EqualTo(2));
            Assert.That(command.WorkspaceMounts!.Select(m => m.SettingsByRole["clone-url"]), Is.EquivalentTo(new[]
            {
                "https://dev.azure.com/contoso/app/_git/app",
                "https://dev.azure.com/contoso/docs/_git/docs"
            }));
            Assert.That(command.WorkspaceMounts.Select(m => m.MountId),
                Is.EquivalentTo(new[] { RepositorySlot, RepositorySlot + "-2" }),
                "Multiple bindings of one slot get distinct mount ids.");
            Assert.That(command.WorkspaceMounts.All(m => m.AuthSlotName is not null), Is.True,
                "Each mount references its auth slot so the runner resolves the PAT JIT at dispatch.");
            Assert.That(command.SlotProviderTypes ?? [], Does.Not.Contain("git-repository"),
                "Workspace-mount providers are consumed by the runner — no plugin ships into the container.");
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
            SlotBindings:
            [
                new SlotBinding(RepositorySlot, "git-repository", connectorId,
                    new Dictionary<string, string>
                    {
                        ["CloneUrl"] = "https://dev.azure.com/contoso/app/_git/app"
                    })
            ])));

        Assert.That(ex!.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden),
            "Mount auth is gated like any connector — you cannot use another principal's credential.");
    }
}
