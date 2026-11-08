using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Auxilia.Workflows.Testing.DummyWorkflows;

namespace Auxilia.SystemTestSuite.CoreClientSurface;

/// <summary>
/// Run input files and required tools, end to end (real Core, runner behind the Docker API guard,
/// SDK): a file uploaded for a run reaches exactly that run's container — byte for byte, never in a
/// view, the run record or the log — and belongs to that run alone; a run that needs a tool version
/// the image does not provide ends PreFlightFailed instead of running.
/// </summary>
[TestFixture]
[Category("System")]
public sealed class RunInputFileSystemTests
{
    [Test]
    [CancelAfter(180_000)]
    public async Task AFileUploadedForARun_ReachesItsContainer_ByteForByte_AndNothingElse(CancellationToken ct)
    {
        var client = CoreClientEnvironment.CreateClient();
        var marker = "session-marker-" + Guid.NewGuid().ToString("N");
        var content = Encoding.UTF8.GetBytes(marker + new string('x', 200_000));
        var upload = await client.UploadRunInputFileAsync(new MemoryStream(content), ct);

        var accepted = await client.RunAsync(Request(upload.Id, InputFileWorkflow.ProvidedTool), ct);
        var (state, _) = await ClientStreamProbe.AwaitTerminalAsync(client, accepted.RunId, ct);
        var reading = JsonSerializer.Deserialize<InputFileWorkflow.InputFileReading>(
            (await client.GetRunViewsAsync(accepted.RunId, InputFileWorkflow.ViewName, ct: ct)).Items.Single().PayloadJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var stored = (await client.GetRunViewsAsync(accepted.RunId, take: 500, ct: ct)).Items.Select(v => v.PayloadJson).ToList();
        stored.Add(JsonSerializer.Serialize(await client.GetRunAsync(accepted.RunId, ct)));
        stored.Add(await ContainerLogAsync(client, accepted.RunId, ct));
        var reuse = Assert.ThrowsAsync<CoreApiException>(() => client.RunAsync(Request(upload.Id, null), ct));

        Assert.Multiple(() =>
        {
            Assert.That(state, Is.EqualTo(RunStates.Success), "the image provides the tool the run required");
            Assert.That(upload.Sha256, Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(content))));
            Assert.That(reading, Is.EqualTo(new InputFileWorkflow.InputFileReading(upload.Sha256, content.Length)),
                "the container got exactly the uploaded bytes");
            Assert.That(stored, Has.None.Contains(marker), "the content in no view, run record or log");
            Assert.That(reuse!.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), "the upload belonged to that run alone");
        });
    }

    [Test]
    [CancelAfter(180_000)]
    public async Task ARunThatNeedsAnotherToolVersion_EndsPreFlightFailed(CancellationToken ct)
    {
        var client = CoreClientEnvironment.CreateClient();
        var upload = await client.UploadRunInputFileAsync(new MemoryStream("state"u8.ToArray()), ct);

        var accepted = await client.RunAsync(Request(upload.Id, "probe-cli@1.0.0"), ct);
        var (state, _) = await ClientStreamProbe.AwaitTerminalAsync(client, accepted.RunId, ct);
        var run = await client.GetRunAsync(accepted.RunId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(state, Is.EqualTo("PreFlightFailed"), "never a run on another version");
            Assert.That(run!.Error, Does.Contain("probe-cli@1.0.0").And.Contain(InputFileWorkflow.ProvidedTool),
                "the reason names what was needed and what the image has");
        });
    }

    private static RunRequest Request(Guid uploadId, string? requiredTool)
        => new(CoreClientEnvironment.InputFileWorkflowType,
            CoreClientEnvironment.ContextFor(InputFileWorkflow.WorkflowName))
        {
            InputFiles = new Dictionary<string, Guid> { [InputFileWorkflow.FileName] = uploadId },
            RequiredTools = requiredTool is null ? null : [requiredTool],
        };

    /// <summary>The run's persisted container log (written when the container is collected).</summary>
    private static async Task<string> ContainerLogAsync(ICoreClient client, Guid runId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            var log = (await client.QueryArtifactsAsync(new ArtifactQuery(ArtifactType: "container-log", RunId: runId), ct))
                .Items.FirstOrDefault();
            if (log is not null && await client.OpenArtifactContentAsync(log.Id, ct) is { } content)
            {
                await using (content)
                using (var reader = new StreamReader(content))
                    return await reader.ReadToEndAsync(ct);
            }
            if (DateTimeOffset.UtcNow > deadline)
                Assert.Fail($"run {runId} left no container log");
            await Task.Delay(500, ct);
        }
    }
}
