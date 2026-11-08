using System.Formats.Tar;
using System.IO.Compression;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Auxilia.Workflows.Testing.DummyWorkflows;

namespace Auxilia.SystemTestSuite.CoreClientSurface;

/// <summary>
/// Run checkpoints, end to end (real Core, runner behind the Docker API guard, SDK): a workflow
/// saves its session folder and a file while it runs; the last save outlives a FAILED run, comes
/// back to the client as the run's artifact — the folder as a .tar.gz of its tree, without the
/// link the workload planted to the runner's own environment — for the lifetime the run asked, and
/// is gone once the client deletes it.
/// </summary>
[TestFixture]
[Category("System")]
public sealed class RunCheckpointSystemTests
{
    [Test]
    [CancelAfter(180_000)]
    public async Task TheLastSave_OutlivesAFailedRun_AndIsHandedBack_UntilItIsDeleted(CancellationToken ct)
    {
        var client = CoreClientEnvironment.CreateClient();

        var accepted = await client.RunAsync(new RunRequest(CoreClientEnvironment.CheckpointWorkflowType,
            CoreClientEnvironment.ContextFor(CheckpointWorkflow.WorkflowName, ("FAIL", "true")))
        {
            CheckpointLifetimeHours = 1,
        }, ct);
        var (state, _) = await ClientStreamProbe.AwaitTerminalAsync(client, accepted.RunId, ct);

        var checkpoints = (await client.QueryArtifactsAsync(new ArtifactQuery(RunId: accepted.RunId), ct)).Items
            .Where(a => a.Kind == ArtifactKinds.Checkpoint)
            .ToDictionary(a => a.ArtifactType);
        Assert.That(checkpoints.Keys, Is.EquivalentTo(new[] { CheckpointWorkflow.SessionCheckpoint, CheckpointWorkflow.SummaryCheckpoint }),
            $"the run ended {state}");
        var session = checkpoints[CheckpointWorkflow.SessionCheckpoint];
        var summary = checkpoints[CheckpointWorkflow.SummaryCheckpoint];
        var tree = await ReadArchiveAsync(client, session.Id, ct);
        var summaryText = await ReadTextAsync(client, summary.Id, ct);

        Assert.Multiple(() =>
        {
            Assert.That(state, Is.EqualTo(RunStates.Failed), "the workflow failed on purpose after its saves");
            Assert.That(session.Version, Is.EqualTo(2), "one version per save — only the latest is kept");
            Assert.That(session.MediaType, Is.EqualTo("application/gzip"));
            Assert.That(session.ExpiresUtc, Is.EqualTo(DateTimeOffset.UtcNow.AddHours(1)).Within(TimeSpan.FromMinutes(3)),
                "the run's lifetime overrides the workflow's two hours");
            Assert.That(tree, Is.EquivalentTo(new Dictionary<string, string?>
            {
                ["turns/"] = null,
                ["turns/1.md"] = "turn 1",
                ["turns/2.md"] = "turn 2",
                ["state.json"] = """{"turn":2}""",
            }), "the folder as it was at the last save — the planted link left out");
            Assert.That(summaryText, Is.EqualTo("2 turn(s) so far"));
        });

        Assert.That(await client.DeleteArtifactAsync(session.Id, ct), Is.True);
        Assert.Multiple(async () =>
        {
            Assert.That(await client.GetArtifactAsync(session.Id, ct), Is.Null);
            Assert.That(await client.OpenArtifactContentAsync(session.Id, ct), Is.Null);
            Assert.That(await client.DeleteArtifactAsync(session.Id, ct), Is.False, "already gone");
            Assert.That(await client.GetArtifactAsync(summary.Id, ct), Is.Not.Null, "the other checkpoint stays");
        });
    }

    private static async Task<Dictionary<string, string?>> ReadArchiveAsync(ICoreClient client, Guid id, CancellationToken ct)
    {
        await using var content = await client.OpenArtifactContentAsync(id, ct)
                                  ?? throw new AssertionException($"checkpoint {id} has no content");
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        var entries = new Dictionary<string, string?>();
        await using var gzip = new GZipStream(buffer, CompressionMode.Decompress);
        await using var tar = new TarReader(gzip);
        while (await tar.GetNextEntryAsync(cancellationToken: ct) is { } entry)
        {
            if (entry.DataStream is null)
            {
                entries[entry.Name] = null;
                continue;
            }
            using var reader = new StreamReader(entry.DataStream);
            entries[entry.Name] = await reader.ReadToEndAsync(ct);
        }
        return entries;
    }

    private static async Task<string> ReadTextAsync(ICoreClient client, Guid id, CancellationToken ct)
    {
        await using var content = await client.OpenArtifactContentAsync(id, ct)
                                  ?? throw new AssertionException($"checkpoint {id} has no content");
        using var reader = new StreamReader(content);
        return await reader.ReadToEndAsync(ct);
    }
}
