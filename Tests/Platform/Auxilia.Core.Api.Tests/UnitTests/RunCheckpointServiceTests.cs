using System.Security.Cryptography;
using System.Text;
using Auxilia.Core.Api.Auth;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Auxilia.Core.Api.Tests.UnitTests;

/// <summary>
/// Run checkpoints: each save is a new version that replaces the previous one only once it is
/// stored, held encrypted, kept for the lifetime the most specific permitted level names, and
/// swept at its expiry (or the platform's current cap).
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class RunCheckpointServiceTests
{
    private FakeTimeProvider _time = null!;
    private InMemoryDataAccess<CoreArtifactRecord> _artifacts = null!;
    private InMemoryDataAccess<PlatformSettingRecord> _settingsStore = null!;
    private InMemoryDataAccess<AuditRecord> _audit = null!;
    private PlatformSettingsService _platformSettings = null!;
    private FakeMessageBusClient _bus = null!;
    private string _directory = "";
    private RunCheckpointService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));
        _artifacts = new InMemoryDataAccess<CoreArtifactRecord>();
        _settingsStore = new InMemoryDataAccess<PlatformSettingRecord>();
        _audit = new InMemoryDataAccess<AuditRecord>();
        _platformSettings = new PlatformSettingsService(
            _settingsStore, Options.Create(new CoreSecuritySettings()), new AuditLog(_audit, _time), _time);
        _bus = new FakeMessageBusClient();
        _directory = Path.Combine(Path.GetTempPath(), $"run-checkpoints-{Guid.NewGuid():N}");
        var settings = Options.Create(new CoreApiSettings { MaxCheckpointBytes = 1024, CheckpointLifetimeHours = 168 });
        _sut = new RunCheckpointService(
            _artifacts, new CheckpointLifetimePolicy(_platformSettings, settings),
            new AesGcmSettingsProtector(RandomNumberGenerator.GetBytes(32)), new RunCheckpointStorage(_directory),
            _bus, settings, _time, NullLogger<RunCheckpointService>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _sut.Dispose();
        _artifacts.Dispose();
        _settingsStore.Dispose();
        _audit.Dispose();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public async Task ASave_IsHeldEncrypted_AndReadBackAsSaved()
    {
        var run = Run();
        var content = "the whole conversation, nobody else's"u8.ToArray();

        var saved = await SaveAsync(run, content);

        var record = (await _artifacts.ReadAsync(saved.Id))!;
        Assert.Multiple(async () =>
        {
            Assert.That(saved.Kind, Is.EqualTo(ArtifactKinds.Checkpoint));
            Assert.That(saved.ContentHash, Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(content))));
            Assert.That(saved.SizeBytes, Is.EqualTo(content.Length));
            Assert.That(saved.WorkItemId, Is.EqualTo("WI-7"), "the run's work item, like every artifact of it");
            Assert.That(record.Encrypted, Is.True);
            Assert.That(Encoding.UTF8.GetString(File.ReadAllBytes(Directory.GetFiles(_directory).Single())),
                Does.Not.Contain("conversation"), "never on disk in the clear");
            Assert.That(await _sut.ReadContentAsync(record, default), Is.EqualTo(content));
        });
    }

    [Test]
    public async Task EachSave_IsTheNextVersion_AndReplacesThePreviousOne()
    {
        var run = Run();

        var first = await SaveAsync(run, "turn 1"u8.ToArray());
        var second = await SaveAsync(run, "turn 2"u8.ToArray());
        var otherName = await SaveAsync(run, "notes"u8.ToArray(), name: "notes");

        var left = (await _artifacts.ReadAsync()).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(first.Version, Is.EqualTo(1));
            Assert.That(second.Version, Is.EqualTo(2));
            Assert.That(otherName.Version, Is.EqualTo(1), "versions count per checkpoint name");
            Assert.That(left.Select(a => a.Id), Is.EquivalentTo(new[] { second.Id, otherName.Id }));
            Assert.That(Directory.GetFiles(_directory), Has.Length.EqualTo(2), "the replaced payload went too");
        });
    }

    [Test]
    public async Task ASaveAboveTheLimit_IsRefused_AndTheLastCopyStays()
    {
        var run = Run();
        var kept = await SaveAsync(run, "good"u8.ToArray());

        Assert.ThrowsAsync<RunCheckpointTooLargeException>(() => SaveAsync(run, new byte[1025]));

        Assert.That((await _artifacts.ReadAsync()).Select(a => a.Id), Is.EqualTo(new[] { kept.Id }));
    }

    [Test]
    public async Task ASave_IsAnnouncedOnTheArtifactStream_AsACheckpoint()
    {
        var saved = await SaveAsync(Run(), "state"u8.ToArray());

        var announced = _bus.PublishedMessages.Select(m => m.Message).OfType<ArtifactPersistedEvent>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(announced.ArtifactId, Is.EqualTo(saved.Id));
            Assert.That(announced.Kind, Is.EqualTo(ArtifactKinds.Checkpoint));
            Assert.That(announced.ExpiresUtc, Is.EqualTo(saved.ExpiresUtc));
        });
    }

    [Test]
    public async Task TheSweep_DeletesExpiredCopies_AndThoseBeyondALoweredCap()
    {
        var expiring = await SaveAsync(Run(), "a"u8.ToArray(), declaredHours: 1);
        var longLived = await SaveAsync(Run(), "b"u8.ToArray(), declaredHours: 100);
        var keptLonger = await SaveAsync(Run(), "c"u8.ToArray(), declaredHours: 100);

        _time.Advance(TimeSpan.FromHours(2));
        var firstSweep = await _sut.SweepOnceAsync();
        await _platformSettings.SetAsync(null, PlatformSettingKeys.CheckpointLifetimeMaxHours, "1.5", default);
        _time.Advance(TimeSpan.FromMinutes(1));
        await _artifacts.SaveAsync((await _artifacts.ReadAsync(keptLonger.Id))! with { CreatedUtc = _time.GetUtcNow() });
        var secondSweep = await _sut.SweepOnceAsync();

        var left = (await _artifacts.ReadAsync()).Select(a => a.Id).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(firstSweep, Is.EqualTo(1));
            Assert.That(secondSweep, Is.EqualTo(1), "a cap lowered later applies to copies saved before it");
            Assert.That(left, Is.EqualTo(new[] { keptLonger.Id }));
            Assert.That(left, Does.Not.Contain(expiring.Id).And.Not.Contain(longLived.Id));
            Assert.That(Directory.GetFiles(_directory), Has.Length.EqualTo(1));
        });
    }

    [Test]
    public async Task TheSweep_RemovesAbandonedHalfWrites_ButNeverAWholePayloadWithoutItsRecord()
    {
        Directory.CreateDirectory(_directory);
        var abandoned = Path.Combine(_directory, $"{Guid.NewGuid():N}.bin.partial");
        var unclaimed = Path.Combine(_directory, $"{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(abandoned, [1]);
        File.WriteAllBytes(unclaimed, [2]);
        File.SetLastWriteTimeUtc(abandoned, _time.GetUtcNow().UtcDateTime.AddHours(-1));
        File.SetLastWriteTimeUtc(unclaimed, _time.GetUtcNow().UtcDateTime.AddHours(-1));

        await _sut.SweepOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(abandoned), Is.False);
            Assert.That(File.Exists(unclaimed), Is.True, "only a record's expiry deletes a payload");
        });
    }

    [Test]
    public async Task CopiesLeftByRacingSaves_ConvergeOnTheNewest()
    {
        var run = Run();
        // Two saves of one name raced earlier and both landed as version 1.
        foreach (var minutesAgo in new[] { 2, 1 })
            await _artifacts.SaveAsync(new CoreArtifactRecord
            {
                Id = Guid.NewGuid(), ArtifactType = "session", WorkflowType = "checkpoint-workflow", RunInstanceId = run.Id,
                Version = 1, ContentHash = "x", CreatedUtc = _time.GetUtcNow().AddMinutes(-minutesAgo),
                Kind = ArtifactKinds.Checkpoint, ExpiresUtc = _time.GetUtcNow().AddDays(1), Encrypted = true,
            });

        var saved = await SaveAsync(run, "turn 3"u8.ToArray());

        Assert.Multiple(async () =>
        {
            Assert.That(saved.Version, Is.EqualTo(2));
            Assert.That((await _artifacts.ReadAsync()).Select(a => a.Id), Is.EqualTo(new[] { saved.Id }), "one copy left");
        });
    }

    [Test]
    public async Task ACopyPastItsLifetime_IsNotServed_EvenBeforeTheSweep()
    {
        var saved = await SaveAsync(Run(), "state"u8.ToArray(), declaredHours: 1);
        var record = (await _artifacts.ReadAsync(saved.Id))!;

        _time.Advance(TimeSpan.FromHours(1));

        Assert.That(await _sut.ReadContentAsync(record, default), Is.Null);
    }

    [Test]
    public async Task APayloadThatIsNotTheRecordedContent_IsNotServed()
    {
        var first = (await _artifacts.ReadAsync((await SaveAsync(Run(), "alice's session"u8.ToArray())).Id))!;
        var second = (await _artifacts.ReadAsync((await SaveAsync(Run(), "bob's session"u8.ToArray())).Id))!;
        var gone = (await _artifacts.ReadAsync((await SaveAsync(Run(), "gone"u8.ToArray())).Id))!;
        var corrupt = (await _artifacts.ReadAsync((await SaveAsync(Run(), "corrupt"u8.ToArray())).Id))!;
        string PayloadOf(Guid id) => Path.Combine(_directory, $"{id:N}.bin");

        // Someone with write access to the storage swaps two payloads, removes one, damages one.
        var firstBytes = File.ReadAllBytes(PayloadOf(first.Id));
        File.WriteAllBytes(PayloadOf(first.Id), File.ReadAllBytes(PayloadOf(second.Id)));
        File.WriteAllBytes(PayloadOf(second.Id), firstBytes);
        File.Delete(PayloadOf(gone.Id));
        var damaged = File.ReadAllBytes(PayloadOf(corrupt.Id));
        damaged[^1] ^= 0xFF;
        File.WriteAllBytes(PayloadOf(corrupt.Id), damaged);

        Assert.Multiple(async () =>
        {
            Assert.That(await _sut.ReadContentAsync(first, default), Is.Null, "never one run's content as another's");
            Assert.That(await _sut.ReadContentAsync(second, default), Is.Null);
            Assert.That(await _sut.ReadContentAsync(gone, default), Is.Null);
            Assert.That(await _sut.ReadContentAsync(corrupt, default), Is.Null);
        });
    }

    [Test]
    public async Task WithoutAProtectionKey_ACopyIsMarkedAsKeptInTheClear()
    {
        var settings = Options.Create(new CoreApiSettings { MaxCheckpointBytes = 1024 });
        using var plain = new RunCheckpointService(
            _artifacts, new CheckpointLifetimePolicy(_platformSettings, settings), new NullSettingsProtector(),
            new RunCheckpointStorage(_directory), _bus, settings, _time, NullLogger<RunCheckpointService>.Instance);
        var run = Run();

        var saved = await plain.SaveAsync(run, run.Id, "session", null, new MemoryStream("state"u8.ToArray()), null, null, default);

        var record = (await _artifacts.ReadAsync(saved.Id))!;
        Assert.Multiple(async () =>
        {
            Assert.That(record.Encrypted, Is.False, "the record never claims encryption it did not get");
            Assert.That(await plain.ReadContentAsync(record, default), Is.EqualTo("state"u8.ToArray()));
        });
    }

    [Test]
    public async Task OnlyACheckpoint_IsDeletedThroughTheService()
    {
        var saved = (await _artifacts.ReadAsync((await SaveAsync(Run(), "x"u8.ToArray())).Id))!;
        var output = saved with { Id = Guid.NewGuid(), Kind = null };
        await _artifacts.SaveAsync(output);

        Assert.Multiple(async () =>
        {
            Assert.That(await _sut.DeleteAsync(output, default), Is.False);
            Assert.That(await _sut.DeleteAsync(saved, default), Is.True);
            Assert.That(await _sut.DeleteAsync(saved, default), Is.False, "already gone");
            Assert.That(Directory.GetFiles(_directory), Is.Empty);
        });
    }

    // ------------------------------------------------------------------ helpers

    private static CoreRunRecord Run(double? requestedHours = null)
    {
        var id = Guid.NewGuid();
        var command = new RunWorkflowCommand(
            id, "checkpoint-workflow", "registry://checkpoint-workflow",
            new Dictionary<string, string> { ["WorkItemId"] = "WI-7" }, CheckpointLifetimeHours: requestedHours);
        return new CoreRunRecord
        {
            Id = id,
            CommandId = id,
            WorkflowType = "checkpoint-workflow",
            State = RunStates.Running,
            DispatchCommandJson = System.Text.Json.JsonSerializer.Serialize(command),
        };
    }

    private Task<ArtifactDto> SaveAsync(CoreRunRecord run, byte[] content, string name = "session", double? declaredHours = null)
        => _sut.SaveAsync(run, run.Id, name, "application/gzip", new MemoryStream(content), content.Length, declaredHours, default);
}

