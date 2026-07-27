using Auxilia.Core.Runner.Workflows;

namespace Auxilia.Core.Runner.Tests.Workflows;

/// <summary>
/// Exercises the pre-flight linker check with REAL assemblies from the test output: the runner
/// assembly referencing the workflows SDK it was actually built against (compatible), and the
/// same references checked against a wrong assembly's member surface (incompatible).
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class PluginCompatibilityCheckerTests
{
    private static byte[] Load(string simpleName)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, simpleName + ".dll"));

    [Test]
    public void ReferencedAssemblyNames_ListsTheAuxiliaReferences()
    {
        var names = PluginCompatibilityChecker.ReferencedAssemblyNames(Load("Auxilia.Core.Runner"));

        Assert.That(names, Does.Contain("Auxilia.Workflows"));
    }

    [Test]
    public void FindMissingReferences_AssemblyPairedWithTheBuildItWasCompiledAgainst_IsCompatible()
    {
        var missing = PluginCompatibilityChecker.FindMissingReferences(
            Load("Auxilia.Core.Runner"),
            new Dictionary<string, byte[]> { ["Auxilia.Workflows"] = Load("Auxilia.Workflows") });

        Assert.That(missing, Is.Empty);
    }

    [Test]
    public void FindMissingReferences_AssemblyPairedWithTheWrongTarget_ReportsTheSkew()
    {
        // The messaging assembly defines none of the workflows-SDK members the runner
        // references — exactly the shape of a stale-image / fresh-plugin mismatch.
        var missing = PluginCompatibilityChecker.FindMissingReferences(
            Load("Auxilia.Core.Runner"),
            new Dictionary<string, byte[]> { ["Auxilia.Workflows"] = Load("Auxilia.Messaging") });

        Assert.That(missing, Is.Not.Empty,
            "referencing members a stale contract build does not define must be detected");
    }

    [Test]
    public void FindMissingReferences_NoOverlappingTargets_IsCompatible()
    {
        var missing = PluginCompatibilityChecker.FindMissingReferences(
            Load("Auxilia.Core.Runner"), new Dictionary<string, byte[]>());

        Assert.That(missing, Is.Empty);
    }
}
