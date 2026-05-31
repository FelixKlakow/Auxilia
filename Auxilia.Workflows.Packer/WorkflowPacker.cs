using System.IO.Compression;
using System.Runtime.Loader;
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
        var schemaBytes = System.Text.Encoding.UTF8.GetBytes(schemaJson);
        var schemaHashBase64 = Convert.ToBase64String(SHA256.HashData(schemaBytes));

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

        fileEntries.Add(new WorkflowPackageFileEntry("workflow-schema.json", schemaHashBase64));

        var unsignedManifest = new WorkflowPackageManifest(
            Files: fileEntries,
            SignatureBase64: string.Empty,
            PublicKeyBase64: _signer.PublicKeyBase64,
            ExecutableRelativePath: Path.GetRelativePath(inputDirectory, executable).Replace('\\', '/'));

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
        {
            schemaStream.Write(schemaBytes);
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

        var dllPath = executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.ChangeExtension(executablePath, ".dll")
            : executablePath + ".dll";

        if (!File.Exists(dllPath))
            throw new InvalidOperationException($"Workflow DLL not found at '{dllPath}'.");

        var context = new WorkflowAssemblyLoadContext();
        try
        {
            var assembly = context.LoadFromAssemblyPath(dllPath);

            var providerType = assembly.GetExportedTypes()
                .FirstOrDefault(t => !t.IsAbstract && t.IsClass &&
                    t.GetInterfaces().Any(i => i.FullName == "Auxilia.Workflows.IWorkflowSchemaProvider"))
                ?? throw new InvalidOperationException(
                    $"No IWorkflowSchemaProvider implementation found in '{dllPath}'.");

            var instance = Activator.CreateInstance(providerType)
                ?? throw new InvalidOperationException(
                    $"Failed to instantiate '{providerType.FullName}'.");

            var method = providerType.GetMethod("GetSchema")!;
            var schemaObject = method.Invoke(instance, null)
                ?? throw new InvalidOperationException(
                    $"GetSchema() returned null for '{providerType.FullName}'.");

            return JsonSerializer.Serialize(schemaObject, WorkflowPackageJsonOptions.SerializeOptions);
        }
        finally
        {
            context.Unload();
        }
    }

    private sealed class WorkflowAssemblyLoadContext : AssemblyLoadContext
    {
        public WorkflowAssemblyLoadContext() : base(isCollectible: true) { }

        protected override System.Reflection.Assembly? Load(System.Reflection.AssemblyName assemblyName)
            => null;
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
