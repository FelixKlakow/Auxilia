using Auxilia.Workflows.Capabilities;
using Auxilia.Workflows.Environment;

namespace Auxilia.Workflows.Tests;

[TestFixture]
[Category("Unit")]
public class WorkflowBuilderTests
{
    private interface IStubService { }

    [Test]
    public void Create_ReturnsNonNull_IWorkflowBuilder()
    {
        var builder = WorkflowBuilder.Create("test");
        Assert.That(builder, Is.Not.Null);
    }

    [Test]
    public void FluentChaining_ReturnsSameBuilderInstance_ForEachMethod()
    {
        var builder = WorkflowBuilder.Create("test");
        var b1 = builder.Requires<IStubService>("slot1", new NoCapabilities());
        var b2 = b1.RequiresEnvironment(_ => { });
        var b3 = b2.WithMetadata(_ => { });
        Assert.That(b1, Is.SameAs(builder));
        Assert.That(b2, Is.SameAs(builder));
        Assert.That(b3, Is.SameAs(builder));
    }

    [Test]
    public void Requires_TwoCalls_ProducesTwoSlots_InSchema()
    {
        var builder = WorkflowBuilder.Create("test");
        builder.Requires<IStubService>("slot-a", new NoCapabilities()).Requires<IStubService>("slot-b", new NoCapabilities());
        var wb = (WorkflowBuilder)builder;

        var schema = wb.BuildSchema();

        Assert.That(schema.Slots, Has.Count.EqualTo(2));
    }

    [Test]
    public void WithInteractiveTerminal_PortAndGate_RideSchemaAndManifest()
    {
        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test")
            .WithInteractiveTerminal(7681, new InteractiveTerminalGate("view-mode", "console"));

        var schema = builder.BuildSchema();

        Assert.Multiple(() =>
        {
            Assert.That(schema.InteractiveTerminalPort, Is.EqualTo(7681));
            Assert.That(schema.InteractiveTerminalGate,
                Is.EqualTo(new InteractiveTerminalGate("view-mode", "console")));
        });
    }

    [Test]
    public void Requires_DuplicateSlotName_ThrowsInvalidOperationException()
    {
        var builder = WorkflowBuilder.Create("test");
        builder.Requires<IStubService>("slot-a", new NoCapabilities());
        Assert.Throws<InvalidOperationException>(() => builder.Requires<IStubService>("slot-a", new NoCapabilities()));
    }

    [Test]
    public void Requires_ServiceType_EqualsTServiceType()
    {
        var builder = WorkflowBuilder.Create("test");
        builder.Requires<IStubService>("s", new NoCapabilities());
        var wb = (WorkflowBuilder)builder;

        var schema = wb.BuildSchema();

        Assert.That(schema.Slots[0].ServiceType, Is.EqualTo(typeof(IStubService)));
    }

    [Test]
    public void Requires_ContractIsTheServiceTypeFullName_AndPersistsInSchemaJson()
    {
        var builder = WorkflowBuilder.Create("test");
        builder.Requires<IStubService>("s", new NoCapabilities());
        var schema = ((WorkflowBuilder)builder).BuildSchema();

        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<WorkflowSchema>(
            System.Text.Json.JsonSerializer.Serialize(schema))!;
        Assert.Multiple(() =>
        {
            Assert.That(schema.Slots[0].Contract, Is.EqualTo(typeof(IStubService).FullName));
            Assert.That(roundTripped.Slots[0].Contract, Is.EqualTo(typeof(IStubService).FullName));
        });
    }

    [Test]
    public void Requires_DefaultsToRequired_OptionalFlagPropagatesToSchema()
    {
        var builder = WorkflowBuilder.Create("test");
        builder.Requires<IStubService>("must", new NoCapabilities());
        builder.Requires<IStubService>("may", new NoCapabilities(), optional: true);
        var schema = ((WorkflowBuilder)builder).BuildSchema();

        Assert.Multiple(() =>
        {
            Assert.That(schema.Slots[0].Optional, Is.False);
            Assert.That(schema.Slots[1].Optional, Is.True);
        });
    }

