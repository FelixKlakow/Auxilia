using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Auxilia.Core.Runner.Workflows;
using Auxilia.Core.Runner.Workflows.Storage;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.Workflows;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Runner.Tests.Workflows;

/// <summary>
/// The runner's side of a run checkpoint: the workflow only names a declared checkpoint, the
/// runner reads it from the run's output directory — a file as it is, a folder as a .tar.gz, never
/// through a link the workload planted — and hands it to the Core with the run's resolution token.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class RunCheckpointConnectorTests
{
    private string _root = "";
    private WorkflowDispatcherSettings _settings = null!;
    private WorkflowInstanceRegistry _instances = null!;
    private WorkflowSchemaStore _schemas = null!;
    private InMemoryDataAccess<WorkflowInstanceRecord> _instanceRecords = null!;
    private InMemoryDataAccess<AuditRecord> _audit = null!;
    private ISettingsProtector _protector = null!;
    private CapturingUploader _uploader = null!;
    private RunCheckpointConnector _sut = null!;
    private Guid _instanceId;
    private RunWorkflowCommand _command = null!;

    [SetUp]
    public async Task SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"auxilia-checkpoints-{Guid.NewGuid():N}");
        _settings = new WorkflowDispatcherSettings
        {
            RunOutputDirectory = Path.Combine(_root, "run-output"),
            MaxCheckpointBytes = 16 * 1024,
        };
        _instanceRecords = new InMemoryDataAccess<WorkflowInstanceRecord>();
        _instances = new WorkflowInstanceRegistry(_instanceRecords, TimeProvider.System);
        _schemas = TestStores.NewWorkflowSchemaStore();
        _audit = new InMemoryDataAccess<AuditRecord>();
        _protector = new AesGcmSettingsProtector(RandomNumberGenerator.GetBytes(32));
        _uploader = new CapturingUploader();
        _sut = new RunCheckpointConnector(
            _instances, _schemas, _uploader, _protector, Options.Create(_settings),
            new AuditLog(_audit, TimeProvider.System), NullLogger<RunCheckpointConnector>.Instance);

        await _schemas.SetSchemaAsync("session-workflow", new WorkflowSchema("session-workflow", [], [])
        {
            Checkpoints =
            [
                new WorkflowCheckpointDescriptor("session", "state/session"),
                new WorkflowCheckpointDescriptor("summary", "summary.md"),
            ],
        });
        _instanceId = Guid.NewGuid();
        _command = new RunWorkflowCommand(
            Guid.NewGuid(), "session-workflow", "registry://session-workflow", new Dictionary<string, string>(),
            ResolutionToken: "the-runs-token");
        await _instances.CreateAsync(_instanceId, "session-workflow", "Running",
            dispatchCommandJson: JsonSerializer.Serialize(DispatchCommandProtection.Protect(_command, _protector)));
        Directory.CreateDirectory(OutputDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        _instanceRecords.Dispose();
        _audit.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string OutputDirectory => Path.Combine(_settings.RunOutputDirectory, _instanceId.ToString("N"));

    [Test]
    public async Task ADeclaredFile_IsHandedToTheCore_WithTheRunsToken()
    {
        await File.WriteAllTextAsync(Path.Combine(OutputDirectory, "summary.md"), "what happened");

        var result = await SaveAsync("summary");

        var upload = _uploader.Uploads.Single();
        Assert.Multiple(async () =>
        {
            Assert.That(result, Is.EqualTo("""{"ok":true}"""), "the Core's answer goes back to the workflow");
            Assert.That(upload.CoreRunId, Is.EqualTo(_command.CommandId));
            Assert.That(upload.InstanceId, Is.EqualTo(_instanceId));
            Assert.That(upload.Token, Is.EqualTo("the-runs-token"), "the stored token, unprotected for the Core");
            Assert.That(upload.Archive, Is.False);
            Assert.That(Encoding.UTF8.GetString(upload.Content), Is.EqualTo("what happened"));
            Assert.That((await _audit.ReadAsync()).Select(a => a.Action), Does.Contain("artifact.checkpoint-saved"));
        });
    }

    [Test]
    public async Task ADeclaredFolder_ArrivesAsATarGz_WithItsTree()
    {
        var session = Path.Combine(OutputDirectory, "state", "session");
        Directory.CreateDirectory(Path.Combine(session, "turns"));
        await File.WriteAllTextAsync(Path.Combine(session, "events.jsonl"), "{\"turn\":1}");
        await File.WriteAllTextAsync(Path.Combine(session, "turns", "1.md"), "hello");

        await SaveAsync("session");

        var upload = _uploader.Uploads.Single();
        Assert.Multiple(() =>
        {
            Assert.That(upload.Archive, Is.True);
            Assert.That(ReadArchive(upload.Content), Is.EquivalentTo(new Dictionary<string, string?>
            {
                ["events.jsonl"] = "{\"turn\":1}",
                ["turns/"] = null,
                ["turns/1.md"] = "hello",
            }));
        });
    }

    [Test]
    public void AnUndeclaredName_ANotYetWrittenOne_OrAnUnknownOperation_IsRefused()
    {
        Assert.Multiple(() =>
        {
            Assert.ThrowsAsync<InvalidOperationException>(() => SaveAsync("secrets"));
            Assert.ThrowsAsync<InvalidOperationException>(() => SaveAsync("summary"), "not written yet");
            Assert.ThrowsAsync<InvalidOperationException>(() => _sut.ExecuteAsync(
                _instanceId, "delete", """{"name":"summary"}""", default));
        });
        Assert.That(_uploader.Uploads, Is.Empty);
    }

    [Test]
    public void ACheckpointAboveTheLimit_IsRefused()
    {
        File.WriteAllBytes(Path.Combine(OutputDirectory, "summary.md"), new byte[16 * 1024 + 1]);

        Assert.ThrowsAsync<SafeRunOutput.TooLargeException>(() => SaveAsync("summary"));
        Assert.That(_uploader.Uploads, Is.Empty);
    }

    [Test]
    public void ACheckpointReachedThroughALink_IsRefused()
    {
        var secret = Path.Combine(_root, "runner-secret.txt");
        File.WriteAllText(secret, "the runner's own environment");
        SafeRunOutputTests.LinkOrIgnore(() => File.CreateSymbolicLink(Path.Combine(OutputDirectory, "summary.md"), secret));

        Assert.ThrowsAsync<InvalidOperationException>(() => SaveAsync("summary"));
        Assert.That(_uploader.Uploads, Is.Empty);
    }

    private Task<string> SaveAsync(string name)
        => _sut.ExecuteAsync(_instanceId, RunCheckpointsResource.SaveOperation,
            JsonSerializer.Serialize(new RunCheckpointsResource.SaveRequest(name), JsonSerializerOptions.Web), default);

    internal static Dictionary<string, string?> ReadArchive(byte[] archive)
    {
        var entries = new Dictionary<string, string?>();
        using var gzip = new GZipStream(new MemoryStream(archive), CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.DataStream is null)
            {
                entries[entry.Name] = null;
                continue;
            }
            using var reader = new StreamReader(entry.DataStream);
            entries[entry.Name] = reader.ReadToEnd();
        }
        return entries;
    }

    private sealed class CapturingUploader : IRunCheckpointUploader
    {
        public List<(Guid CoreRunId, Guid InstanceId, string Token, string Name, byte[] Content, bool Archive)> Uploads { get; } = [];

        public Task<string> UploadAsync(
            Guid coreRunId, Guid instanceId, string resolutionToken, string name, byte[] content, bool archive,
            CancellationToken ct)
        {
            Uploads.Add((coreRunId, instanceId, resolutionToken, name, content, archive));
            return Task.FromResult("""{"ok":true}""");
        }
    }
}

