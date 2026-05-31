namespace Auxilia.Workflows.Tests;

[TestFixture]
[Category("Unit")]
public class IWorkflowSchemaProviderTests
{
    [Test]
    public void IWorkflowSchemaProvider_IsPublicInterface()
    {
        var type = typeof(IWorkflowSchemaProvider);
        Assert.That(type.IsPublic, Is.True);
        Assert.That(type.IsInterface, Is.True);
    }

    [Test]
    public void IWorkflowSchemaProvider_HasGetSchemaMethod()
    {
        var method = typeof(IWorkflowSchemaProvider).GetMethod("GetSchema");
        Assert.That(method, Is.Not.Null);
        Assert.That(method!.ReturnType, Is.EqualTo(typeof(WorkflowSchema)));
        Assert.That(method.GetParameters(), Is.Empty);
    }

    [Test]
    public void ConcreteProvider_CanBeInstantiatedAndReturnsSchema()
    {
        IWorkflowSchemaProvider provider = new TestSchemaProvider();
        var schema = provider.GetSchema();
        Assert.That(schema, Is.Not.Null);
        Assert.That(schema.WorkflowName, Is.EqualTo("test-provider-workflow"));
    }

    private sealed class TestSchemaProvider : IWorkflowSchemaProvider
    {
        public WorkflowSchema GetSchema()
            => WorkflowBuilder.Create("test-provider-workflow").BuildSchema();
    }
}