    [Test]
    public async Task Run_WithEmitSchemaArg_PrintsSchemaJsonAndExitsWithoutBus()
    {
        var builder = WorkflowBuilder.Create("emit-test");
        builder.Requires<IStubService>("s", new NoCapabilities(), "the stub slot");

        var originalOut = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            // No message bus, no environment — must not throw and must not block.
            await builder.Run(["--emit-schema"]);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var schema = System.Text.Json.JsonSerializer.Deserialize<WorkflowSchema>(captured.ToString())!;
        Assert.Multiple(() =>
        {
            Assert.That(schema.WorkflowName, Is.EqualTo("emit-test"));
            Assert.That(schema.Slots.Single().SlotName, Is.EqualTo("s"));
            Assert.That(schema.Slots.Single().Contract, Is.EqualTo(typeof(IStubService).FullName));
        });
    }

    [Test]
    public void DeclaresTrigger_AndConsumesArtifact_LandInTheSchema()
    {
        var builder = WorkflowBuilder.Create("test");
        builder.DeclaresTrigger(TriggerDeclaration.Mailbox, "driven by mail");
        builder.ConsumesArtifact("code-review-result");
        builder.ConsumesArtifact("code-review-result"); // duplicates collapse

        var schema = ((WorkflowBuilder)builder).BuildSchema();
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<WorkflowSchema>(
            System.Text.Json.JsonSerializer.Serialize(schema))!;

        Assert.Multiple(() =>
        {
            Assert.That(schema.Triggers.Single().Kind, Is.EqualTo(TriggerDeclaration.Mailbox));
            Assert.That(schema.Triggers.Single().Description, Is.EqualTo("driven by mail"));
            Assert.That(schema.ConsumedArtifacts, Is.EqualTo(new[] { "code-review-result" }));
            Assert.That(roundTripped.Triggers.Single().Kind, Is.EqualTo(TriggerDeclaration.Mailbox));
            Assert.That(roundTripped.ConsumedArtifacts, Is.EqualTo(new[] { "code-review-result" }));
        });
    }

    [Test]
    public void DeclaresTrigger_DuplicateKind_Throws()
    {
        var builder = WorkflowBuilder.Create("test");
        builder.DeclaresTrigger(TriggerDeclaration.Schedule);

        Assert.Throws<InvalidOperationException>(() => builder.DeclaresTrigger(TriggerDeclaration.Schedule));
    }

    [Test]
    public void Requires_Capabilities_EqualsPassedObject()
    {
        var builder = WorkflowBuilder.Create("test");
        var caps = new NoCapabilities();
        builder.Requires<IStubService>("s", caps);
        var wb = (WorkflowBuilder)builder;

        var schema = wb.BuildSchema();

        Assert.That(schema.Slots[0].Capabilities, Is.SameAs(caps));
    }

    [Test]
    public void RequiresEnvironment_RequiresTool_AppendsToolRequirement()
    {
        var builder = WorkflowBuilder.Create("test");
        builder.RequiresEnvironment(e => e.RequiresTool("git"));
        var wb = (WorkflowBuilder)builder;

        var schema = wb.BuildSchema();

        Assert.That(schema.EnvironmentRequirements,
            Has.Some.InstanceOf<ToolRequirement>()
               .With.Property(nameof(ToolRequirement.ToolName)).EqualTo("git"));
    }

    [Test]
    public void RequiresEnvironment_RequiresOs_AppendsOsRequirement()
    {
        var builder = WorkflowBuilder.Create("test");
        builder.RequiresEnvironment(e => e.RequiresOs(OsConstraint.Linux));
        var wb = (WorkflowBuilder)builder;

        var schema = wb.BuildSchema();

        Assert.That(schema.EnvironmentRequirements,
            Has.Some.InstanceOf<OsRequirement>()
               .With.Property(nameof(OsRequirement.Os)).EqualTo(OsConstraint.Linux));
    }

    [Test]
    public void RequiresEnvironment_RequiresPort_AppendsPortRequirement()
    {
        var builder = WorkflowBuilder.Create("test");
        builder.RequiresEnvironment(e => e.RequiresPort(8080));
        var wb = (WorkflowBuilder)builder;

        var schema = wb.BuildSchema();

        Assert.That(schema.EnvironmentRequirements,
            Has.Some.InstanceOf<PortRequirement>()
               .With.Property(nameof(PortRequirement.Port)).EqualTo(8080));
    }

