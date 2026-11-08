using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Auxilia.Core.Runner.Workflows;

/// <summary>
/// Reads what a workload left in its run's output directory WITHOUT following a link out of it.
/// The workload owns that directory and could plant a symlink to anything the runner can read —
/// its own environment under <c>/proc</c>, another run's workspace — so on Linux every file and
/// directory is opened <c>O_NOFOLLOW | O_NONBLOCK</c> and its real path checked through
/// <c>/proc/self/fd</c> before it is read, written or descended into (a link swapped in after any
/// earlier check is caught there), and only seekable regular files are read (a FIFO would block the
/// runner forever). A file yields exactly the bytes it had when it was opened — the workload may
/// keep writing it. Elsewhere (a dev runner) every path component is checked for links instead —
/// best effort, not race-free.
/// </summary>
internal static class SafeRunOutput
{
    /// <summary>What is at a path inside the output directory, without following links.</summary>
    public enum EntryKind { Missing, File, Directory, Refused }

    /// <summary>Content larger than the run checkpoint limit.</summary>
    public sealed class TooLargeException(long limit)
        : Exception($"the checkpoint exceeds the limit of {limit} bytes");

    private const int MaxArchiveEntries = 100_000;

    /// <summary>What every archive entry counts against the size limit beyond its content (its headers).</summary>
    internal const long EntryAllowance = 2048;

    public static EntryKind KindOf(string root, string relativePath)
    {
        if (Scope.For(root) is not { } scope || scope.Resolve(relativePath) is not { } full || !scope.ComponentsAreReal(full))
            return EntryKind.Refused;
        if (Directory.Exists(full))
            return EntryKind.Directory;
        return File.Exists(full) ? EntryKind.File : EntryKind.Missing;
    }

    /// <summary>
    /// The regular file at root/relativePath as it is now — exactly the bytes it held when opened;
    /// null when absent, special, or reached through a link.
    /// </summary>
    public static Stream? OpenFile(string root, string relativePath)
        => Scope.For(root) is { } scope && scope.Resolve(relativePath) is { } full && scope.ComponentsAreReal(full)
           && scope.OpenNoFollow(full) is { } file
            ? new Snapshot(file, file.Length)
            : null;

