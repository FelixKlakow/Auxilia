using System.Reflection;

// What a build records for ProvidedTool.Versioned (an MSBuild AssemblyMetadata item, in an image).
[assembly: AssemblyMetadata("TestToolVersion", "4.2.1")]

namespace Auxilia.Workflows.Tests;

/// <summary>Input files a workflow declares, versioned tool provisions, and reading both back.</summary>
[TestFixture]
[Category("Unit")]
[NonParallelizable] // environment variables are process-wide
public sealed class RunInputFilesTests
{
    [Test]
    public void DeclaredInputFiles_RideSchemaAndManifest()
    {
        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test")
            .RequiresInputFile("session-state", "the session to continue", required: true)
            .RequiresInputFile("notes");

        var expected = new[]
        {
            new WorkflowInputFileDescriptor("session-state", "the session to continue", Required: true),
            new WorkflowInputFileDescriptor("notes"),
        };
        Assert.Multiple(() =>
        {
            Assert.That(builder.BuildSchema().InputFiles, Is.EqualTo(expected));
            Assert.That(builder.BuildManifest().InputFiles, Is.EqualTo(expected));
        });
    }

    [TestCase("Session")]
    [TestCase("session state")]
    [TestCase("../etc")]
    [TestCase("-leading")]
    [TestCase("")]
    public void AnInputFileName_IsLowercaseLettersDigitsAndDashes(string name)
        => Assert.Throws<ArgumentException>(() => WorkflowBuilder.Create("test").RequiresInputFile(name));

    [Test]
    public void AnInputFile_IsDeclaredOnce()
        => Assert.Throws<InvalidOperationException>(() =>
            WorkflowBuilder.Create("test").RequiresInputFile("notes").RequiresInputFile("notes"));

    [Test]
    public void ARun_FindsItsInputFile_WhereThePlatformPutIt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"input-{Guid.NewGuid():N}");
        File.WriteAllText(path, "state");
        var variable = WorkflowEnvironmentVariables.InputFileVariable("session-state");
        System.Environment.SetEnvironmentVariable(variable, path);
        try
        {
            var files = new EnvironmentRunInputFiles(
                [new WorkflowInputFileDescriptor("session-state"), new WorkflowInputFileDescriptor("notes")]);

            Assert.Multiple(() =>
            {
                Assert.That(variable, Is.EqualTo("Workflow__InputFile__SESSION-STATE"));
                Assert.That(files.RequirePath("session-state"), Is.EqualTo(path));
                Assert.That(files.PathOf("notes"), Is.Null, "an optional file this run did not get");
                Assert.Throws<InvalidOperationException>(() => files.RequirePath("notes"));
                Assert.Throws<InvalidOperationException>(() => files.PathOf("sesion-state"),
                    "a typo never reads nothing");
            });
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(variable, null);
            File.Delete(path);
        }
    }

    [Test]
    public void ProvidedTools_MayCarryAVersion()
    {
        var builder = (WorkflowBuilder)WorkflowBuilder.Create("test").ProvidesTools("claude", "copilot@1.0.107");

        Assert.That(builder.BuildSchema().ProvidedTools, Is.EqualTo(new[] { "claude", "copilot@1.0.107" }));
    }

    [TestCase("copilot@")]
    [TestCase("@1.0")]
    [TestCase("copilot@1@2")]
    [TestCase("co pilot")]
    public void AProvidedTool_IsNameOrNameAtVersion(string tool)
        => Assert.Throws<ArgumentException>(() => WorkflowBuilder.Create("test").ProvidesTools(tool));

    [Test]
    public void AVersionedTool_TakesTheVersionTheBuildRecorded()
    {
        var assembly = typeof(RunInputFilesTests).Assembly;

        Assert.Multiple(() =>
        {
            Assert.That(ProvidedTool.Versioned("tool", assembly, "TestToolVersion"), Is.EqualTo("tool@4.2.1"));
            Assert.That(ProvidedTool.Versioned("tool", assembly, "NoSuchKey"), Is.EqualTo("tool"),
                "no recorded version — the bare tool");
        });
    }
}
