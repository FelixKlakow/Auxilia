namespace Auxilia.SystemTestSuite;

/// <summary>
/// The repository root, found by walking up from the test binaries to <c>Auxilia.slnx</c> —
/// immune to how deep a test project sits in the folder structure.
/// </summary>
internal static class RepoPaths
{
    internal static string Root { get; } = Find();

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Auxilia.slnx")))
            dir = dir.Parent;
        return dir?.FullName
               ?? throw new InvalidOperationException(
                   $"Auxilia.slnx not found above {AppContext.BaseDirectory}.");
    }
}
