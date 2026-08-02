using System.Reflection;
using NUnit.Framework;

namespace Auxilia.Workflows.Tests;

[TestFixture]
[Category("Unit")]
public class InternalSurfaceTests
{
    private static readonly Assembly WorkflowsAssembly =
        typeof(WorkflowBuilder).Assembly;

    [Test]
    public void SlotConfigurationCrypto_IsNotPublic()
    {
        var exported = WorkflowsAssembly.GetExportedTypes();
        Assert.That(exported, Has.None.Matches<Type>(t => t.Name == "SlotConfigurationCrypto"),
            "SlotConfigurationCrypto must remain internal and must not appear in exported types.");
    }

    [Test]
    public void SlotConfigurationDto_IsNotPublic()
    {
        var exported = WorkflowsAssembly.GetExportedTypes();
        Assert.That(exported, Has.None.Matches<Type>(t => t.Name == "SlotConfigurationDto"),
            "SlotConfigurationDto must remain internal and must not appear in exported types.");
    }
}
