using System.Security.Cryptography;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Contracts;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Services;

/// <summary>Where the encrypted content of run input files lives — beside the artifact payloads, shared by every Core node.</summary>
public sealed record RunInputFileStorage(string Directory);

/// <summary>An upload larger than <see cref="CoreApiSettings.MaxRunInputFileBytes"/>.</summary>
public sealed class RunInputFileTooLargeException(long limit)
    : Exception($"the file exceeds the run input file limit of {limit} bytes");

/// <summary>
/// Files a client hands a run (ARCHITECTURE §9 "Run input files"): uploaded by a principal, held
/// ENCRYPTED (the platform protection key) until a run request of that principal names it, then
/// bound to that one run's dispatch command — the runner fetches it with the run's resolution token
/// while the run is live and places it into the container before launch. Content never rides the
/// bus, a view, an input or a log. Deleted when its run ended (after a short grace in which a
/// failover re-dispatch takes it over), or unused at its expiry.
/// </summary>
public sealed class RunInputFileService(
    IDataAccess<CoreRunInputFileRecord> files,
    IDataAccess<CoreRunRecord> runs,
    ISettingsProtector protector,
    RunInputFileStorage storage,
    IOptions<CoreApiSettings> settings,
    TimeProvider clock,
    ILogger<RunInputFileService> logger) : IHostedService, IDisposable
{
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    /// <summary>How long a finished run's files outlive it — the window a failover re-dispatch takes them over in.</summary>
    internal static readonly TimeSpan EndedGrace = TimeSpan.FromMinutes(2);

    private CancellationTokenSource? _lifetime;
    private Task? _sweeper;

    /// <summary>Stores <paramref name="content"/> for <paramref name="owner"/>; throws past the size limit.</summary>
    public async Task<RunInputFile> UploadAsync(Guid owner, Stream content, CancellationToken ct)
    {
        var limit = settings.Value.MaxRunInputFileBytes;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit)
                throw new RunInputFileTooLargeException(limit);
            buffer.Write(chunk, 0, read);
        }
        var plain = buffer.GetBuffer().AsMemory(0, (int)buffer.Length);
        var sha = Convert.ToHexStringLower(SHA256.HashData(plain.Span));
        var id = Guid.NewGuid();
        await WriteAsync(id, protector.ProtectBytes(plain.Span), ct);

        var now = clock.GetUtcNow();
        var record = new CoreRunInputFileRecord
        {
            Id = id,
            OwnerPrincipalId = owner,
            Length = plain.Length,
            Sha256 = sha,
            CreatedUtc = now,
            ExpiresUtc = now.AddMinutes(settings.Value.RunInputFileTtlMinutes),
        };
        await files.SaveAsync(record, ct);
        return new RunInputFile(id, record.Length, sha, record.ExpiresUtc);
    }

    /// <summary>
    /// Binds a run's input files to its dispatch command, checked against what the workflow
    /// declares: every named file declared, every required one present, each upload the caller's
    /// own, unexpired and unbound. All or nothing; returns the names the command carries.
    /// </summary>
    public async Task<IReadOnlyList<string>> BindToRunAsync(
        Guid commandId, string workflowType, IReadOnlyDictionary<string, Guid>? requested,
        IReadOnlyList<WorkflowInputFileDto> declared, Guid? caller, CancellationToken ct)
    {
        requested ??= new Dictionary<string, Guid>();
        var undeclared = requested.Keys.Where(name => declared.All(d => d.Name != name)).ToList();
        if (undeclared.Count > 0)
            throw new InvalidOperationException(
                $"'{workflowType}' declares no input file named {string.Join(", ", undeclared.Select(n => $"'{n}'"))}");
        var missing = declared.Where(d => d.Required && !requested.ContainsKey(d.Name)).Select(d => d.Name).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"'{workflowType}' needs the input file(s) {string.Join(", ", missing.Select(n => $"'{n}'"))}");
        if (requested.Count == 0)
            return [];
        if (requested.Values.Distinct().Count() != requested.Count)
            throw new InvalidOperationException("one upload is one input file — upload it again for another name");

        var claimed = new List<CoreRunInputFileRecord>();
        try
        {
            foreach (var (name, id) in requested)
            {
                var record = await files.ReadAsync(id, ct);
                // Someone else's upload is indistinguishable from a missing one.
                if (record is null || caller is null || record.OwnerPrincipalId != caller)
                    throw new KeyNotFoundException($"input file '{id:D}' does not exist");
                if (record.BoundCommandId is not null)
                    throw new InvalidOperationException(
                        $"input file '{id:D}' already belongs to a run — upload it again for this one");
                if (record.ExpiresUtc <= clock.GetUtcNow())
                    throw new InvalidOperationException($"input file '{id:D}' expired unused — upload it again");
                var bound = record with { BoundCommandId = commandId, Name = name, BoundUtc = clock.GetUtcNow() };
                if (!await files.TrySaveAsync(bound, record.Version, ct))
                    throw new InvalidOperationException($"input file '{id:D}' was just taken by another run");
                claimed.Add(bound);
            }
        }
        catch
        {
            foreach (var bound in claimed)
                await ReleaseAsync(bound.Id, commandId);
            throw;
        }
        return [.. requested.Keys];
    }

    /// <summary>The decrypted content of the input file <paramref name="name"/> of a run; null when it has none.</summary>
    public async Task<(CoreRunInputFileRecord Record, byte[] Content)?> OpenAsync(
        Guid commandId, string name, CancellationToken ct)
    {
        var record = (await files.ReadAsync(ct)).FirstOrDefault(f => f.BoundCommandId == commandId && f.Name == name);
        if (record is null || !File.Exists(PathOf(record.Id)))
            return null;
        return (record, protector.UnprotectBytes(await File.ReadAllBytesAsync(PathOf(record.Id), ct)));
    }

    /// <summary>True when the run of <paramref name="commandId"/> was dispatched with input files.</summary>
    public async Task<bool> HasFilesAsync(Guid commandId, CancellationToken ct)
        => (await files.ReadAsync(ct)).Any(f => f.BoundCommandId == commandId);

    /// <summary>A failover re-dispatch continues the same logical run: its files move to the new command.</summary>
    public async Task<IReadOnlyList<string>> HandOverAsync(Guid fromCommandId, Guid toCommandId, CancellationToken ct)
    {
        var moved = new List<string>();
        foreach (var record in (await files.ReadAsync(ct)).Where(f => f.BoundCommandId == fromCommandId).ToList())
            if (await files.TrySaveAsync(
                    record with { BoundCommandId = toCommandId, BoundUtc = clock.GetUtcNow() }, record.Version, ct))
                moved.Add(record.Name!);
        return moved;
    }

    /// <summary>
    /// Deletes what no run may receive any more: unbound uploads past their expiry, files of runs
    /// that ended more than <see cref="EndedGrace"/> ago, and files bound to a dispatch that never
    /// recorded a run (or whose run was cleared) once they outlived the upload expiry. Public for tests.
    /// </summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct = default)
    {
        var all = (await files.ReadAsync(ct)).ToList();
        if (all.Count == 0)
            return 0;
        var now = clock.GetUtcNow();
        var ttl = TimeSpan.FromMinutes(settings.Value.RunInputFileTtlMinutes);
        var runRecords = (await runs.ReadAsync(ct))
            .Select(r => new { r.Id, r.CommandId, r.State, r.UpdatedUtc, r.CompletedUtc })
            .ToList();
        var removed = 0;
        foreach (var file in all)
        {
            bool done;
            if (file.BoundCommandId is not { } commandId)
                done = file.ExpiresUtc <= now;
            else
            {
                var run = runRecords.FirstOrDefault(r => r.Id == commandId)
                          ?? runRecords.FirstOrDefault(r => r.CommandId == commandId);
                done = run is null
                    ? (file.BoundUtc ?? file.CreatedUtc) + ttl <= now
                    : CoreRunStates.IsTerminal(run.State) && (run.CompletedUtc ?? run.UpdatedUtc) + EndedGrace <= now;
            }
            if (done && await files.RemoveAsync(file.Id, ct))
            {
                File.Delete(PathOf(file.Id));
                removed++;
            }
        }
        if (removed > 0)
            logger.LogInformation("Deleted {Count} run input file(s) no run may receive any more.", removed);
        return removed;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime = new CancellationTokenSource();
        _sweeper = SweepLoopAsync(_lifetime.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Idempotent against a host that disposes before (or while) it stops.
        if (Volatile.Read(ref _lifetime) is { } lifetime)
        {
            try
            {
                await lifetime.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // Disposed meanwhile — Dispose cancelled it.
            }
        }
        if (_sweeper is not null)
            await _sweeper.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ContinueWith(_ => { });
    }

    public void Dispose()
    {
        // Cancels too: a host disposed without a stop must not leave the sweep running.
        if (Interlocked.Exchange(ref _lifetime, null) is { } lifetime)
        {
            lifetime.Cancel();
            lifetime.Dispose();
        }
    }

    private async Task SweepLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(SweepInterval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await SweepOnceAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Run input file sweep failed — retrying on the next interval.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Service stop.
        }
    }

    private async Task ReleaseAsync(Guid id, Guid commandId)
    {
        try
        {
            if (await files.ReadAsync(id, CancellationToken.None) is { } current && current.BoundCommandId == commandId)
                await files.TrySaveAsync(
                    current with { BoundCommandId = null, Name = null, BoundUtc = null }, current.Version,
                    CancellationToken.None);
        }
        catch (Exception ex)
        {
            // It stays bound to a dispatch that never ran — the sweep removes it after the expiry.
            logger.LogWarning(ex, "Could not release run input file {FileId} after a failed dispatch.", id);
        }
    }

    private string PathOf(Guid id) => Path.Combine(storage.Directory, $"{id:N}.bin");

    private async Task WriteAsync(Guid id, byte[] protectedContent, CancellationToken ct)
    {
        Directory.CreateDirectory(storage.Directory);
        var path = PathOf(id);
        var temp = path + ".partial";
        await File.WriteAllBytesAsync(temp, protectedContent, ct);
        File.Move(temp, path, overwrite: true);
    }
}
