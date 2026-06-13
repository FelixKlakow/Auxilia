using System.Text.Json;
using Auxilia.BackendService.Dashboard;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.BackendService.Tests.ComponentTests;

/// <summary>
/// The visual workflow configuration editor (#20) through the real DI container: drafts
/// round-trip into <see cref="UpsertWorkflowConfigurationCommand"/>s on the seeding exchange
/// (the Steering Instance stays the single writer), secrets are write-only and survive edits,
/// deletions publish the removal and audit, and the pages enforce
/// workflow-configuration.manage.
/// </summary>
[TestFixture]
[Category("Component")]
public class WorkflowConfigurationEditorTests : DashboardComponentTestBase
{
    private WorkflowConfigurationEditorService EditorService
        => Factory.Services.GetRequiredService<WorkflowConfigurationEditorService>();

    private IDataAccess<WorkflowConfigurationRecord> Configurations
        => Factory.Services.GetRequiredService<IDataAccess<WorkflowConfigurationRecord>>();

    private IDataAccess<AuditRecord> AuditRecords
        => Factory.Services.GetRequiredService<IDataAccess<AuditRecord>>();

    private async Task SeedEmailProviderAsync()
        => await Factory.Services.GetRequiredService<IDataAccess<SlotProviderRecord>>()
            .SaveAsync(new SlotProviderRecord
            {
                Id = SlotProviderRecord.IdFor("email-work-items"),
                ProviderType = "email-work-items",
                DllPath = "/plugins/email.slothandler.dll",
                SettingDescriptorsJson = JsonSerializer.Serialize(new List<SettingDescriptor>
                {
                    new("ImapHost", "IMAP host", SettingKind.Text, Required: true),
                    new("Password", "Password", SettingKind.Secret, Required: true)
                })
            });

    /// <summary>Persists a configuration record the way the Steering Instance would, using the host's protector.</summary>
    private async Task<Guid> SeedConfigurationRecordAsync(string name, string displayName)
    {
        var protector = Factory.Services.GetRequiredService<ISettingsProtector>();
        var record = new WorkflowConfigurationRecord
        {
            Id = WorkflowConfigurationRecord.IdFor(name),
            Name = name,
            DisplayName = displayName,
            WorkflowType = "pull-request-code-review",
            PackageUri = "docker://review:1",
            Enabled = true,
            SlotBindingsJson = JsonSerializer.Serialize(new List<WorkflowConfigurationSlotBinding>
            {
                new()
                {
                    SlotName = "work-items",
                    ProviderType = "email-work-items",
                    ProtectedSettingsJson = protector.Protect(JsonSerializer.Serialize(
                        new Dictionary<string, string>
                        {
                            ["ImapHost"] = "imap.example.org",
                            ["Password"] = "stored-secret"
                        }))
                }
            })
        };
        await Configurations.SaveAsync(record);
        return record.Id;
    }

    private static WorkflowConfigurationDraft NewDraft(string displayName)
    {
        var draft = new WorkflowConfigurationDraft
        {
            DisplayName = displayName,
            WorkflowType = "pull-request-code-review",
            PackageUri = "docker://review:1"
        };
        var binding = new SlotBindingDraft { SlotName = "work-items", ProviderType = "email-work-items" };
        binding.Settings["ImapHost"] = "imap.example.org";
        binding.Settings["Password"] = "typed-secret";
        draft.Bindings.Add(binding);
        return draft;
    }

    [Test]
    public async Task CreateConfiguration_PublishesUpsertCommandOnTheSeedingExchange()
    {
        await SeedEmailProviderAsync();

        var name = await EditorService.SaveAsync("component-test", Guid.NewGuid(),
            NewDraft("Component create"));

        var command = MessageBus.PublishedMessages
            .Where(p => p.Topic == "slot-configurations")
            .Select(p => p.Message).OfType<UpsertWorkflowConfigurationCommand>()
            .Single(c => c.Name == name);
        Assert.Multiple(() =>
        {
            Assert.That(command.WorkflowType, Is.EqualTo("pull-request-code-review"));
            Assert.That(command.SlotBindings.Single().ProviderType, Is.EqualTo("email-work-items"));
            Assert.That(command.SlotBindings.Single().Settings["Password"], Is.EqualTo("typed-secret"));
        });
    }

