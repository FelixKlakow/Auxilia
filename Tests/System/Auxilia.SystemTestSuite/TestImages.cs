using System.Diagnostics;

namespace Auxilia.SystemTestSuite;

/// <summary>
/// Builds the system-test Docker images. The <c>.prebuilt-images</c> marker in the repo root (or
/// <c>AUXILIA_PREBUILT_IMAGES=1</c>) opts out of in-test builds — but only while the images are
/// FRESH: any build input written after the marker forces an automatic rebuild of every image,
/// and each successful rebuild renews the marker. CI leaves both unset so images are always
/// rebuilt from local source.
/// </summary>
internal static class TestImages
{
    private static readonly string MarkerPath = Path.Combine(RepoPaths.Root, ".prebuilt-images");

    // Directory names that are never docker build inputs (or are runtime residue, like the dev
    // Core's core-data stores) — writes there must not invalidate the marker.
    private static readonly string[] ExcludedDirectories =
        ["bin", "obj", "TestResults", "node_modules", ".git", ".vs", "core-data"];

    // One decision per test process: every image shares the same build-input cutoff, and the
    // marker renewal after the first rebuilt image must not flip later images back to skipping.
    private static readonly Lazy<bool> SkipBuilds = new(DecideSkip);

    internal static async Task BuildImageAsync(string tag, string dockerfilePath)
    {
        // Local opt-out: docker builds spawned from the test process can hang on a wedged
        // Docker Desktop daemon, so a marker of prebuilt-and-still-fresh images skips them.
        if (SkipBuilds.Value)
        {
            await Console.Out.WriteLineAsync($"Prebuilt-images opt-out active — skipping docker build for {tag}.");
            return;
        }

        // The Docker Desktop daemon occasionally wedges under suite load and a build then
        // hangs forever at ~0 CPU. Bound each attempt and retry once instead of hanging.
        try
        {
            try
            {
                await BuildImageOnceAsync(tag, dockerfilePath, TimeSpan.FromMinutes(8));
            }
            catch (TimeoutException)
            {
                await Console.Error.WriteLineAsync(
                    $"docker build for {tag} timed out — retrying once (daemon may have been wedged).");
                await BuildImageOnceAsync(tag, dockerfilePath, TimeSpan.FromMinutes(8));
            }
        }
        catch
        {
            // A failed build leaves the image set half-refreshed — drop the marker so the next
            // run rebuilds everything instead of trusting a renewal from a sibling's success.
            try { File.Delete(MarkerPath); } catch (IOException) { /* concurrent delete */ }
            throw;
        }

        try
        {
            if (File.Exists(MarkerPath))
                File.SetLastWriteTimeUtc(MarkerPath, DateTime.UtcNow);
        }
        catch (IOException) { /* a concurrent failure deleted the marker — that wins */ }
    }

    private static bool DecideSkip()
    {
        var hasMarker = File.Exists(MarkerPath);
        if (!hasMarker)
            return Environment.GetEnvironmentVariable("AUXILIA_PREBUILT_IMAGES") == "1";

        var markerTime = File.GetLastWriteTimeUtc(MarkerPath);
        var (newestPath, newestTime) = NewestBuildInputWrite();
        if (newestTime <= markerTime)
            return true;

        Console.WriteLine(
            $"Prebuilt-images marker is STALE ({Path.GetRelativePath(RepoPaths.Root, newestPath)} " +
            $"written {newestTime:u}, marker {markerTime:u}) — rebuilding the system-test images.");
        return false;
    }

    private static (string Path, DateTime Time) NewestBuildInputWrite()
    {
        var newestPath = MarkerPath;
        var newestTime = DateTime.MinValue;
        foreach (var file in EnumerateBuildInputs())
        {
            var time = File.GetLastWriteTimeUtc(file);
            if (time > newestTime && !string.Equals(file, MarkerPath, StringComparison.OrdinalIgnoreCase))
                (newestPath, newestTime) = (file, time);
        }
        return (newestPath, newestTime);
    }

    private static IEnumerable<string> EnumerateBuildInputs()
    {
        // Root-level files (Auxilia.slnx, build props, .dockerignore) plus everything the
        // Dockerfiles can pull from the build context (the repo root).
        foreach (var file in Directory.EnumerateFiles(RepoPaths.Root))
            yield return file;
        foreach (var top in new[] { "Source", "Tests", "Scripts" })
        {
            var dir = Path.Combine(RepoPaths.Root, top);
            if (Directory.Exists(dir))
                foreach (var file in EnumerateRecursive(dir))
                    yield return file;
        }
    }

    private static IEnumerable<string> EnumerateRecursive(string directory)
    {
        foreach (var sub in Directory.EnumerateDirectories(directory))
            if (!ExcludedDirectories.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
                foreach (var file in EnumerateRecursive(sub))
                    yield return file;
        foreach (var file in Directory.EnumerateFiles(directory))
            yield return file;
    }

    private static async Task BuildImageOnceAsync(string tag, string dockerfilePath, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo("docker", $"build -t {tag} -f {dockerfilePath} .")
        {
            WorkingDirectory = RepoPaths.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("Failed to start docker build.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException($"docker build for {tag} exceeded {timeout.TotalMinutes:0} minutes.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"docker build failed for {tag} (exit {process.ExitCode}):\n{stdout}\n{stderr}");
    }
}