    /// <summary>
    /// Archives the directory root/relativePath as .tar.gz into <paramref name="destination"/>: its
    /// regular files and directories, named relative to it; links and special files are left out.
    /// Throws past <paramref name="maxBytes"/> (content plus <see cref="EntryAllowance"/> per entry),
    /// and when a file shrinks while it is archived.
    /// </summary>
    public static async Task ArchiveDirectoryAsync(
        string root, string relativePath, Stream destination, long maxBytes, CancellationToken ct)
    {
        if (Scope.For(root) is not { } scope || scope.Resolve(relativePath) is not { } top
            || !scope.ComponentsAreReal(top) || !scope.IsRealDirectory(top))
            throw new InvalidOperationException($"'{relativePath}' is not a directory of the run's output");

        await using var gzip = new GZipStream(destination, CompressionLevel.Optimal, leaveOpen: true);
        await using var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true);
        long total = 0;
        var entries = 0;
        var pending = new Stack<(string Full, string Name)>();
        pending.Push((top, ""));
        while (pending.Count > 0)
        {
            var (directory, prefix) = pending.Pop();
            foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();
                if (++entries > MaxArchiveEntries)
                    throw new TooLargeException(maxBytes);
                if (IsLink(info))
                    continue;
                total += EntryAllowance;
                if (total > maxBytes)
                    throw new TooLargeException(maxBytes);
                var name = prefix + info.Name;
                if (info is DirectoryInfo)
                {
                    // Written and descended only where it really is: a parent swapped for a link
                    // meanwhile lists another tree, and every entry of it fails this check.
                    if (!scope.IsRealDirectory(info.FullName))
                        continue;
                    await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, name + "/"), ct);
                    pending.Push((info.FullName, name + "/"));
                    continue;
                }
                await using var file = scope.OpenNoFollow(info.FullName);
                if (file is null)
                    continue; // a special file, or a link swapped in meanwhile
                var length = file.Length;
                total += length;
                if (total > maxBytes)
                    throw new TooLargeException(maxBytes);
                // The entry's size is the length counted here, and exactly that many bytes follow it:
                // the tar writer would otherwise copy whatever the workload appended meanwhile and
                // corrupt every entry after this one.
                await using var content = new Snapshot(file, length);
                await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = content,
                    ModificationTime = info.LastWriteTimeUtc,
                }, ct);
            }
        }
    }

    private static bool IsLink(FileSystemInfo info)
        => info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint);

    private static bool IsLinkPath(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        return info.Exists && IsLink(info);
    }

    /// <summary>
    /// The first <c>length</c> bytes of a file the workload may still be writing: reading ends there
    /// even if the file grew since it was opened, and a file that shrank fails the read instead of
    /// yielding fewer bytes than were counted. Reports its length (the tar writer sizes an entry by it).
    /// </summary>
    internal sealed class Snapshot(Stream inner, long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _position;
            set
            {
                if (value != _position)
                    throw new NotSupportedException("a snapshot is read once, front to back");
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var wanted = Wanted(buffer.Length);
            if (wanted == 0)
                return 0;
            return Advance(inner.Read(buffer[..wanted]));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var wanted = Wanted(buffer.Length);
            if (wanted == 0)
                return 0;
            return Advance(await inner.ReadAsync(buffer[..wanted], cancellationToken));
        }

        public override long Seek(long offset, SeekOrigin origin)
            => (origin, offset) switch
            {
                (SeekOrigin.Current, 0) => _position,
                (SeekOrigin.Begin, var to) when to == _position => _position,
                _ => throw new NotSupportedException("a snapshot is read once, front to back"),
            };

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }

        private int Wanted(int room) => (int)Math.Min(room, length - _position);

        private int Advance(int read)
        {
            if (read == 0)
                throw new IOException("the file shrank while it was saved");
            _position += read;
            return read;
        }
    }

    /// <summary>
    /// The output directory a read is confined to: its configured path, and on Linux its REAL path
    /// (the runner's own configuration may legitimately sit below a linked directory — only what
    /// lies BELOW the root is the workload's).
    /// </summary>
    private sealed class Scope
    {
        private readonly string _root;
        private readonly string? _realRoot;

        private Scope(string root, string? realRoot)
        {
            _root = root;
            _realRoot = realRoot;
        }

        public static Scope? For(string root)
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (!Directory.Exists(full) || IsLinkPath(full))
                return null;
            if (!OperatingSystem.IsLinux())
                return new Scope(full, null);
            // On Linux only the race-free path — an architecture without known flag values reads nothing.
            if (LinuxFlags(noFollow: false) is not { } flags)
                return null;
            var fd = LinuxOpen(full, flags);
            if (fd < 0)
                return null;
            using var handle = new SafeFileHandle(fd, ownsHandle: true);
            return RealPathOf(fd) is { } real ? new Scope(full, real) : null;
        }

        public string? Resolve(string relativePath)
        {
            var full = Path.GetFullPath(Path.Combine(_root, relativePath));
            return full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
        }

        /// <summary>No existing component below the root, down to <paramref name="full"/>, is a link.</summary>
        public bool ComponentsAreReal(string full)
        {
            var current = _root;
            foreach (var segment in Path.GetRelativePath(_root, full).Split(Path.DirectorySeparatorChar))
            {
                current = Path.Combine(current, segment);
                if (IsLinkPath(current))
                    return false;
            }
            return true;
        }

        /// <summary>Whether <paramref name="full"/> is a directory where its path says — below the root, reached through no link.</summary>
        public bool IsRealDirectory(string full)
        {
            if (_realRoot is null || LinuxFlags(noFollow: true) is not { } flags || LinuxOpenFlags() is not { } values)
                return Directory.Exists(full) && !IsLinkPath(full);
            var directory = values.Directory;
            var fd = LinuxOpen(full, flags | directory);
            if (fd < 0)
                return false;
            using var handle = new SafeFileHandle(fd, ownsHandle: true);
            return IsWhereExpected(fd, full);
        }

        public FileStream? OpenNoFollow(string full)
        {
            if (_realRoot is null || LinuxFlags(noFollow: true) is not { } flags)
            {
                if (IsLinkPath(full) || !File.Exists(full))
                    return null;
                var portable = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (portable.CanSeek)
                    return portable;
                portable.Dispose();
                return null;
            }

            var fd = LinuxOpen(full, flags);
            if (fd < 0)
                return null; // absent, or a link as the last component (ELOOP)
            var handle = new SafeFileHandle(fd, ownsHandle: true);
            try
            {
                if (!IsWhereExpected(fd, full))
                {
                    handle.Dispose();
                    return null;
                }
                var stream = new FileStream(handle, FileAccess.Read);
                if (stream.CanSeek)
                    return stream;
                stream.Dispose();
                return null;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        /// <summary>The kernel's view of what was opened: a link anywhere below the root shows up here.</summary>
        private bool IsWhereExpected(int fd, string full)
            => string.Equals(RealPathOf(fd), _realRoot + full[_root.Length..], StringComparison.Ordinal);

        private static string? RealPathOf(int fd)
            => File.ResolveLinkTarget($"/proc/self/fd/{fd}", returnFinalTarget: false)?.FullName;
    }

    /// <summary>O_RDONLY | O_NONBLOCK | O_CLOEXEC (| O_NOFOLLOW); null where the values are unknown.</summary>
    private static int? LinuxFlags(bool noFollow)
        => LinuxOpenFlags() is { } values ? (noFollow ? values.NoFollow : 0) | ONonBlock | OCloExec : null;

    // The same on every architecture below.
    private const int ONonBlock = 0x800;
    private const int OCloExec = 0x80000;

    /// <summary>
    /// O_NOFOLLOW and O_DIRECTORY of the architectures .NET runs Linux on: most use the generic
    /// values, arm and powerpc their own; null for any other.
    /// </summary>
    private static (int NoFollow, int Directory)? LinuxOpenFlags() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 or Architecture.X86 or Architecture.S390x or Architecture.LoongArch64 or Architecture.RiscV64
            => (0x20000, 0x10000),
        Architecture.Arm64 or Architecture.Arm or Architecture.Armv6 or Architecture.Ppc64le
            => (0x8000, 0x4000),
        _ => null,
    };

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int LinuxOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
}
