using Auxilia.Workflows.SourceControl;

namespace Auxilia.Workflows.SlotPackages.Tests.TestDoubles;

public sealed class StubSourceControlAccess : ISourceControlAccess
{
    public string WorkingPath => "/stub/repo";

    public Task<IReadOnlyList<string>> ListFilesAsync(string? relativePath = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(["src/Program.cs", "src/Utils.cs", "README.md"]);

    public Task<string> ReadFileContentAsync(string relativePath, CancellationToken cancellationToken = default)
        => Task.FromResult($"// stub content of {relativePath}");

    public Task<IReadOnlyList<ChangedFile>> GetChangedFilesAsync(string baseRef, string headRef, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ChangedFile>>([
            new ChangedFile("src/Program.cs", ChangeKind.Modified),
            new ChangedFile("src/NewFile.cs", ChangeKind.Added)
        ]);
}
