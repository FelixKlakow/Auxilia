using Auxilia.Core.Api.Services;
using Auxilia.Workflows;

namespace Auxilia.Core.Api.Tests.UnitTests;

/// <summary>Maps a stored <see cref="WorkflowSchema"/> to the config-editor DTO.</summary>
[TestFixture]
[Category("Unit")]
public sealed class WorkflowSchemaReadServiceTests
{
    [Test]
    public void ToSchemaDto_CarriesSlotsCapabilitiesInputsViewsAndTriggers()
    {
        var schema = new WorkflowSchema("wf",
            [
                new SlotDefinition("repo", new { access = "write" }, "desc")
                {
                    Contract = "Auxilia.Workflows.SourceControl.ISourceControlAccess", Optional = false
                },
                new SlotDefinition("opt", null) { Optional = true }
            ],
            [])
        {
            Version = "1.2.3",
            Lifetime = WorkflowLifetime.LongLiving,
            Tags = ["a"],
            Inputs = [new WorkflowInputDescriptor("instruction", "Instruction", Required: true)],
            Views = [new Auxilia.Workflows.Views.ViewDescriptor(
                "log", "{\"type\":\"object\"}", Auxilia.Workflows.Views.ViewRendering.Log,
                Auxilia.Workflows.Views.ViewLifecycle.Persisted)],
            Triggers = [new TriggerDeclaration(TriggerDeclaration.Schedule)],
            ConsumedArtifacts = ["patch"],
            InteractiveTerminalPort = 7681
        };

        var dto = WorkflowSchemaReadService.ToSchemaDto(schema, "wf", "docker://wf", Auxilia.Core.Contracts.WorkflowTypeStatus.Active);

        Assert.Multiple(() =>
        {
            Assert.That(dto.WorkflowType, Is.EqualTo("wf"));
            Assert.That(dto.Version, Is.EqualTo("1.2.3"));
            Assert.That(dto.Lifetime, Is.EqualTo("LongLiving"));
            Assert.That(dto.Slots, Has.Count.EqualTo(2));

            var repo = dto.Slots.Single(s => s.SlotName == "repo");
            Assert.That(repo.Contract, Is.EqualTo("Auxilia.Workflows.SourceControl.ISourceControlAccess"));
            Assert.That(repo.Optional, Is.False);
            Assert.That(repo.Description, Is.EqualTo("desc"));
            Assert.That(repo.CapabilitiesJson, Does.Contain("write"));

            var opt = dto.Slots.Single(s => s.SlotName == "opt");
            Assert.That(opt.Optional, Is.True);
            Assert.That(opt.CapabilitiesJson, Is.Null);

            Assert.That(dto.Inputs.Single().Required, Is.True);
            Assert.That(dto.Views.Single().Rendering, Is.EqualTo("Log"));
            Assert.That(dto.Views.Single().Lifecycle, Is.EqualTo("Persisted"));
            Assert.That(dto.Triggers.Single().Kind, Is.EqualTo(TriggerDeclaration.Schedule));
            Assert.That(dto.ConsumedArtifacts, Is.EquivalentTo(new[] { "patch" }));
            Assert.That(dto.InteractiveTerminalPort, Is.EqualTo(7681));
            Assert.That(dto.PackageUri, Is.EqualTo("docker://wf"));
            Assert.That(dto.Status, Is.EqualTo(Auxilia.Core.Contracts.WorkflowTypeStatus.Active));
        });
    }
}
