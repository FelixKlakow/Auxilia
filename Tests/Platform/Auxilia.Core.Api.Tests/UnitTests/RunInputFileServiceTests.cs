using System.Security.Cryptography;
using System.Text;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess.Implementations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Auxilia.Core.Api.Tests.UnitTests;

/// <summary>
/// Run input files: held encrypted, the uploader's alone until one run of theirs takes it, served
/// for that run only, and deleted once no run may receive it any more.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class RunInputFileServiceTests
{
    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();
    private static readonly IReadOnlyList<WorkflowInputFileDto> Declared =
    [
        new("session-state", "the session to continue", Required: true),
        new("notes", null, Required: false),
    ];

    private FakeTimeProvider _time = null!;
    private InMemoryDataAccess<CoreRunInputFileRecord> _files = null!;
    private InMemoryDataAccess<CoreRunRecord> _runs = null!;
    private string _directory = "";
    private RunInputFileService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));
        _files = new InMemoryDataAccess<CoreRunInputFileRecord>();
        _runs = new InMemoryDataAccess<CoreRunRecord>();
        _directory = Path.Combine(Path.GetTempPath(), $"run-input-files-{Guid.NewGuid():N}");
        _sut = new RunInputFileService(
            _files, _runs, new AesGcmSettingsProtector(RandomNumberGenerator.GetBytes(32)),
            new RunInputFileStorage(_directory),
            Options.Create(new CoreApiSettings { MaxRunInputFileBytes = 1024, RunInputFileTtlMinutes = 60 }),
            _time, NullLogger<RunInputFileService>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _sut.Dispose();
        _files.Dispose();
        _runs.Dispose();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public async Task AnUpload_IsHeldEncrypted_AndDescribedByItsDigest()
    {
        var content = "the whole conversation, which is nobody else's"u8.ToArray();

        var file = await UploadAsync(Alice, content);

        var stored = Directory.GetFiles(_directory).Single();
        Assert.Multiple(() =>
        {
            Assert.That(file.Length, Is.EqualTo(content.Length));
            Assert.That(file.Sha256, Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(content))));
            Assert.That(file.ExpiresUtc, Is.EqualTo(_time.GetUtcNow().AddMinutes(60)));
            Assert.That(Encoding.UTF8.GetString(File.ReadAllBytes(stored)), Does.Not.Contain("conversation"),
                "never on disk in the clear");
        });
    }

    [Test]
    public void AnUploadAboveTheLimit_IsRefused()
        => Assert.ThrowsAsync<RunInputFileTooLargeException>(() => UploadAsync(Alice, new byte[1025]));

    [Test]
    public async Task ARunTakesItsCallersUpload_AndOnlyThatRunIsServedIt()
    {
        var content = "state"u8.ToArray();
        var file = await UploadAsync(Alice, content);
        var command = Guid.NewGuid();

        var names = await _sut.BindToRunAsync(command, "wf", Files(("session-state", file.Id)), Declared, Alice, default);

        Assert.That(names, Is.EqualTo(new[] { "session-state" }));
        Assert.That((await _sut.OpenAsync(command, "session-state", default))!.Value.Content, Is.EqualTo(content));
        Assert.That(await _sut.OpenAsync(Guid.NewGuid(), "session-state", default), Is.Null, "another run gets nothing");
        Assert.That(await _sut.OpenAsync(command, "notes", default), Is.Null, "a name it did not receive");
    }

    [Test]
    public async Task SomeoneElsesUpload_IsAsGoodAsMissing()
    {
        var file = await UploadAsync(Alice, "state"u8.ToArray());

        Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _sut.BindToRunAsync(Guid.NewGuid(), "wf", Files(("session-state", file.Id)), Declared, Bob, default));
    }

    [Test]
    public async Task AnUpload_BelongsToOneRunOnly()
    {
        var file = await UploadAsync(Alice, "state"u8.ToArray());
        await _sut.BindToRunAsync(Guid.NewGuid(), "wf", Files(("session-state", file.Id)), Declared, Alice, default);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.BindToRunAsync(Guid.NewGuid(), "wf", Files(("session-state", file.Id)), Declared, Alice, default));
        Assert.That(ex!.Message, Does.Contain("already belongs to a run"));
    }

    [Test]
    public async Task WhatTheWorkflowDoesNotDeclare_OrStillNeeds_IsRefused_AndNothingStaysBound()
    {
        var state = await UploadAsync(Alice, "state"u8.ToArray());
        var extra = await UploadAsync(Alice, "extra"u8.ToArray());

        Assert.ThrowsAsync<InvalidOperationException>(() => _sut.BindToRunAsync(
            Guid.NewGuid(), "wf", Files(("session-state", state.Id), ("secrets", extra.Id)), Declared, Alice, default));
        Assert.ThrowsAsync<InvalidOperationException>(() => _sut.BindToRunAsync(
            Guid.NewGuid(), "wf", Files(("notes", extra.Id)), Declared, Alice, default),
            "the required session-state is missing");
        Assert.That((await _files.ReadAsync()).ToList().All(f => f.BoundCommandId is null), Is.True);
    }

    [Test]
    public async Task AFailedBind_ReleasesWhatItAlreadyTook()
    {
        var state = await UploadAsync(Alice, "state"u8.ToArray());
        var bobs = await UploadAsync(Bob, "notes"u8.ToArray());

        Assert.ThrowsAsync<KeyNotFoundException>(() => _sut.BindToRunAsync(
            Guid.NewGuid(), "wf", Files(("session-state", state.Id), ("notes", bobs.Id)), Declared, Alice, default));

        Assert.That((await _files.ReadAsync(state.Id))!.BoundCommandId, Is.Null, "free for Alice's next run");
    }

    [Test]
    public async Task AnUploadPastItsExpiry_IsRefused()
    {
        var file = await UploadAsync(Alice, "state"u8.ToArray());
        _time.Advance(TimeSpan.FromMinutes(61));

        Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.BindToRunAsync(Guid.NewGuid(), "wf", Files(("session-state", file.Id)), Declared, Alice, default));
    }

    [Test]
    public async Task AFailoverReDispatch_TakesTheRunsFilesOver()
    {
        var file = await UploadAsync(Alice, "state"u8.ToArray());
        var original = Guid.NewGuid();
        var replacement = Guid.NewGuid();
        await _sut.BindToRunAsync(original, "wf", Files(("session-state", file.Id)), Declared, Alice, default);

        Assert.That(await _sut.HandOverAsync(original, replacement, default), Is.EqualTo(new[] { "session-state" }));
        Assert.That(await _sut.OpenAsync(replacement, "session-state", default), Is.Not.Null);
        Assert.That(await _sut.OpenAsync(original, "session-state", default), Is.Null);
    }

    [Test]
    public async Task TheSweep_DeletesWhatNoRunMayReceiveAnyMore()
    {
        var unused = await UploadAsync(Alice, "unused"u8.ToArray());
        var ofEndedRun = await UploadAsync(Alice, "ended"u8.ToArray());
        var ofLiveRun = await UploadAsync(Alice, "live"u8.ToArray());
        var ended = Guid.NewGuid();
        var live = Guid.NewGuid();
        await _sut.BindToRunAsync(ended, "wf", Files(("session-state", ofEndedRun.Id)), Declared, Alice, default);
        await _sut.BindToRunAsync(live, "wf", Files(("session-state", ofLiveRun.Id)), Declared, Alice, default);
        await RunAsync(live, RunStates.Running);
        await RunAsync(ended, RunStates.Success);

        _time.Advance(RunInputFileService.EndedGrace - TimeSpan.FromSeconds(1));
        Assert.That(await _sut.SweepOnceAsync(), Is.Zero, "a failover may still take the ended run's files over");

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.That(await _sut.SweepOnceAsync(), Is.EqualTo(1));
        Assert.That(await _files.ReadAsync(ofEndedRun.Id), Is.Null, "the ended run's file is gone");

        _time.Advance(TimeSpan.FromMinutes(60));
        Assert.That(await _sut.SweepOnceAsync(), Is.EqualTo(1));
        Assert.Multiple(async () =>
        {
            Assert.That(await _files.ReadAsync(unused.Id), Is.Null, "never taken, expired");
            Assert.That(await _files.ReadAsync(ofLiveRun.Id), Is.Not.Null, "a live run keeps its file");
            Assert.That(Directory.GetFiles(_directory), Has.Length.EqualTo(1), "the content goes with the record");
        });
    }

    private Task<RunInputFile> UploadAsync(Guid owner, byte[] content)
        => _sut.UploadAsync(owner, new MemoryStream(content), default);

    private static Dictionary<string, Guid> Files(params (string Name, Guid Id)[] files)
        => files.ToDictionary(f => f.Name, f => f.Id);

    private Task RunAsync(Guid commandId, string state)
        => _runs.SaveAsync(new CoreRunRecord
        {
            Id = commandId,
            CommandId = commandId,
            WorkflowType = "wf",
            State = state,
            CreatedUtc = _time.GetUtcNow(),
            UpdatedUtc = _time.GetUtcNow(),
            CompletedUtc = RunStates.IsTerminal(state) ? _time.GetUtcNow() : null,
        });
}