/// <summary>
/// Reading what a workload left in its output directory: regular files and directories inside
/// it — never a link out of it, whatever component it sits on.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class SafeRunOutputTests
{
    private string _root = "";
    private string _output = "";

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"auxilia-safe-output-{Guid.NewGuid():N}");
        _output = Path.Combine(_root, "output");
        Directory.CreateDirectory(_output);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>Creates a link, or skips the test where this machine may not (Windows without Developer Mode).</summary>
    internal static void LinkOrIgnore(Action createLink)
    {
        try
        {
            createLink();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Ignore($"this machine cannot create symbolic links: {ex.Message}");
        }
    }

    [Test]
    public void AFileInside_IsRead_AndAPathOutOfTheDirectory_IsRefused()
    {
        File.WriteAllText(Path.Combine(_output, "a.txt"), "inside");
        File.WriteAllText(Path.Combine(_root, "outside.txt"), "outside");

        using var inside = SafeRunOutput.OpenFile(_output, "a.txt");
        Assert.Multiple(() =>
        {
            Assert.That(SafeRunOutput.KindOf(_output, "a.txt"), Is.EqualTo(SafeRunOutput.EntryKind.File));
            Assert.That(new StreamReader(inside!).ReadToEnd(), Is.EqualTo("inside"));
            Assert.That(SafeRunOutput.KindOf(_output, "../outside.txt"), Is.EqualTo(SafeRunOutput.EntryKind.Refused));
            Assert.That(SafeRunOutput.OpenFile(_output, "../outside.txt"), Is.Null);
            Assert.That(SafeRunOutput.KindOf(_output, "nothing-yet"), Is.EqualTo(SafeRunOutput.EntryKind.Missing));
        });
    }

    [Test]
    public void AFileLink_AndADirectoryLinkOnTheWay_AreRefused()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "not the workload's");
        LinkOrIgnore(() => File.CreateSymbolicLink(Path.Combine(_output, "file-link"), Path.Combine(outside, "secret.txt")));
        LinkOrIgnore(() => Directory.CreateSymbolicLink(Path.Combine(_output, "dir-link"), outside));

        Assert.Multiple(() =>
        {
            Assert.That(SafeRunOutput.KindOf(_output, "file-link"), Is.EqualTo(SafeRunOutput.EntryKind.Refused));
            Assert.That(SafeRunOutput.OpenFile(_output, "file-link"), Is.Null);
            Assert.That(SafeRunOutput.KindOf(_output, "dir-link/secret.txt"), Is.EqualTo(SafeRunOutput.EntryKind.Refused));
            Assert.That(SafeRunOutput.OpenFile(_output, "dir-link/secret.txt"), Is.Null);
            Assert.ThrowsAsync<InvalidOperationException>(() =>
                SafeRunOutput.ArchiveDirectoryAsync(_output, "dir-link", new MemoryStream(), 1024, default));
        });
    }

    [Test]
    public async Task AnArchivedFolder_LeavesItsLinksOut()
    {
        var folder = Path.Combine(_output, "session");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "kept.txt"), "kept");
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "not the workload's");
        LinkOrIgnore(() => File.CreateSymbolicLink(Path.Combine(folder, "planted"), Path.Combine(_root, "secret.txt")));
        LinkOrIgnore(() => Directory.CreateSymbolicLink(Path.Combine(folder, "planted-dir"), _root));

        using var archive = new MemoryStream();
        await SafeRunOutput.ArchiveDirectoryAsync(_output, "session", archive, 1 << 20, default);

        Assert.That(RunCheckpointConnectorTests.ReadArchive(archive.ToArray()),
            Is.EquivalentTo(new Dictionary<string, string?> { ["kept.txt"] = "kept" }));
    }

    [Test]
    public void AFolderAboveTheLimit_IsRefused_ByItsContent_OrByItsEntries()
    {
        var large = Path.Combine(_output, "large");
        Directory.CreateDirectory(large);
        File.WriteAllBytes(Path.Combine(large, "a.bin"), new byte[6000]);
        File.WriteAllBytes(Path.Combine(large, "b.bin"), new byte[6000]);
        var many = Path.Combine(_output, "many");
        Directory.CreateDirectory(many);
        for (var i = 0; i < 6; i++)
            File.WriteAllBytes(Path.Combine(many, $"{i}.txt"), []);

        Assert.Multiple(() =>
        {
            Assert.ThrowsAsync<SafeRunOutput.TooLargeException>(() =>
                SafeRunOutput.ArchiveDirectoryAsync(_output, "large", new MemoryStream(), 10_000, default));
            Assert.ThrowsAsync<SafeRunOutput.TooLargeException>(() =>
                SafeRunOutput.ArchiveDirectoryAsync(_output, "many", new MemoryStream(), 10_000, default),
                "empty files still cost their headers");
        });
    }

    [Test]
    public void AFileThatGrew_IsArchivedAsItWasWhenOpened_AndTheEntriesAfterItStayReadable()
    {
        using var archive = new MemoryStream();
        using (var tar = new TarWriter(archive, TarEntryFormat.Pax, leaveOpen: true))
        {
            // Opened at 10 bytes; the workload appended meanwhile.
            using var grown = new SafeRunOutput.Snapshot(new MemoryStream(Encoding.UTF8.GetBytes("0123456789-appended later")), 10);
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "log.jsonl") { DataStream = grown });
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "after.txt")
            {
                DataStream = new MemoryStream("after"u8.ToArray()),
            });
        }

        archive.Position = 0;
        var read = new Dictionary<string, string>();
        using (var reader = new TarReader(archive))
            while (reader.GetNextEntry() is { } entry)
                read[entry.Name] = new StreamReader(entry.DataStream!).ReadToEnd();

        Assert.That(read, Is.EquivalentTo(new Dictionary<string, string>
        {
            ["log.jsonl"] = "0123456789",
            ["after.txt"] = "after",
        }));
    }

    [Test]
    public void AFileThatShrank_FailsTheSave_InsteadOfACorruptArchive()
    {
        using var tar = new TarWriter(new MemoryStream(), TarEntryFormat.Pax);
        using var shrunk = new SafeRunOutput.Snapshot(new MemoryStream("01234"u8.ToArray()), 10);

        Assert.Throws<IOException>(() =>
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "log.jsonl") { DataStream = shrunk }));
    }
}