    [Test]
    public async Task EditWithoutRetypingSecret_PreservesTheStoredSecret_AndNeverAuditsIt()
    {
        await SeedEmailProviderAsync();
        var id = await SeedConfigurationRecordAsync("edit-keeps-secret", "Edit keeps secret");

        var draft = (await EditorService.LoadDraftAsync(id))!;
        Assert.That(draft.Bindings.Single().Settings["Password"], Is.Empty,
            "the stored secret must not be readable in the draft");
        draft.Bindings[0].Settings["ImapHost"] = "imap.changed.org";

        await EditorService.SaveAsync("component-test", null, draft);

        var command = MessageBus.PublishedMessages
            .Select(p => p.Message).OfType<UpsertWorkflowConfigurationCommand>()
            .Single(c => c.Name == "edit-keeps-secret");
        Assert.Multiple(async () =>
        {
            Assert.That(command.SlotBindings.Single().Settings["Password"], Is.EqualTo("stored-secret"));
            Assert.That(command.SlotBindings.Single().Settings["ImapHost"], Is.EqualTo("imap.changed.org"));

            var audit = (await AuditRecords.ReadAsync()).ToList();
            Assert.That(audit.Any(a => a.Action == "workflow-configuration.saved"), Is.True);
            Assert.That(audit.Where(a => a.DetailJson != null).Select(a => a.DetailJson!),
                Has.None.Contains("stored-secret"), "secrets must never reach the audit log");
        });
    }

    [Test]
    public async Task DeleteConfiguration_PublishesRemovalAndAudits()
    {
        await SeedEmailProviderAsync();
        var id = await SeedConfigurationRecordAsync("delete-me", "Delete me");

        await EditorService.DeleteAsync("component-test", id);

        Assert.Multiple(async () =>
        {
            var removal = MessageBus.PublishedMessages
                .Select(p => p.Message).OfType<RemoveWorkflowConfigurationCommand>()
                .SingleOrDefault(c => c.Name == "delete-me");
            Assert.That(removal, Is.Not.Null, "the removal must travel over the bus — the SI owns the store");

            var audit = await AuditRecords.ReadAsync();
            Assert.That(audit.Any(a =>
                a.Action == "workflow-configuration.deleted" && a.Subject == "delete-me"), Is.True);
        });
    }

    // ------------------------------------------------------------------ authorization matrix

