using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Auxilia.Core.Contracts;
using Auxilia.Governance;
using Auxilia.Workflows;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// The Core workflow-type registry surface (REST): registration is the only way into the catalog
/// (a runtime schema announcement refreshes a registered type but never creates one), docker
/// packages enter Pending until the signing authority approves or denies, and only Active types
/// dispatch. Reads are gated by workflow-configuration.manage, registration by workflow-type.manage,
/// signing by workflow-type.sign (Administrator, not Operator).
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class WorkflowTypesTests : CoreApiComponentTestBase
{
    private static WorkflowSchema SampleSchema(string name = "pull-request-code-review") =>
        new(name,
            [
                new SlotDefinition("repository", new { access = "read" }, "The repo under review")
                {
                    Contract = "Auxilia.Workflows.SourceControl.ISourceControlAccess", Optional = false
                },
                new SlotDefinition("work-items", null) { Contract = "Auxilia.Workflows.TaskSource.ITaskSourceAccess", Optional = true }
            ],
            [])
        {
            Version = "2.1.0",
            Tags = ["review", "ai"],
            Lifetime = WorkflowLifetime.OneShot,
            Inputs = [new WorkflowInputDescriptor("instruction", "Instruction", Required: true, "What to review")],
            Views = [new Auxilia.Workflows.Views.ViewDescriptor(
                "activity", "{}", Auxilia.Workflows.Views.ViewRendering.Log, Auxilia.Workflows.Views.ViewLifecycle.Live)],
            Triggers = [new TriggerDeclaration(TriggerDeclaration.Manual, "Start from the dashboard")]
        };

    private async Task PublishSchemaAsync(WorkflowSchema schema)
    {
        await MessageBus.SimulateReceivedAsync(
            WorkflowSchemaPublished.ExchangeName,
            new WorkflowSchemaPublished(schema.WorkflowName, schema, DateTimeOffset.UtcNow));
    }

    private async Task<HttpClient> ClientForRolesAsync(params string[] roles)
    {
        var directory = Factory.Services.GetRequiredService<PrincipalDirectory>();
        var (principal, apiKey) = await directory.CreateApiKeyPrincipalAsync($"svc-{Guid.NewGuid():N}", "Service");
        foreach (var role in roles.Where(BuiltInRoles.Exists))
            await directory.AssignRoleAsync(principal.Id, role);
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    // --- Registration lifecycle ---

    [Test]
    public async Task DockerRegistration_IsPending_ApproveActivates()
    {
        var client = CreateClient();

        var register = await client.PostAsJsonAsync("/api/workflow-types",
            new RegisterWorkflowTypeRequest("pull-request-code-review", "docker://review:1"));
        Assert.That(register.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var registration = await register.Content.ReadFromJsonAsync<WorkflowTypeRegistrationDto>();
        Assert.That(registration!.Status, Is.EqualTo(WorkflowTypeStatus.Pending),
            "A docker package has no verifiable signature — it must await the signing authority.");

        var approve = await client.PostAsync("/api/workflow-types/pull-request-code-review/approve", null);
        Assert.That(approve.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var approved = await approve.Content.ReadFromJsonAsync<WorkflowTypeRegistrationDto>();
        Assert.That(approved!.Status, Is.EqualTo(WorkflowTypeStatus.Active));
    }

    [Test]
    public async Task DenyRecordsTheReason_AndTheTypeCannotRun()
    {
        var client = CreateClient();
        await client.PostAsJsonAsync("/api/workflow-types",
            new RegisterWorkflowTypeRequest("evil-wf", "docker://evil:1"));

        var deny = await client.PostAsJsonAsync("/api/workflow-types/evil-wf/deny",
            new DenyWorkflowTypeRequest("failed the security review"));
        Assert.That(deny.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var denied = await deny.Content.ReadFromJsonAsync<WorkflowTypeRegistrationDto>();
        Assert.That(denied!.Status, Is.EqualTo(WorkflowTypeStatus.Denied));
        Assert.That(denied.StatusReason, Is.EqualTo("failed the security review"));

        var run = await client.PostAsJsonAsync("/api/runs", new RunRequest("evil-wf"));
        Assert.That(run.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest),
            "A denied type must not dispatch.");
    }

    [Test]
    public async Task UnregisteredType_CannotRun()
    {
        var run = await CreateClient().PostAsJsonAsync("/api/runs", new RunRequest("ghost"));
        Assert.That(run.StatusCode, Is.EqualTo(HttpStatusCode.NotFound),
            "Runs reference registered types only; there is no caller-supplied package path.");
    }

    [Test]
    public async Task Unregister_RemovesTheType()
    {
        var client = CreateClient();
        await RegisterActiveTypeAsync(client, "short-lived", "docker://x:1");

        var delete = await client.DeleteAsync("/api/workflow-types/short-lived");
        Assert.That(delete.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        var page = await client.GetFromJsonAsync<PagedResult<WorkflowTypeDto>>("/api/workflow-types");
        Assert.That(page!.Items, Is.Empty);
    }

    // --- Schema announcements refresh registered types only ---

    [Test]
    public async Task PublishedSchema_ForUnregisteredType_IsNotCataloged()
    {
        await PublishSchemaAsync(SampleSchema());

        var page = await CreateClient().GetFromJsonAsync<PagedResult<WorkflowTypeDto>>("/api/workflow-types");

        Assert.That(page!.Items, Is.Empty,
            "Registration is the only way into the catalog — a runtime announcement never creates a type.");
    }

    [Test]
    public async Task PublishedSchema_RefreshesARegisteredType()
    {
        var client = CreateClient();
        await RegisterActiveTypeAsync(client, "pull-request-code-review", "docker://review:1");
        await PublishSchemaAsync(SampleSchema());

        var page = await client.GetFromJsonAsync<PagedResult<WorkflowTypeDto>>("/api/workflow-types");
        Assert.That(page!.Items, Has.Count.EqualTo(1));
        var type = page.Items[0];
        Assert.Multiple(() =>
        {
            Assert.That(type.WorkflowType, Is.EqualTo("pull-request-code-review"));
            Assert.That(type.Version, Is.EqualTo("2.1.0"));
            Assert.That(type.Lifetime, Is.EqualTo("OneShot"));
            Assert.That(type.Tags, Is.EquivalentTo(new[] { "review", "ai" }));
            Assert.That(type.PackageUri, Is.EqualTo("docker://review:1"));
            Assert.That(type.Status, Is.EqualTo(WorkflowTypeStatus.Active));
        });
    }

    [Test]
    public async Task GetSchema_ReturnsSlotsCapabilitiesInputsAndViews()
    {
        var client = CreateClient();
        await RegisterActiveTypeAsync(client, "pull-request-code-review", "docker://review:1");
        await PublishSchemaAsync(SampleSchema());

        var schema = await client.GetFromJsonAsync<WorkflowSchemaDto>(
            "/api/workflow-types/pull-request-code-review/schema");

        Assert.That(schema, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(schema!.Slots, Has.Count.EqualTo(2));
            var repo = schema.Slots.Single(s => s.SlotName == "repository");
            Assert.That(repo.Contract, Is.EqualTo("Auxilia.Workflows.SourceControl.ISourceControlAccess"));
            Assert.That(repo.Optional, Is.False);
            Assert.That(repo.CapabilitiesJson, Does.Contain("read"));
            Assert.That(schema.Slots.Single(s => s.SlotName == "work-items").Optional, Is.True);
            Assert.That(schema.Inputs.Single().Name, Is.EqualTo("instruction"));
            Assert.That(schema.Views.Single().Name, Is.EqualTo("activity"));
            Assert.That(schema.Triggers.Single().Kind, Is.EqualTo(TriggerDeclaration.Manual));
        });
    }

    [Test]
    public async Task Republish_UpsertsInPlace_NoDuplicate()
    {
        var client = CreateClient();
        await RegisterActiveTypeAsync(client, "pull-request-code-review", "docker://review:1");
        await PublishSchemaAsync(SampleSchema());
        await PublishSchemaAsync(SampleSchema() with { Version = "3.0.0" });

        var page = await client.GetFromJsonAsync<PagedResult<WorkflowTypeDto>>("/api/workflow-types");

        Assert.That(page!.Items, Has.Count.EqualTo(1));
        Assert.That(page.Items[0].Version, Is.EqualTo("3.0.0"));
    }

    [Test]
    public async Task GetSchema_UnknownType_Returns404()
    {
        var response = await CreateClient().GetAsync("/api/workflow-types/ghost/schema");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // --- Authorization boundaries ---

    [TestCase(BuiltInRoles.Administrator, HttpStatusCode.OK)]
    [TestCase(BuiltInRoles.Operator, HttpStatusCode.OK)]
    [TestCase(BuiltInRoles.User, HttpStatusCode.Forbidden)]
    [TestCase(BuiltInRoles.Auditor, HttpStatusCode.Forbidden)]
    public async Task ListWorkflowTypes_RequiresConfigurationManage(string role, HttpStatusCode expected)
    {
        var client = await ClientForRolesAsync(role);
        var response = await client.GetAsync("/api/workflow-types");
        Assert.That(response.StatusCode, Is.EqualTo(expected));
    }

    [TestCase(BuiltInRoles.Administrator, HttpStatusCode.OK)]
    [TestCase(BuiltInRoles.Operator, HttpStatusCode.OK)]
    [TestCase(BuiltInRoles.User, HttpStatusCode.Forbidden)]
    public async Task Register_RequiresWorkflowTypeManage(string role, HttpStatusCode expected)
    {
        var client = await ClientForRolesAsync(role);
        var response = await client.PostAsJsonAsync("/api/workflow-types",
            new RegisterWorkflowTypeRequest($"wf-{Guid.NewGuid():N}", "docker://img:1"));
        Assert.That(response.StatusCode, Is.EqualTo(expected));
    }

    [Test]
    public async Task Approve_IsTheSigningAuthority_OperatorIsForbidden()
    {
        await CreateClient().PostAsJsonAsync("/api/workflow-types",
            new RegisterWorkflowTypeRequest("wf-sign", "docker://img:1"));

        var operatorClient = await ClientForRolesAsync(BuiltInRoles.Operator);
        var response = await operatorClient.PostAsync("/api/workflow-types/wf-sign/approve", null);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden),
            "Operators register; only the signing authority (workflow-type.sign) approves.");
    }

    [Test]
    public async Task Anonymous_IsRejected()
    {
        var response = await CreateAnonymousClient().GetAsync("/api/workflow-types");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }
}
