using Auxilia.Workflows;
using Auxilia.Workflows.Crypto;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Auxilia.FakeSlots.Tests;

[TestFixture]
[Category("Integration")]
public class PluginLoadingTests
{
    private string _tempDir = null!;
    private string _repoRoot = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _repoRoot = FindRepoRoot();
        _tempDir = Path.Combine(Path.GetTempPath(), $"fakeslots-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        PublishFakeSlots();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Test]
    public void PluginLoading_FourDllsDiscovered()
    {
        Environment.SetEnvironmentVariable("AUXILIA_DEVELOPER_MODE", "1");
        try
        {
            var discovery = new FileSystemPluginDiscovery();
            var plugins = discovery.DiscoverPlugins(_tempDir);
            Assert.That(plugins, Has.Count.EqualTo(4));

            var providerTypes = plugins.Select(p => p.ProviderType).ToHashSet();
            Assert.That(providerTypes, Does.Contain("fake-code-review-happy"));
            Assert.That(providerTypes, Does.Contain("fake-code-review-write-back-failure"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AUXILIA_DEVELOPER_MODE", null);
        }
    }

    [Test]
    public void PluginLoading_AllPluginsLoadWithoutException()
    {
        Environment.SetEnvironmentVariable("AUXILIA_DEVELOPER_MODE", "1");
        try
        {
            var discovery = new FileSystemPluginDiscovery();
            var plugins = discovery.DiscoverPlugins(_tempDir);

            var developerMode = new EnvironmentDeveloperModeProvider();
            var verifier = new PluginManifestVerifier(developerMode, NullLogger<PluginManifestVerifier>.Instance);
            var resolver = new SlotHandlerResolver();
            var loader = new PluginLoader(resolver, verifier);

            Assert.DoesNotThrow(() => loader.Load(plugins));

            Assert.That(resolver.Resolve("fake-code-review-happy"), Is.Not.Null);
            Assert.That(resolver.Resolve("fake-code-review-write-back-failure"), Is.Not.Null);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AUXILIA_DEVELOPER_MODE", null);
        }
    }

    private void PublishFakeSlots()
    {
        var projects = new[]
        {
            "Auxilia.FakeSlots.CodeReview.Happy",
            "Auxilia.FakeSlots.CodeReview.WriteBackFailure",
        };

        foreach (var project in projects)
        {
            var csproj = Path.Combine(_repoRoot, project, $"{project}.csproj");
            var publishDir = _tempDir;

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"publish \"{csproj}\" -c Release -o \"{publishDir}\" --no-self-contained",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = _repoRoot,
            };

            using var process = System.Diagnostics.Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start dotnet publish for {project}");

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"dotnet publish failed for {project}.\nSTDOUT: {stdout}\nSTDERR: {stderr}");
        }
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Auxilia.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Could not find repo root containing Auxilia.slnx");
    }
}