/// <summary>
/// The checkpoint lifetime: the run request over the workflow's declaration over the general one,
/// as far as the overrides setting lets each level — and the cap over all of them.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class CheckpointLifetimePolicyTests
{
    private InMemoryDataAccess<PlatformSettingRecord> _store = null!;
    private InMemoryDataAccess<AuditRecord> _audit = null!;
    private PlatformSettingsService _settings = null!;
    private CheckpointLifetimePolicy _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new InMemoryDataAccess<PlatformSettingRecord>();
        _audit = new InMemoryDataAccess<AuditRecord>();
        _settings = new PlatformSettingsService(
            _store, Options.Create(new CoreSecuritySettings()), new AuditLog(_audit, TimeProvider.System), TimeProvider.System);
        _sut = new CheckpointLifetimePolicy(_settings, Options.Create(new CoreApiSettings { CheckpointLifetimeHours = 168 }));
    }

    [TearDown]
    public void TearDown()
    {
        _store.Dispose();
        _audit.Dispose();
    }

    [TestCase(null, null, null, null, 168, TestName = "Nothing set — the deployment's general lifetime")]
    [TestCase(null, null, null, 72.0, 72, TestName = "The general setting replaces the deployment's")]
    [TestCase(null, null, 48.0, 72.0, 48, TestName = "The workflow's declaration overrides the general one")]
    [TestCase(null, 6.0, 48.0, 72.0, 6, TestName = "The run request overrides the workflow's")]
    [TestCase("workflow", 6.0, 48.0, 72.0, 48, TestName = "overrides=workflow — a run's request does not apply")]
    [TestCase("none", 6.0, 48.0, 72.0, 72, TestName = "overrides=none — only the general lifetime applies")]
    public async Task TheMostSpecificPermittedLevel_Wins(
        string? overrides, double? run, double? workflow, double? general, double expectedHours)
    {
        if (overrides is not null)
            await _settings.SetAsync(null, PlatformSettingKeys.CheckpointLifetimeOverrides, overrides, default);
        if (general is not null)
            await _settings.SetAsync(null, PlatformSettingKeys.CheckpointLifetimeHours, general.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), default);

        Assert.That(await _sut.ResolveAsync(run, workflow, default), Is.EqualTo(TimeSpan.FromHours(expectedHours)));
    }

    [Test]
    public async Task ByDefault_TheDeploymentCapBoundsEveryLevel_AndZeroLiftsIt()
    {
        var uncapped = new CheckpointLifetimePolicy(_settings,
            Options.Create(new CoreApiSettings { CheckpointLifetimeHours = 168, CheckpointLifetimeMaxHours = 0 }));

        Assert.Multiple(async () =>
        {
            Assert.That(await _sut.ResolveAsync(null, 1000, default), Is.EqualTo(TimeSpan.FromHours(720)),
                "thirty days unless the platform names another cap");
            Assert.ThrowsAsync<CheckpointLifetimeRefusedException>(() => _sut.ValidateRunRequestAsync(721, default));
            Assert.That(await uncapped.ResolveAsync(null, 1000, default), Is.EqualTo(TimeSpan.FromHours(1000)));
        });
    }

    [Test]
    public async Task TheCap_CutsTheWorkflowsAndTheGeneralLifetime()
    {
        await _settings.SetAsync(null, PlatformSettingKeys.CheckpointLifetimeMaxHours, "24", default);

        Assert.Multiple(async () =>
        {
            Assert.That(await _sut.ResolveAsync(null, 48, default), Is.EqualTo(TimeSpan.FromHours(24)));
            Assert.That(await _sut.ResolveAsync(null, null, default), Is.EqualTo(TimeSpan.FromHours(24)), "the 168 deployment default too");
            Assert.That(await _sut.ResolveAsync(12, 48, default), Is.EqualTo(TimeSpan.FromHours(12)));
        });
    }

    [Test]
    public async Task ARunRequest_ThePlatformDoesNotAllow_IsRefused()
    {
        Assert.DoesNotThrowAsync(() => _sut.ValidateRunRequestAsync(6, default));
        Assert.DoesNotThrowAsync(() => _sut.ValidateRunRequestAsync(null, default));
        Assert.ThrowsAsync<CheckpointLifetimeRefusedException>(() => _sut.ValidateRunRequestAsync(0, default));
        Assert.ThrowsAsync<CheckpointLifetimeRefusedException>(() => _sut.ValidateRunRequestAsync(double.NaN, default));

        await _settings.SetAsync(null, PlatformSettingKeys.CheckpointLifetimeMaxHours, "24", default);
        Assert.ThrowsAsync<CheckpointLifetimeRefusedException>(() => _sut.ValidateRunRequestAsync(25, default));

        await _settings.SetAsync(null, PlatformSettingKeys.CheckpointLifetimeOverrides, "workflow", default);
        Assert.ThrowsAsync<CheckpointLifetimeRefusedException>(() => _sut.ValidateRunRequestAsync(6, default));
    }

    [TestCase(PlatformSettingKeys.CheckpointLifetimeHours, "0")]
    [TestCase(PlatformSettingKeys.CheckpointLifetimeHours, "a week")]
    [TestCase(PlatformSettingKeys.CheckpointLifetimeMaxHours, "-1")]
    [TestCase(PlatformSettingKeys.CheckpointLifetimeMaxHours, "1e9")]
    [TestCase(PlatformSettingKeys.CheckpointLifetimeOverrides, "everyone")]
    public void AMalformedSetting_IsRefused(string key, string value)
        => Assert.ThrowsAsync<ArgumentException>(() => _settings.SetAsync(null, key, value, default));
}
