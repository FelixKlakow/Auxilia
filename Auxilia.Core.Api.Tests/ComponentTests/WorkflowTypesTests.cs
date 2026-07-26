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
/// The Core-owned workflow schema registry surface (REST): the runner announces a registered type's
/// schema on the bus, a Core.Api hosted consumer catalogs it into the Core's OWN store (no cross-DB
/// read), and the config editor lists types + fetches full schemas — gated by
/// workflow-configuration.manage. MCP parity is covered by the same service layer these call.
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

    [Test]
    public async Task PublishedSchema_IsListedAsAWorkflowType()
    {
        await PublishSchemaAsync(SampleSchema());

        var page = await CreateClient().GetFromJsonAsync<PagedResult<WorkflowTypeDto>>("/api/workflow-types");

        Assert.That(page!.Items, Has.Count.EqualTo(1));
        var type = page.Items[0];
        Assert.Multiple(() =>
        {
            Assert.That(type.WorkflowType, Is.EqualTo("pull-request-code-review"));
            Assert.That(type.Version, Is.EqualTo("2.1.0"));
            Assert.That(type.Lifetime, Is.EqualTo("OneShot"));
            Assert.That(type.Tags, Is.EquivalentTo(new[] { "review", "ai" }));
        });
    }

    [Test]
    public async Task GetSchema_ReturnsSlotsCapabilitiesInputsAndViews()
    {
        await PublishSchemaAsync(SampleSchema());

        var schema = await CreateClient().GetFromJsonAsync<WorkflowSchemaDto>(
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
        await PublishSchemaAsync(SampleSchema());
        await PublishSchemaAsync(SampleSchema() with { Version = "3.0.0" });

        var page = await CreateClient().GetFromJsonAsync<PagedResult<WorkflowTypeDto>>("/api/workflow-types");

        Assert.That(page!.Items, Has.Count.EqualTo(1));
        Assert.That(page.Items[0].Version, Is.EqualTo("3.0.0"));
    }

    [Test]
    public async Task GetSchema_UnknownType_Returns404()
    {
        var response = await CreateClient().GetAsync("/api/workflow-types/ghost/schema");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

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

    [Test]
    public async Task Anonymous_IsRejected()
    {
        var response = await CreateAnonymousClient().GetAsync("/api/workflow-types");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }
}
