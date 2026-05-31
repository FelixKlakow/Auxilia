using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Auxilia.Workflows;
using Auxilia.Workflows.Crypto;

namespace Auxilia.Workflows.Packer;

public sealed class WorkflowPacker
{
    private readonly RsaFileSigningProvider _signer;
    private readonly Func<string, WorkflowSchema>? _schemaEmitter;

    public WorkflowPacker(RsaFileSigningProvider signer, Func<string, WorkflowSchema>? schemaEmitter = null)
    {
        _signer = signer;
        _schemaEmitter = schemaEmitter;
    }

    public void Pack(string inputDirectory, string outputZipPath)
    {
        var executable = FindExecutable(inputDirectory);

        var schemaJson = EmitSchema(executable);

        var filesToPack = DiscoverFiles(inputDirectory, executable);

        var fileEntries = filesToPack
            .Select(absolutePath =>
            {
                var relativePath = Path.GetRelativePath(inputDirectory, absolutePath)
                    .Replace('\\', '/');
                var hashBase64 = Convert.ToBase64String(SHA256.HashData(File.ReadAllBytes(absolutePath)));
                return new WorkflowPackageFileEntry(relativePath, hashBase64);
            })
            .ToList();

        var unsignedManifest = new WorkflowPackageManifest(
            Files: fileEntries,
            SignatureBase64: string.Empty,
            PublicKeyBase64: _signer.PublicKeyBase64);

        var unsignedBytes = JsonSerializer.SerializeToUtf8Bytes(unsignedManifest, WorkflowPackageJsonOptions.SerializeOptions);
        var signatureBytes = _signer.Sign(unsignedBytes);
        var signatureBase64 = Convert.ToBase64String(signatureBytes);

        var signedManifest = unsignedManifest with { SignatureBase64 = signatureBase64 };

        using var zip = new ZipArchive(File.Create(outputZipPath), ZipArchiveMode.Create, leaveOpen: false);

        foreach (var absolutePath in filesToPack)
        {
            var relativePath = Path.GetRelativePath(inputDirectory, absolutePath).Replace('\\', '/');
            var entry = zip.CreateEntry(relativePath);
            using var entryStream = entry.Open();
            using var fileStream = File.OpenRead(absolutePath);
            fileStream.CopyTo(entryStream);
        }

        var schemaEntry = zip.CreateEntry("workflow-schema.json");
        using (var schemaStream = schemaEntry.Open())
        using (var writer = new StreamWriter(schemaStream, System.Text.Encoding.UTF8))
        {
            writer.Write(schemaJson);
        }

        var manifestEntry = zip.CreateEntry("package-manifest.json");
        using (var manifestStream = manifestEntry.Open())
        {
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(signedManifest, WorkflowPackageJsonOptions.SerializeOptions);
            manifestStream.Write(manifestBytes);
        }
    }

    private static string FindExecutable(string inputDirectory)
    {
        var isWindows = OperatingSystem.IsWindows();
        IEnumerable<string> candidates;

        if (isWindows)
        {
            candidates = Directory.GetFiles(inputDirectory, "*.exe", SearchOption.TopDirectoryOnly);
        }
        else
        {
            candidates = Directory.GetFiles(inputDirectory, "*", SearchOption.TopDirectoryOnly)
                .Where(f => Path.GetExtension(f).Length == 0);
        }

        var executables = candidates.ToList();

        if (executables.Count == 0)
            throw new InvalidOperationException($"No executable found in '{inputDirectory}'.");
        if (executables.Count > 1)
            throw new InvalidOperationException($"Multiple executables found in '{inputDirectory}': {string.Join(", ", executables.Select(Path.GetFileName))}.");

        return executables[0];
    }

    private string EmitSchema(string executablePath)
    {
        if (_schemaEmitter is not null)
        {
            var schema = _schemaEmitter(executablePath);
            return JsonSerializer.Serialize(schema, WorkflowPackageJsonOptions.SerializeOptions);
        }

        var psi = new ProcessStartInfo(executablePath)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            Environment = { ["AUXILIA_DIRECTIVE"] = "EmitSchema" }
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process '{executablePath}'.");

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Schema emission process exited with code {process.ExitCode}.");
        if (string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException("Schema emission process produced no output.");

        return output;
    }

    private static List<string> DiscoverFiles(string inputDirectory, string executablePath)
    {
        var files = new List<string> { executablePath };

        var dataDir = Path.Combine(inputDirectory, "data");
        if (Directory.Exists(dataDir))
        {
            files.AddRange(Directory.GetFiles(dataDir, "*", SearchOption.AllDirectories));
        }

        return files;
    }
}