    [Test]
    public void BuildSchema_ReturnsSchema_WithSchemaVersion1_0()
    {
        var wb = (WorkflowBuilder)WorkflowBuilder.Create("smoke");

        var schema = wb.BuildSchema();

        Assert.That(schema, Is.Not.Null);
        Assert.That(schema.SchemaVersion, Is.EqualTo("1.0"));
    }

    [Test]
    public void BuildSchema_ContainsAllDeclaredSlotNames()
    {
        var builder = WorkflowBuilder.Create("workflow");
        builder.Requires<IStubService>("alpha", new NoCapabilities()).Requires<IStubService>("beta", new NoCapabilities());
        var wb = (WorkflowBuilder)builder;

        var schema = wb.BuildSchema();

        Assert.That(schema.Slots.Select(s => s.SlotName), Does.Contain("alpha"));
        Assert.That(schema.Slots.Select(s => s.SlotName), Does.Contain("beta"));
    }

    [Test]
    public void BuildSchema_TwoCalls_ReturnSchemas_WithSameWorkflowNameAndVersion()
    {
        var wb = (WorkflowBuilder)WorkflowBuilder.Create("test");

        var schema1 = wb.BuildSchema();
        var schema2 = wb.BuildSchema();

        Assert.That(schema2.WorkflowName, Is.EqualTo(schema1.WorkflowName));
        Assert.That(schema2.SchemaVersion, Is.EqualTo(schema1.SchemaVersion));
        Assert.That(schema2.Slots.Count, Is.EqualTo(schema1.Slots.Count));
    }

    [Test]
    public void BuildManifest_NoMetadata_HasDefaultVersionAndEmptyTags()
    {
        var wb = (WorkflowBuilder)WorkflowBuilder.Create("test");

        var manifest = wb.BuildManifest();

        Assert.That(manifest.Version, Is.Null.Or.Empty);
        Assert.That(manifest.Tags, Is.Empty);
    }

    [Test]
    public void BuildSchema_NoMetadata_HasDefaultVersionAndEmptyTags()
    {
        var wb = (WorkflowBuilder)WorkflowBuilder.Create("test");

        var schema = wb.BuildSchema();

        Assert.That(schema.Version, Is.Null.Or.Empty);
        Assert.That(schema.Tags, Is.Empty);
    }

    [Test]
    public void BuildManifest_WithMetadata_PropagatesVersionAndTags()
    {
        var wb = (WorkflowBuilder)WorkflowBuilder.Create("test");
        wb.WithMetadata(m => { m.Version = "2.0"; m.Tags.Add("prod"); });

        var manifest = wb.BuildManifest();

        Assert.That(manifest.Version, Is.EqualTo("2.0"));
        Assert.That(manifest.Tags, Contains.Item("prod"));
    }

    [Test]
    public void BuildSchema_WithMetadata_PropagatesVersionAndTags()
    {
        var wb = (WorkflowBuilder)WorkflowBuilder.Create("test");
        wb.WithMetadata(m => { m.Version = "2.0"; m.Tags.Add("prod"); });

        var schema = wb.BuildSchema();

        Assert.That(schema.Version, Is.EqualTo("2.0"));
        Assert.That(schema.Tags, Contains.Item("prod"));
    }

    [Test]
    public void DeclaresOutput_NoCall_BothArtifactsHaveEmptyOutputs()
    {
        var wb = (WorkflowBuilder)WorkflowBuilder.Create("test");

        var manifest = wb.BuildManifest();
        var schema = wb.BuildSchema();

        Assert.That(manifest.Outputs, Is.Empty);
        Assert.That(schema.Outputs, Is.Empty);
    }

