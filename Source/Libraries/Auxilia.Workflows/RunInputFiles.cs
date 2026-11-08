using System.Reflection;
using System.Text.RegularExpressions;

namespace Auxilia.Workflows;

/// <summary>
/// A file the workflow takes per run (<see cref="IWorkflowBuilder.RequiresInputFile"/>): the caller
/// uploads it to the Core, names it in the run request, and the runner places it into the container
/// before the workflow starts (<see cref="IRunInputFiles"/>). The file is the run's alone and is
/// deleted when the run ends. <paramref name="Name"/> is lowercase letters, digits and dashes.
/// </summary>
public sealed record WorkflowInputFileDescriptor(string Name, string? Description = null, bool Required = false)
{
    private static readonly Regex NamePattern = new("^[a-z0-9][a-z0-9-]{0,62}$", RegexOptions.CultureInvariant);

    /// <summary>True for a name a file can be declared and delivered under.</summary>
    public static bool IsValidName(string? name) => name is not null && NamePattern.IsMatch(name);
}

/// <summary>The run's input files, where the platform placed them inside the container.</summary>
public interface IRunInputFiles
{
    /// <summary>
    /// The path of the declared input file <paramref name="name"/>; null when this run did not
    /// receive it (an optional file). An undeclared name throws — a typo never reads nothing.
    /// </summary>
    string? PathOf(string name);

    /// <summary>Like <see cref="PathOf"/> but throws when the run did not receive the file.</summary>
    string RequirePath(string name);
}

/// <summary>Resolves input files from the launch environment (<see cref="WorkflowEnvironmentVariables.InputFilePrefix"/>).</summary>
public sealed class EnvironmentRunInputFiles(IReadOnlyList<WorkflowInputFileDescriptor> declared) : IRunInputFiles
{
    public string? PathOf(string name)
    {
        if (!declared.Any(f => f.Name == name))
            throw new InvalidOperationException(
                $"Input file '{name}' is not declared by this workflow. Declare it via RequiresInputFile.");
        return System.Environment.GetEnvironmentVariable(WorkflowEnvironmentVariables.InputFileVariable(name))
            is { Length: > 0 } path && File.Exists(path)
            ? path
            : null;
    }

    public string RequirePath(string name)
        => PathOf(name) ?? throw new InvalidOperationException($"This run received no input file '{name}'.");
}

/// <summary>Builds <see cref="IWorkflowBuilder.ProvidesTools"/> entries.</summary>
public static class ProvidedTool
{
    /// <summary>
    /// <c>tool@version</c>, with the version the build baked into <paramref name="assembly"/> as
    /// <c>[AssemblyMetadata(metadataKey, …)]</c> — e.g. the runtime version a package the image
    /// bundles brings; the bare <paramref name="tool"/> when the build recorded none.
    /// </summary>
    public static string Versioned(string tool, Assembly assembly, string metadataKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(tool);
        var version = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == metadataKey)?.Value;
        return version is { Length: > 0 } && !version.Any(char.IsWhiteSpace) ? $"{tool}@{version}" : tool;
    }
}