    [TestCase("/workflows")]
    [TestCase("/workflows/new")]
    public async Task WorkflowPages_Anonymous_Return401(string path)
    {
        using var client = CreateClient();

        var response = await client.GetAsync(path);

        Assert.That(response.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.Unauthorized));
    }

    [TestCase("/workflows")]
    [TestCase("/workflows/new")]
    public async Task WorkflowPages_AsUserWithoutManagePermission_ShowDenyPanel(string path)
    {
        using var client = CreateClient();
        var (_, username, password) = await CreatePrincipalAsync("User");
        var (cookie, _) = await LoginAsync(client, username, password);

        var html = await GetHtmlAsync(client, path, cookie);

        Assert.That(html, Does.Contain("Access denied"));
    }

    [Test]
    public async Task WorkflowsPage_AsOperator_RendersConfiguredWorkflows()
    {
        await SeedEmailProviderAsync();
        await SeedConfigurationRecordAsync("operator-sees-this", "Operator sees this");

        using var client = CreateClient();
        var (_, username, password) = await CreatePrincipalAsync("Operator");
        var (cookie, _) = await LoginAsync(client, username, password);

        var html = await GetHtmlAsync(client, "/workflows", cookie);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("Operator sees this"));
            Assert.That(html, Does.Contain("work-items"));
            Assert.That(html, Does.Not.Contain("Access denied"));
        });
    }

    [Test]
    public async Task EditorPage_AsOperator_RendersTheThreeSteps()
    {
        using var client = CreateClient();
        var (_, username, password) = await CreatePrincipalAsync("Operator");
        var (cookie, _) = await LoginAsync(client, username, password);

        var html = await GetHtmlAsync(client, "/workflows/new", cookie);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("Basics"));
            Assert.That(html, Does.Contain("Slots"));
            Assert.That(html, Does.Contain("Trigger"));
            Assert.That(html, Does.Not.Contain("Access denied"));
        });
    }

    // ------------------------------------------------------------------ runs filter & rerun

    [Test]
    public async Task RunsPage_WithConfigurationFilter_ShowsOnlyThatConfigurationsRuns()
    {
        var instances = Factory.Services.GetRequiredService<IDataAccess<WorkflowInstanceRecord>>();
        var configurationId = Guid.NewGuid();
        await instances.SaveAsync(new WorkflowInstanceRecord
        {
            Id = Guid.NewGuid(),
            WorkflowType = "filtered-workflow-type",
            State = "Success",
            CreatedUtc = DateTimeOffset.UtcNow,
            WorkflowConfigurationId = configurationId,
            WorkflowConfigurationName = "filtered-configuration"
        });
        await instances.SaveAsync(new WorkflowInstanceRecord
        {
            Id = Guid.NewGuid(),
            WorkflowType = "unrelated-workflow-type",
            State = "Success",
            CreatedUtc = DateTimeOffset.UtcNow
        });

        using var client = CreateClient();
        var (_, username, password) = await CreatePrincipalAsync("Operator");
        var (cookie, _) = await LoginAsync(client, username, password);

        var html = await GetHtmlAsync(client, $"/runs?configuration={configurationId}", cookie);

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("filtered-workflow-type"));
            Assert.That(html, Does.Not.Contain("unrelated-workflow-type"));
        });
    }

    [Test]
    public async Task Rerun_PublishesTheReDispatchAndAudits()
    {
        var (operatorId, _, _) = await CreatePrincipalAsync("Operator");
        var instances = Factory.Services.GetRequiredService<IDataAccess<WorkflowInstanceRecord>>();
        var original = new RunWorkflowCommand(
            Guid.NewGuid(), "pull-request-code-review", "docker://review:1",
            new Dictionary<string, string> { ["WorkItemId"] = "mail-7" }, Guid.NewGuid());
        var instanceId = Guid.NewGuid();
        await instances.SaveAsync(new WorkflowInstanceRecord
        {
            Id = instanceId,
            WorkflowType = "pull-request-code-review",
            State = "Success",
            CreatedUtc = DateTimeOffset.UtcNow,
            DispatchCommandJson = JsonSerializer.Serialize(original)
        });

        var rerunService = Factory.Services.GetRequiredService<WorkflowRerunService>();
        var commandId = await rerunService.RerunAsync(operatorId, instanceId);

        var command = MessageBus.PublishedMessages
            .Where(p => p.Topic == "workflow.run-commands")
            .Select(p => p.Message).OfType<RunWorkflowCommand>()
            .Single(c => c.CommandId == commandId);
        Assert.Multiple(async () =>
        {
            Assert.That(command.RequestedBy, Is.EqualTo(operatorId));
            Assert.That(command.Context["RERUN_OF"], Is.EqualTo(instanceId.ToString("D")));
            Assert.That(command.Context["WorkItemId"], Is.EqualTo("mail-7"));

            var audit = await AuditRecords.ReadAsync();
            Assert.That(audit.Any(a => a.Action == "workflow.rerun" && a.Subject == instanceId.ToString()),
                Is.True, "the rerun must be audited with the predecessor instance");
        });
    }

    [Test]
    public async Task Rerun_AsUnprivilegedUser_IsDeniedByPolicy()
    {
        // The "User" role may trigger workflows; a principal WITHOUT any role may not.
        var directory = Factory.Services.GetRequiredService<Auxilia.Governance.PrincipalDirectory>();
        var nobody = await directory.CreateHumanAsync("No role", $"norole-{Guid.NewGuid():N}", "test-pw-12345");

        var instances = Factory.Services.GetRequiredService<IDataAccess<WorkflowInstanceRecord>>();
        var instanceId = Guid.NewGuid();
        await instances.SaveAsync(new WorkflowInstanceRecord
        {
            Id = instanceId,
            WorkflowType = "pull-request-code-review",
            State = "Failed",
            CreatedUtc = DateTimeOffset.UtcNow,
            DispatchCommandJson = JsonSerializer.Serialize(new RunWorkflowCommand(
                Guid.NewGuid(), "pull-request-code-review", "docker://review:1",
                new Dictionary<string, string>()))
        });

        var rerunService = Factory.Services.GetRequiredService<WorkflowRerunService>();
        var exception = Assert.ThrowsAsync<InvalidOperationException>(
            () => rerunService.RerunAsync(nobody.Id, instanceId));

        Assert.That(exception!.Message, Does.Contain("workflow.trigger denied"));
    }
}
