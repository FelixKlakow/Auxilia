using Auxilia.PlatformData.Entities;

namespace Auxilia.PlatformData.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class WorkflowConfigurationRecordTests
{
    [Test]
    public void IdFor_SameName_IsDeterministic()
    {
        Assert.That(
            WorkflowConfigurationRecord.IdFor("code-review-alpha"),
            Is.EqualTo(WorkflowConfigurationRecord.IdFor("code-review-alpha")));
    }

    [Test]
    public void IdFor_DifferentNames_ProduceDifferentIds()
    {
        Assert.That(
            WorkflowConfigurationRecord.IdFor("code-review-alpha"),
            Is.Not.EqualTo(WorkflowConfigurationRecord.IdFor("code-review-beta")));
    }

    [Test]
    public void IdFor_DoesNotCollideWithOtherEntityNamespaces()
    {
        // A configuration named like a workflow type must not collide with that type's schema record.
        Assert.That(
            WorkflowConfigurationRecord.IdFor("my-workflow"),
            Is.Not.EqualTo(WorkflowSchemaRecord.IdFor("my-workflow")));
    }
}
