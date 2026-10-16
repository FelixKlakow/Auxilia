using System.Diagnostics;

namespace Auxilia.SystemTestSuite;

/// <summary>
/// Builds the system-test Docker images. The <c>.prebuilt-images</c> marker in the repo root (or
/// <c>AUXILIA_PREBUILT_IMAGES=1</c>) opts out of in-test builds — but only while an image is
/// FRESH, judged PER IMAGE: an image is skipped only when no build input was written after both
/// the marker's own timestamp (a human's "everything is prebuilt" touch) and that image's last
/// successful in-test build, which the marker records per tag. A run that rebuilds a few images
/// therefore never vouches for the others. CI leaves both unset so images are always rebuilt.
/// </summary>
internal static class TestImages
{
    private static readonly string MarkerPath = Path.Combine(RepoPaths.Root, ".prebuilt-images");

    // Directory names that are never docker build inputs (or are runtime residue, like the dev
    // Core's core-data stores) — writes there must not invalidate the marker.
    private static readonly string[] ExcludedDirectories =
        ["bin", "obj", "TestResults", "node_modules", ".git", ".vs", "core-data"];

    // One snapshot per test process: the newest build input and the marker's stamps as they were
    // when the first image was considered — stamps written during this process change nothing.
    private static readonly Lazy<Freshness> Snapshot = new(TakeSnapshot);
    private static readonly object StampGate = new();

    private sealed record Freshness(
        bool OptedOut, DateTime MarkerTime, IReadOnlyDictionary<string, DateTime> Stamps,
        string NewestPath, DateTime NewestTime);

    /// <summary>The per-run egress gateway every launched workflow container sits behind.</summary>
    internal const string EgressGatewayImageName = "auxilia-egress-gateway:system-test";

    internal static Task BuildEgressGatewayAsync()
        => BuildImageAsync(EgressGatewayImageName, "Source/Platform/Auxilia.EgressGateway/Dockerfile");

    internal static async Task BuildImageAsync(string tag, string dockerfilePath)
    {
        // Local opt-out: docker builds spawned from the test process can hang on a wedged
        // Docker Desktop daemon, so a marker of prebuilt-and-still-fresh images skips them.
        if (IsFresh(tag))
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

        RecordBuilt(tag);
    }

    private static bool IsFresh(string tag)
    {
        var snapshot = Snapshot.Value;
        if (snapshot.OptedOut)
            return true;
        if (snapshot.MarkerTime == DateTime.MinValue)
            return false;
        var builtAt = snapshot.Stamps.TryGetValue(tag, out var stamp) && stamp > snapshot.MarkerTime
            ? stamp
            : snapshot.MarkerTime;
        if (snapshot.NewestTime <= builtAt)
            return true;
        Console.WriteLine(
            $"Image {tag} is STALE ({Path.GetRelativePath(RepoPaths.Root, snapshot.NewestPath)} " +
            $"written {snapshot.NewestTime:u}, image built {builtAt:u}) — rebuilding it.");
        return false;
    }

    private static Freshness TakeSnapshot()
    {
        if (!File.Exists(MarkerPath))
            return new Freshness(
                Environment.GetEnvironmentVariable("AUXILIA_PREBUILT_IMAGES") == "1",
                DateTime.MinValue, new Dictionary<string, DateTime>(), MarkerPath, DateTime.MinValue);
        var (newestPath, newestTime) = NewestBuildInputWrite();
        return new Freshness(false, File.GetLastWriteTimeUtc(MarkerPath), ReadStamps(), newestPath, newestTime);
    }

    /// <summary>The marker's per-image stamps: one <c>tag ticks</c> line per image built in-test.</summary>
    private static Dictionary<string, DateTime> ReadStamps()
    {
        var stamps = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        try
        {
            foreach (var line in File.ReadAllLines(MarkerPath))
                if (line.Split(' ', 2) is [var tag, var ticks] && long.TryParse(ticks, out var value))
                    stamps[tag] = new DateTime(value, DateTimeKind.Utc);
        }
        catch (IOException) { /* concurrently deleted — no stamps */ }
        return stamps;
    }

    /// <summary>
    /// Records <paramref name="tag"/> as freshly built. The marker's own timestamp is preserved —
    /// it stays the human's "everything is prebuilt" claim, which a partial rebuild must not renew.
    /// </summary>
    private static void RecordBuilt(string tag)
    {
        lock (StampGate)
        {
            try
            {
                if (!File.Exists(MarkerPath))
                    return;
                var markerTime = File.GetLastWriteTimeUtc(MarkerPath);
                var stamps = ReadStamps();
                stamps[tag] = DateTime.UtcNow;
                File.WriteAllLines(MarkerPath, stamps.Select(kv => $"{kv.Key} {kv.Value.Ticks}"));
                File.SetLastWriteTimeUtc(MarkerPath, markerTime);
            }
            catch (IOException) { /* a concurrent failure deleted the marker — that wins */ }
        }
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
