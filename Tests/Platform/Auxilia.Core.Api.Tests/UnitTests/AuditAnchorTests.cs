using Auxilia.Core.Api.Services;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess.Implementations;

namespace Auxilia.Core.Api.Tests.UnitTests;

/// <summary>
/// The Core's audit writer anchors every stored record outside the store, so cutting the trail
/// — even its very last record — is reported by the integrity check.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class AuditAnchorTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task EvenTheLastRecord_CannotBeCutUndetected(bool batched)
    {
        var store = new InMemoryDataAccess<AuditRecord>();
        var integrity = new AuditIntegrity(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        var file = new AuditCheckpointFile(
            Path.Combine(Path.GetTempPath(), $"auxilia-cp-{Guid.NewGuid():N}.jsonl"), integrity, TimeProvider.System);
        try
        {
            await using var log = new AuditLog(store, TimeProvider.System,
                new AuditLogSettings { BatchedWrites = batched }, integrity, anchor: file);
            for (var i = 0; i < 4; i++)
                await log.AppendAsync("core-api", "action", $"s{i}", "ok");
            await log.FlushAsync();
            var reads = new AuditReadService(store, log, file);

            var anchored = await reads.VerifyAsync(CancellationToken.None);
            Assert.That((anchored.Intact, anchored.AnchoredChains), Is.EqualTo((true, 1)));

            await store.RemoveAsync((await store.ReadAsync()).Single(r => r.Sequence == 4).Id);
            var truncated = await reads.VerifyAsync(CancellationToken.None);

            Assert.That(truncated.Intact, Is.False);
            Assert.That(truncated.Breaks.Single().Problem, Does.Contain("checkpointed head"));
        }
        finally
        {
            File.Delete(file.Path);
        }
    }
}
