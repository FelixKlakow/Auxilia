using System.Globalization;
using System.Security.Cryptography;
using Auxilia.Core.Api.Data;
using Auxilia.Core.Contracts;
using Auxilia.Messaging;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Services;

/// <summary>Where run checkpoints are kept — encrypted, beside the artifact payloads every Core node shares.</summary>
public sealed record RunCheckpointStorage(string Directory);

/// <summary>A checkpoint larger than <see cref="CoreApiSettings.MaxCheckpointBytes"/>.</summary>
public sealed class RunCheckpointTooLargeException(long limit)
    : Exception($"the checkpoint exceeds the limit of {limit} bytes");

/// <summary>A checkpoint lifetime the platform settings do not allow (a 400 at the run request).</summary>
public sealed class CheckpointLifetimeRefusedException(string message) : InvalidOperationException(message);

/// <summary>
/// How long a run checkpoint is kept after its latest save. The most specific level wins — the
/// run request over the workflow's declaration over the general lifetime — as far as
/// <see cref="PlatformSettingKeys.CheckpointLifetimeOverrides"/> lets it, and the cap
/// (<see cref="PlatformSettingKeys.CheckpointLifetimeMaxHours"/>, else
/// <see cref="CoreApiSettings.CheckpointLifetimeMaxHours"/>) bounds them all: a run request above
/// it (or one the platform does not let runs make) is refused, a workflow's declaration or the
/// general lifetime above it is cut to it. The settings in force at each save decide.
/// </summary>
public sealed class CheckpointLifetimePolicy(PlatformSettingsService platformSettings, IOptions<CoreApiSettings> settings)
{
    /// <summary>The longest lifetime any level may name (a hundred years) — beyond it is a typo.</summary>
    public const double MaxHours = 876_000;

    /// <summary>The rules in force: the general lifetime, who may override it, and the cap (null = none).</summary>
    public sealed record Rules(TimeSpan General, string Overrides, TimeSpan? Max);

    public async Task<Rules> ReadAsync(CancellationToken ct)
    {
        var general = Hours(await platformSettings.GetAsync(PlatformSettingKeys.CheckpointLifetimeHours, ct))
                      ?? Math.Max(1, settings.Value.CheckpointLifetimeHours);
        var overrides = (await platformSettings.GetAsync(PlatformSettingKeys.CheckpointLifetimeOverrides, ct))
                        ?.ToLowerInvariant() switch
        {
            CheckpointLifetimeOverrideModes.Workflow => CheckpointLifetimeOverrideModes.Workflow,
            CheckpointLifetimeOverrideModes.None => CheckpointLifetimeOverrideModes.None,
            _ => CheckpointLifetimeOverrideModes.Run,
        };
        // The deployment's cap applies until the platform setting names another; 0 there means none.
        var max = Hours(await platformSettings.GetAsync(PlatformSettingKeys.CheckpointLifetimeMaxHours, ct))
                  ?? (settings.Value.CheckpointLifetimeMaxHours is > 0 and var configured && IsValidHours(configured)
                      ? configured
                      : null);
        return new Rules(TimeSpan.FromHours(general), overrides, max is { } cap ? TimeSpan.FromHours(cap) : null);
    }

    /// <summary>Refuses a run request's lifetime the rules do not allow; null asks for none.</summary>
    public async Task ValidateRunRequestAsync(double? hours, CancellationToken ct)
    {
        if (hours is not { } requested)
            return;
        if (!IsValidHours(requested))
            throw new CheckpointLifetimeRefusedException(
                $"a checkpoint lifetime is a positive number of hours, at most {MaxHours}");
        var rules = await ReadAsync(ct);
        if (rules.Overrides != CheckpointLifetimeOverrideModes.Run)
            throw new CheckpointLifetimeRefusedException(
                $"this platform does not let a run set its checkpoint lifetime ({PlatformSettingKeys.CheckpointLifetimeOverrides} is '{rules.Overrides}')");
        if (rules.Max is { } max && TimeSpan.FromHours(requested) > max)
            throw new CheckpointLifetimeRefusedException(
                $"this platform keeps checkpoints for at most {max.TotalHours.ToString(CultureInfo.InvariantCulture)} hours");
    }

