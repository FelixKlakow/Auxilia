using System.Security.Cryptography;
using Auxilia.Workflows.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Workflows.Testing.DummyWorkflows;

/// <summary>
/// Dummy workflow for run input files and required tools: it declares one required input file
/// and a versioned tool its image "provides", reads the file the platform placed into the
/// container, and reports its fingerprint and size — never its content.
/// </summary>
public static class InputFileWorkflow
{
    public const string WorkflowName = "input-file-workflow";
    public const string FileName = "session-state";
    public const string ViewName = "input-file";

    /// <summary>The tool (with version) this image declares it provides.</summary>
    public const string ProvidedTool = "probe-cli@2.0.0";

    /// <summary>What the run found: the file's SHA-256 (hex) and length.</summary>
    public sealed record InputFileReading(string Sha256, long Length);

    public static Task RunAsync(string[] args) => Declare().WithApplication(ExecuteAsync).Run(args);

    /// <summary>The schema the platform registers the type with — the same declarations the image runs.</summary>
    public static WorkflowSchema Schema() => ((WorkflowBuilder)Declare()).BuildSchema();

    private static IWorkflowBuilder Declare() =>
        WorkflowBuilder
            .Create(WorkflowName)
            .ProvidesTools(ProvidedTool)
            .RequiresInputFile(FileName, "an archive the run continues from", required: true)
            .DeclaresView<InputFileReading>(ViewName, ViewRendering.Log, ViewLifecycle.LiveAndPersisted);

    private static async Task ExecuteAsync(IServiceProvider provider, CancellationToken ct)
    {
        var path = provider.GetRequiredService<IRunInputFiles>().RequirePath(FileName);
        var content = await File.ReadAllBytesAsync(path, ct);
        var reading = new InputFileReading(Convert.ToHexStringLower(SHA256.HashData(content)), content.Length);
        Console.WriteLine($"[InputFileWorkflow] {FileName}: {reading.Length} bytes, sha256 {reading.Sha256}");
        await provider.GetRequiredService<IViewPublisher>().PublishAsync(ViewName, reading, ct);
    }
}