    [Test]
    public void DeclaresOutput_OneCallWithoutDescription_PropagatesNameAndRelativePath()
    {
        var wb = (WorkflowBuilder)WorkflowBuilder.Create("test");
        wb.DeclaresOutput("report", "output/report.html");

        var manifest = wb.BuildManifest();
        var schema = wb.BuildSchema();

        Assert.That(manifest.Outputs, Has.Count.EqualTo(1));
        Assert.That(manifest.Outputs[0].Name, Is.EqualTo("report"));
        Assert.That(manifest.Outputs[0].RelativePath, Is.EqualTo("output/report.html"));
        Assert.That(manifest.Outputs[0].Description, Is.Null);
        Assert.That(schema.Outputs, Has.Count.EqualTo(1));
        Assert.That(schema.Outputs[0].Name, Is.EqualTo("report"));
        Assert.That(schema.Outputs[0].RelativePath, Is.EqualTo("output/report.html"));
        Assert.That(schema.Outputs[0].Description, Is.Null);
    }

    [Test]
    public void DeclaresOutput_OneCallWithDescription_PropagatesDescription()
    {
        var wb = (WorkflowBuilder)WorkflowBuilder.Create("test");
        wb.DeclaresOutput("report", "output/report.html", "HTML summary");

        var manifest = wb.BuildManifest();
        var schema = wb.BuildSchema();

        Assert.That(manifest.Outputs[0].Description, Is.EqualTo("HTML summary"));
        Assert.That(schema.Outputs[0].Description, Is.EqualTo("HTML summary"));
    }

    [Test]
    public void DeclaresOutput_TwoCalls_BothArtifactsHaveTwoDescriptorsInOrder()
    {
        var wb = (WorkflowBuilder)WorkflowBuilder.Create("test");
        wb.DeclaresOutput("first", "out/first.txt");
        wb.DeclaresOutput("second", "out/second.txt");

        var manifest = wb.BuildManifest();
        var schema = wb.BuildSchema();

        Assert.That(manifest.Outputs, Has.Count.EqualTo(2));
        Assert.That(manifest.Outputs[0].Name, Is.EqualTo("first"));
        Assert.That(manifest.Outputs[1].Name, Is.EqualTo("second"));
        Assert.That(schema.Outputs, Has.Count.EqualTo(2));
        Assert.That(schema.Outputs[0].Name, Is.EqualTo("first"));
        Assert.That(schema.Outputs[1].Name, Is.EqualTo("second"));
    }

    [Test]
    public void DeclaresOutput_FluentChain_AccumulatesCorrectly()
    {
        var builder = WorkflowBuilder.Create("test");
        var result = builder.DeclaresOutput("a", "out/a.txt").DeclaresOutput("b", "out/b.txt");
        var wb = (WorkflowBuilder)result;

        var manifest = wb.BuildManifest();

        Assert.That(result, Is.SameAs(builder));
        Assert.That(manifest.Outputs, Has.Count.EqualTo(2));
        Assert.That(manifest.Outputs[0].Name, Is.EqualTo("a"));
        Assert.That(manifest.Outputs[1].Name, Is.EqualTo("b"));
    }

    private sealed record ViewItem(string Message, int Value);

    [Test]
    public void DeclaresView_NoCall_BothArtifactsHaveEmptyViews()
    {
        var wb = (WorkflowBuilder)WorkflowBuilder.Create("test");

        Assert.That(wb.BuildSchema().Views, Is.Empty);
        Assert.That(wb.BuildManifest().Views, Is.Empty);
    }

    [Test]
    public void DeclaresView_OneCall_LandsInSchemaWithRenderingAndLifecycle()
    {
        var wb = (WorkflowBuilder)WorkflowBuilder.Create("test");
        wb.DeclaresView<ViewItem>("progress", Views.ViewRendering.Table, Views.ViewLifecycle.Persisted);

        var schema = wb.BuildSchema();

        Assert.That(schema.Views, Has.Count.EqualTo(1));
        Assert.That(schema.Views[0].Name, Is.EqualTo("progress"));
        Assert.That(schema.Views[0].Rendering, Is.EqualTo(Views.ViewRendering.Table));
        Assert.That(schema.Views[0].Lifecycle, Is.EqualTo(Views.ViewLifecycle.Persisted));
    }