    /// <summary>The lifetime of a checkpoint saved now, from what the run requested and the workflow declared.</summary>
    public async Task<TimeSpan> ResolveAsync(double? runHours, double? workflowHours, CancellationToken ct)
    {
        var rules = await ReadAsync(ct);
        var chosen = rules.General;
        if (rules.Overrides == CheckpointLifetimeOverrideModes.Run && runHours is { } run && IsValidHours(run))
            chosen = TimeSpan.FromHours(run);
        else if (rules.Overrides != CheckpointLifetimeOverrideModes.None && workflowHours is { } declared
                 && IsValidHours(declared))
            chosen = TimeSpan.FromHours(declared);
        return rules.Max is { } cap && chosen > cap ? cap : chosen;
    }

    public static bool IsValidHours(double hours) => hours > 0 && hours <= MaxHours;

    private static double? Hours(string? value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) && IsValidHours(hours)
            ? hours
            : null;
}

/// <summary>
/// Run checkpoints (ARCHITECTURE §9): what a workflow saved of itself while it ran, handed to the
/// Core by the runner. Each save becomes an artifact of kind <see cref="ArtifactKinds.Checkpoint"/>
/// — a new version of the run's checkpoint of that name that replaces the previous one only once
/// it is stored, so a crash at any point keeps the last good copy; saves that raced each other
/// converge on the newest. Encrypted at rest with the Core's protection key, kept for its lifetime
/// (<see cref="CheckpointLifetimePolicy"/>) and refused once past it, served to whoever may reach the
/// run — only as the content its digest names — and deletable by the run's principals. Every Core
/// node shares the storage; the sweep that deletes expired checkpoints runs on each one.
/// </summary>
public sealed class RunCheckpointService(
    IDataAccess<CoreArtifactRecord> artifacts,
    CheckpointLifetimePolicy lifetimes,
    ISettingsProtector protector,
    RunCheckpointStorage storage,
    IMessageBusClient bus,
    IOptions<CoreApiSettings> settings,
    TimeProvider clock,
    ILogger<RunCheckpointService> logger) : IHostedService, IDisposable
{
    /// <summary>The media type of a checkpointed folder (a .tar.gz).</summary>
    public const string ArchiveMediaType = "application/gzip";

    internal static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    /// <summary>A half-written payload is a save in progress until it is this old.</summary>
    internal static readonly TimeSpan AbandonedWriteGrace = TimeSpan.FromMinutes(10);

    // A payload is held in memory while it is encrypted or decrypted — this many at once per node.
    private readonly SemaphoreSlim _transfers = new(Math.Max(1, settings.Value.MaxConcurrentCheckpointTransfers));

    private CancellationTokenSource? _lifetime;
    private Task? _sweeper;
    private volatile bool _exchangeDeclared;

    /// <summary>
    /// Stores <paramref name="content"/> as the newest copy of <paramref name="run"/>'s checkpoint
    /// <paramref name="name"/> (declared by its workflow), then removes the copies it replaces.
    /// </summary>
    public async Task<ArtifactDto> SaveAsync(
        CoreRunRecord run, Guid runInstanceId, string name, string? mediaType, Stream content, long? contentLength,
        double? declaredLifetimeHours, CancellationToken ct)
    {
        var limit = settings.Value.MaxCheckpointBytes;
        if (contentLength > limit)
            throw new RunCheckpointTooLargeException(limit);
        await _transfers.WaitAsync(ct);
        try
        {
            using var buffer = new MemoryStream(contentLength is { } length ? (int)length : 0);
            var chunk = new byte[81920];
            int read;
            while ((read = await content.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > limit)
                    throw new RunCheckpointTooLargeException(limit);
                buffer.Write(chunk, 0, read);
            }
            var plain = buffer.GetBuffer().AsMemory(0, (int)buffer.Length);
            var command = DispatchCommandOf(run);
            // Everything that can fail before the payload is written — a whole payload is only ever
            // deleted through its record, so none may be left without one.
            var lifetime = await lifetimes.ResolveAsync(command?.CheckpointLifetimeHours, declaredLifetimeHours, ct);
            var id = Guid.NewGuid();
            var pending = await WritePendingAsync(id, protector.ProtectBytes(plain.Span), ct);

            var now = clock.GetUtcNow();
            var record = new CoreArtifactRecord
            {
                Id = id,
                ArtifactType = name,
                WorkflowType = run.WorkflowType,
                WorkItemId = command?.Context.GetValueOrDefault("WorkItemId") ?? string.Empty,
                RunInstanceId = runInstanceId,
                Version = (await CopiesAsync(runInstanceId, name, ct)).Select(c => c.Version).DefaultIfEmpty(0).Max() + 1,
                ContentHash = Convert.ToHexStringLower(SHA256.HashData(plain.Span)),
                SizeBytes = plain.Length,
                CreatedUtc = now,
                Kind = ArtifactKinds.Checkpoint,
                ExpiresUtc = now + lifetime,
                // Truthful: a Core without a protection key stores everything in the clear (dev only).
                Encrypted = protector is not NullSettingsProtector,
                MediaType = mediaType == ArchiveMediaType ? ArchiveMediaType : "application/octet-stream",
            };
            try
            {
                await artifacts.SaveAsync(record, ct);
                // The payload becomes readable only once its record exists: a crash before this
                // leaves a half-write the sweep removes, never a payload nothing points at.
                File.Move(pending, PathOf(id), overwrite: true);
            }
            catch
            {
                TryDelete(pending);
                await artifacts.RemoveAsync(id, CancellationToken.None);
                throw;
            }

            // Stored: what follows must not be lost to a caller that stops waiting now. Saves of this
            // name that raced this one (a save the workflow gave up on, several Core nodes) converge
            // on ONE copy — the highest version, the latest stored among equals; this one goes too
            // when a newer one landed meanwhile.
            var copies = await CopiesAsync(runInstanceId, name, CancellationToken.None);
            var newest = copies.OrderByDescending(c => c.Version).ThenByDescending(c => c.CreatedUtc)
                .ThenByDescending(c => c.Id).First();
            foreach (var replaced in copies.Where(c => c.Id != newest.Id))
                await TryRemoveAsync(replaced);
            if (newest.Id == record.Id)
                await PublishAsync(record, CancellationToken.None);
            return record.ToDto();
        }
        finally
        {
            _transfers.Release();
        }
    }

    /// <summary>
    /// The content of a checkpoint as it was saved — null when it is past its lifetime (or the
    /// current cap), its payload is gone, or the payload is not the content its record names
    /// (undecryptable, or not matching its digest).
    /// </summary>
    public async Task<byte[]?> ReadContentAsync(CoreArtifactRecord checkpoint, CancellationToken ct)
    {
        if (!await IsLiveAsync(checkpoint, ct))
            return null;
        await _transfers.WaitAsync(ct);
        try
        {
            byte[] stored;
            try
            {
                stored = await File.ReadAllBytesAsync(PathOf(checkpoint.Id), ct);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null; // deleted or swept meanwhile
            }
            byte[] plain;
            try
            {
                plain = checkpoint.Encrypted ? protector.UnprotectBytes(stored) : stored;
            }
            catch (CryptographicException ex)
            {
                logger.LogError(ex, "Checkpoint {CheckpointId} cannot be decrypted — refusing it.", checkpoint.Id);
                return null;
            }
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(plain)), checkpoint.ContentHash, StringComparison.Ordinal))
            {
                logger.LogError("Checkpoint {CheckpointId} is not the content its record names — refusing it.", checkpoint.Id);
                return null;
            }
            return plain;
        }
        finally
        {
            _transfers.Release();
        }
    }

    /// <summary>Deletes a checkpoint: its payload, then its record.</summary>
    public async Task<bool> DeleteAsync(CoreArtifactRecord checkpoint, CancellationToken ct)
        => checkpoint.Kind == ArtifactKinds.Checkpoint && await RemoveAsync(checkpoint, ct);

    /// <summary>
    /// Deletes checkpoints past their lifetime — or past the platform's current cap, which applies to
    /// copies saved before it was lowered — and half-written payloads of saves that never finished.
    /// A whole payload is only ever deleted through its record. Public for tests.
    /// </summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var cap = (await lifetimes.ReadAsync(ct)).Max;
        var checkpoints = (await artifacts.ReadAsync(ct)).Where(a => a.Kind == ArtifactKinds.Checkpoint).ToList();
        var removed = 0;
        foreach (var checkpoint in checkpoints.Where(c => !IsLive(c, now, cap)))
            if (await TryRemoveAsync(checkpoint))
                removed++;

        if (Directory.Exists(storage.Directory))
            foreach (var abandoned in new DirectoryInfo(storage.Directory).EnumerateFiles("*.partial"))
                if (abandoned.LastWriteTimeUtc + AbandonedWriteGrace <= now.UtcDateTime)
                    TryDelete(abandoned.FullName);
        if (removed > 0)
            logger.LogInformation("Deleted {Count} run checkpoint(s) past their lifetime.", removed);
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
                    logger.LogWarning(ex, "Run checkpoint sweep failed — retrying on the next interval.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Service stop.
        }
    }

    private async Task<List<CoreArtifactRecord>> CopiesAsync(Guid runInstanceId, string name, CancellationToken ct)
        => (await artifacts.ReadAsync(ct))
            .Where(a => a.Kind == ArtifactKinds.Checkpoint && a.RunInstanceId == runInstanceId && a.ArtifactType == name)
            .ToList();

    private async Task<bool> IsLiveAsync(CoreArtifactRecord checkpoint, CancellationToken ct)
        => IsLive(checkpoint, clock.GetUtcNow(), (await lifetimes.ReadAsync(ct)).Max);

    private static bool IsLive(CoreArtifactRecord checkpoint, DateTimeOffset now, TimeSpan? cap)
        => checkpoint.ExpiresUtc > now && (cap is not { } max || checkpoint.CreatedUtc + max > now);

    /// <summary>
    /// The payload first: a record left behind (a crash right here, a store that refuses) is visible
    /// and expires — a payload left behind would be invisible for good.
    /// </summary>
    private async Task<bool> RemoveAsync(CoreArtifactRecord checkpoint, CancellationToken ct)
    {
        File.Delete(PathOf(checkpoint.Id));
        return await artifacts.RemoveAsync(checkpoint.Id, ct);
    }

    private async Task<bool> TryRemoveAsync(CoreArtifactRecord checkpoint)
    {
        try
        {
            return await RemoveAsync(checkpoint, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete checkpoint {CheckpointId} — the sweep retries.", checkpoint.Id);
            return false;
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete {Path} — the sweep retries.", path);
        }
    }

    private async Task PublishAsync(CoreArtifactRecord record, CancellationToken ct)
    {
        try
        {
            if (!_exchangeDeclared)
            {
                await bus.DeclareTopicExchangeAsync(ArtifactPersistedEvent.ExchangeName, ct);
                _exchangeDeclared = true;
            }
            await bus.PublishToTopicExchangeAsync(ArtifactPersistedEvent.ExchangeName,
                ArtifactPersistedEvent.RoutingKeyFor(record.ArtifactType),
                new ArtifactPersistedEvent(
                    record.Id, record.ArtifactType, record.WorkflowType, record.WorkItemId, record.RunInstanceId,
                    record.Version, record.ContentHash, record.SizeBytes, record.CreatedUtc,
                    record.Kind, record.ExpiresUtc), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The checkpoint is stored — only the live notice is lost; a query still lists it.
            logger.LogWarning(ex, "Could not announce checkpoint {CheckpointId} on the artifact stream.", record.Id);
        }
    }

    private static RunWorkflowCommand? DispatchCommandOf(CoreRunRecord run)
    {
        try
        {
            return run.DispatchCommandJson is { Length: > 0 } json
                ? System.Text.Json.JsonSerializer.Deserialize<RunWorkflowCommand>(json)
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private string PathOf(Guid id) => Path.Combine(storage.Directory, $"{id:N}.bin");

    private async Task<string> WritePendingAsync(Guid id, byte[] protectedContent, CancellationToken ct)
    {
        Directory.CreateDirectory(storage.Directory);
        var pending = PathOf(id) + ".partial";
        await File.WriteAllBytesAsync(pending, protectedContent, ct);
        return pending;
    }
}