    [Test]
    public void DeclaresView_OneCall_ItemSchemaJsonIsNonEmpty()
    {
        var wb = (WorkflowBuilder)WorkflowBuilder.Create("test");
        wb.DeclaresView<ViewItem>("progress", Views.ViewRendering.Stream, Views.ViewLifecycle.Live);

        var schema = wb.BuildSchema();

        Assert.That(schema.Views[0].ItemSchemaJson, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void DeclaresView_OneCall_LandsInManifest()
    {
        var wb = (WorkflowBuilder)WorkflowBuilder.Create("test");
        wb.DeclaresView<ViewItem>("progress", Views.ViewRendering.Chart, Views.ViewLifecycle.LiveAndPersisted);

        var manifest = wb.BuildManifest();

        Assert.That(manifest.Views, Has.Count.EqualTo(1));
        Assert.That(manifest.Views[0].Name, Is.EqualTo("progress"));
        Assert.That(manifest.Views[0].Rendering, Is.EqualTo(Views.ViewRendering.Chart));
        Assert.That(manifest.Views[0].Lifecycle, Is.EqualTo(Views.ViewLifecycle.LiveAndPersisted));
    }

    [Test]
    public void DeclaresView_WithoutRendererKey_RendererKeyIsNull()
    {
        var wb = (WorkflowBuilder)WorkflowBuilder.Create("test");
        wb.DeclaresView<ViewItem>("progress", Views.ViewRendering.Log, Views.ViewLifecycle.Live);

        Assert.That(wb.BuildSchema().Views[0].RendererKey, Is.Null);
        Assert.That(wb.BuildManifest().Views[0].RendererKey, Is.Null);
    }

    [Test]
    public void DeclaresView_WithRendererKey_LandsInSchemaAndManifest()
    {
        var wb = (WorkflowBuilder)WorkflowBuilder.Create("test");
        wb.DeclaresView<ViewItem>(
            "conversation", Views.ViewRendering.Custom, Views.ViewLifecycle.LiveAndPersisted, "agent-chat");

        Assert.That(wb.BuildSchema().Views[0].RendererKey, Is.EqualTo("agent-chat"));
        Assert.That(wb.BuildManifest().Views[0].RendererKey, Is.EqualTo("agent-chat"));
    }

    [Test]
    public void DeclaresView_RendererKeyOverload_DuplicateName_ThrowsInvalidOperationException()
    {
        var builder = WorkflowBuilder.Create("test");
        builder.DeclaresView<ViewItem>("conversation", Views.ViewRendering.Custom, Views.ViewLifecycle.Live, "agent-chat");

        Assert.Throws<InvalidOperationException>(() => builder.DeclaresView<ViewItem>(
            "conversation", Views.ViewRendering.Custom, Views.ViewLifecycle.Live, "agent-chat"));
    }

    [Test]
    public void DeclaresView_DuplicateName_ThrowsInvalidOperationException()
    {
        var builder = WorkflowBuilder.Create("test");
        builder.DeclaresView<ViewItem>("progress", Views.ViewRendering.Stream, Views.ViewLifecycle.Live);

        Assert.Throws<InvalidOperationException>(
            () => builder.DeclaresView<ViewItem>("progress", Views.ViewRendering.Log, Views.ViewLifecycle.Persisted));
    }

    [Test]
    public void DeclaresView_FluentChain_ReturnsSameBuilderInstance()
    {
        var builder = WorkflowBuilder.Create("test");
        var result = builder.DeclaresView<ViewItem>("a", Views.ViewRendering.Stream, Views.ViewLifecycle.Live);
        Assert.That(result, Is.SameAs(builder));
    }

    [Test]
    public void RequiresNetworkEndpoint_NoCall_BothArtifactsHaveEmptyNetworkEndpoints()
    {
        var wb = (WorkflowBuilder)WorkflowBuilder.Create("test");

        Assert.That(wb.BuildSchema().NetworkEndpoints, Is.Empty);
        Assert.That(wb.BuildManifest().NetworkEndpoints, Is.Empty);
    }

    [Test]
    public void RequiresNetworkEndpoint_OneCall_LandsInSchemaAndManifest()
    {
        var wb = (WorkflowBuilder)WorkflowBuilder.Create("test");
        wb.RequiresNetworkEndpoint("api.nuget.org", "NuGet package restore");

        var schema = wb.BuildSchema();
        var manifest = wb.BuildManifest();

        Assert.That(schema.NetworkEndpoints, Has.Count.EqualTo(1));
        Assert.That(schema.NetworkEndpoints[0].Endpoint, Is.EqualTo("api.nuget.org"));
        Assert.That(schema.NetworkEndpoints[0].Purpose, Is.EqualTo("NuGet package restore"));
        Assert.That(manifest.NetworkEndpoints, Has.Count.EqualTo(1));
        Assert.That(manifest.NetworkEndpoints[0].Endpoint, Is.EqualTo("api.nuget.org"));
        Assert.That(manifest.NetworkEndpoints[0].Purpose, Is.EqualTo("NuGet package restore"));
    }

    [Test]
    public void RequiresNetworkEndpoint_TwoCalls_BothArtifactsHaveTwoDeclarationsInOrder()
    {
        var wb = (WorkflowBuilder)WorkflowBuilder.Create("test");
        wb.RequiresNetworkEndpoint("api.nuget.org", "restore");
        wb.RequiresNetworkEndpoint("registry.npmjs.org", "npm install");

        var schema = wb.BuildSchema();
        var manifest = wb.BuildManifest();

        Assert.That(schema.NetworkEndpoints.Select(e => e.Endpoint),
            Is.EqualTo(new[] { "api.nuget.org", "registry.npmjs.org" }));
        Assert.That(manifest.NetworkEndpoints.Select(e => e.Endpoint),
            Is.EqualTo(new[] { "api.nuget.org", "registry.npmjs.org" }));
    }

    [Test]
    public void RequiresNetworkEndpoint_FluentChain_ReturnsSameBuilderInstance()
    {
        var builder = WorkflowBuilder.Create("test");
        var result = builder.RequiresNetworkEndpoint("api.nuget.org", "restore");
        Assert.That(result, Is.SameAs(builder));
    }

    [Test]
    public void RequiresRepository_NoCall_BothArtifactsHaveEmptyRepositories()
    {
        var wb = (WorkflowBuilder)WorkflowBuilder.Create("test");

        Assert.That(wb.BuildSchema().Repositories, Is.Empty);
        Assert.That(wb.BuildManifest().Repositories, Is.Empty);
    }

    [Test]
    public void RequiresRepository_OneCall_LandsInSchemaAndManifest()
    {
        var wb = (WorkflowBuilder)WorkflowBuilder.Create("test");
        wb.RequiresRepository("main-app", "https://example.com/main-app.git", "develop", noCache: true);

        var expected = new Workspace.RepositoryDeclaration(
            "main-app", "https://example.com/main-app.git", "develop", NoCache: true);
        Assert.That(wb.BuildSchema().Repositories, Is.EqualTo(new[] { expected }));
        Assert.That(wb.BuildManifest().Repositories, Is.EqualTo(new[] { expected }));
    }

    [Test]
    public void RequiresRepository_DefaultArguments_BranchNullAndNoCacheFalse()
    {
        var wb = (WorkflowBuilder)WorkflowBuilder.Create("test");
        wb.RequiresRepository("main-app", "https://example.com/main-app.git");

        var repo = wb.BuildSchema().Repositories[0];
        Assert.That(repo.Branch, Is.Null);
        Assert.That(repo.NoCache, Is.False);
    }

    [Test]
    public void RequiresRepository_DuplicateId_ThrowsInvalidOperationException()
    {
        var builder = WorkflowBuilder.Create("test");
        builder.RequiresRepository("main-app", "https://example.com/a.git");

        Assert.Throws<InvalidOperationException>(
            () => builder.RequiresRepository("main-app", "https://example.com/b.git"));
    }

    [Test]
    public void RequiresRepository_FluentChain_ReturnsSameBuilderInstance()
    {
        var builder = WorkflowBuilder.Create("test");
        var result = builder.RequiresRepository("main-app", "https://example.com/main-app.git");
        Assert.That(result, Is.SameAs(builder));
    }

    [Test]
    public void BuildSchema_ViaInterface_ReturnsWorkflowSchema()
    {
        var builder = (IWorkflowBuilder)WorkflowBuilder.Create("test-workflow");
        var schema = builder.BuildSchema();
        Assert.That(schema, Is.Not.Null);
        Assert.That(schema.WorkflowName, Is.EqualTo("test-workflow"));
    }
}
