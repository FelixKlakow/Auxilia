using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Auxilia.Workflows.Sandbox;

/// <summary>
/// Confines everything inside a workflow container that runs agent- or repository-controlled
/// code (coding CLIs, their terminal sessions, workspace setup scripts, workspace git): such
/// processes run as the unprivileged <see cref="UserName"/>, never as the SDK's root identity,
/// and never inherit a platform secret. The SDK process itself (bus identity, instance token,
/// push credentials, JIT slot credentials) stays root-only: a different uid cannot read its
/// memory or <c>/proc/1/environ</c>, signal it, or ptrace it.
/// </summary>
/// <remarks>
/// Active only where it means something — Linux, running as root (the platform's container);
/// a dev/test process on Windows or as a normal user runs children unchanged except for the
/// environment scrub. The user is created on demand, so every SDK-built image gets it.
/// </remarks>
public static class AgentSandbox
{
    public const string UserName = "auxilia-agent";
    public const int Uid = 10001;
    public const int Gid = 10001;
    public const string Home = "/home/auxilia-agent";

    private static readonly object Gate = new();
    private static bool? _active;
    private static int _liveAgentProcesses;

    /// <summary>Whether children are actually dropped to <see cref="UserName"/>.</summary>
    public static bool IsActive
    {
        get
        {
            lock (Gate)
                return _active ??= ProbeActive();
        }
    }

    /// <summary>
    /// The home the agent's CLIs read their configuration from — the sandbox user's while the
    /// sandbox is active, this process's own otherwise. Providers prepare an agent's
    /// credentials and settings HERE, then hand them over with <see cref="GrantToAgent"/>.
    /// </summary>
    public static string AgentHome => IsActive
        ? Home
        : System.Environment.GetEnvironmentVariable("HOME")
          ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// Hands a path the SDK prepared for the agent (recursively) to the agent identity, so the
    /// agent's CLIs can read and update their own configuration. A no-op without the sandbox.
    /// </summary>
    public static void GrantToAgent(string path)
    {
        if (IsActive && Path.Exists(path))
            RunPrivileged("chown", "-R", $"{Uid}:{Gid}", path);
    }

    /// <summary>
    /// The variables only the SDK may hold. Scrubbed from the process environment at startup
    /// (<see cref="ScrubPlatformSecrets"/>) and from every confined child.
    /// </summary>
    public static bool IsPlatformSecret(string variable)
        => variable.StartsWith("RabbitMq__", StringComparison.OrdinalIgnoreCase)
           || string.Equals(variable, WorkflowEnvironmentVariables.InstanceToken, StringComparison.OrdinalIgnoreCase)
           || variable.StartsWith(WorkflowEnvironmentVariables.PushRemotePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Removes the platform secrets from THIS process's environment, after the SDK has read them,
    /// so no child process (whoever starts it) can inherit them.
    /// </summary>
    public static void ScrubPlatformSecrets()
    {
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
            if (entry.Key is string key && IsPlatformSecret(key))
                System.Environment.SetEnvironmentVariable(key, null);
    }

    /// <summary>
    /// Prepares the sandbox once per run: ensures the agent user exists and owns the directories
    /// agents legitimately work in (best-effort — a bind mount that refuses chown, e.g. a
    /// Windows-host share, is already world-writable). Those trees are SHARED with the SDK: this
    /// process takes the agent group as its effective group and creates files group-writable, so
    /// whatever the SDK writes there later (a workflow's exchange files, a story, a prompt) stays
    /// writable for the agent on every filesystem — setgid inheritance is not universal (host
    /// shares ignore it). Root's own access is unchanged (it bypasses permission checks), and
    /// whatever the SDK keeps private lives under 0700 <c>/root</c>; its one root-trusted runtime
    /// file set, the launcher wrappers, carries explicit root-only-writable modes.
    /// </summary>
    public static void Initialize(IEnumerable<string?> agentWritableDirectories, ILogger logger)
    {
        if (!IsActive)
            return;
        foreach (var directory in agentWritableDirectories.Append(Home))
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                continue;
            var exit = RunPrivileged("chown", "-R", $"{Uid}:{Gid}", directory);
            if (exit != 0)
                logger.LogWarning(
                    "Could not hand {Directory} to the agent user (chown exit {Exit}); relying on its existing permissions.",
                    directory, exit);
        }
        if (SetEffectiveGroup(Gid) != 0)
            logger.LogWarning("Could not take the agent group as effective group; files the SDK prepares stay root-only.");
        SetUmask(Convert.ToUInt32("002", 8));
    }

#pragma warning disable SYSLIB1054 // LibraryImport would need unsafe code for these blittable calls.
    [DllImport("libc", EntryPoint = "umask")]
    private static extern uint SetUmask(uint mask);

    [DllImport("libc", EntryPoint = "setegid")]
    private static extern int SetEffectiveGroup(uint gid);
#pragma warning restore SYSLIB1054

    /// <summary>
    /// Confines a child process: platform secrets never reach it, and where the sandbox is active
    /// it runs as <see cref="UserName"/> (via <c>setpriv</c>, with no supplementary groups) and
    /// its HOME is the agent's own.
    /// </summary>
    public static ProcessStartInfo Confine(ProcessStartInfo startInfo)
    {
        foreach (var key in startInfo.Environment.Keys.Where(IsPlatformSecret).ToList())
            startInfo.Environment.Remove(key);
        if (!IsActive)
            return startInfo;

        startInfo.Environment["HOME"] = Home;
        startInfo.Environment["USER"] = UserName;
        startInfo.Environment["LOGNAME"] = UserName;

        var program = startInfo.FileName;
        var prefix = new[] { $"--reuid={Uid}", $"--regid={Gid}", "--clear-groups", "--", program };
        startInfo.FileName = "setpriv";
        if (startInfo.ArgumentList.Count > 0 || string.IsNullOrEmpty(startInfo.Arguments))
        {
            var arguments = startInfo.ArgumentList.ToList();
            startInfo.ArgumentList.Clear();
            foreach (var argument in prefix.Concat(arguments))
                startInfo.ArgumentList.Add(argument);
        }
        else
        {
            startInfo.Arguments = string.Join(' ', prefix.Select(Quote)) + " " + startInfo.Arguments;
        }
        return startInfo;
    }

    /// <summary>
    /// For a third-party launcher that starts the agent CLI itself (no <see cref="ProcessStartInfo"/>
    /// to <see cref="Confine"/>): the path of a root-owned wrapper that execs
    /// <paramref name="program"/> as the agent user with the agent's HOME. Where the sandbox is
    /// inactive, <paramref name="program"/> unchanged.
    /// </summary>
    public static string WrapperFor(string program)
    {
        if (!IsActive)
            return program;
        const string directory = "/var/lib/auxilia-sandbox";
        var name = "exec-" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(program)))[..16].ToLowerInvariant();
        var path = Path.Combine(directory, name);
        lock (Gate)
        {
            if (!File.Exists(path))
            {
                // Root executes these before dropping privileges — never writable by the agent.
                if (OperatingSystem.IsWindows())
                    Directory.CreateDirectory(directory);
                else
                    Directory.CreateDirectory(directory,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                File.WriteAllText(path,
                    "#!/bin/sh\n"
                    + $"export HOME={Home} USER={UserName} LOGNAME={UserName}\n"
                    + $"exec setpriv --reuid={Uid} --regid={Gid} --clear-groups -- {ShellQuote(program)} \"$@\"\n");
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(path,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
        return path;
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>
    /// Marks an agent CLI as running; disposing the lease marks it ended. When the LAST live
    /// agent ends, every remaining process of the agent user is killed — a daemon the agent
    /// detached (double fork, setsid) cannot outlive its session to act later unobserved.
    /// </summary>
    public static IDisposable EnterAgentSession()
    {
        Interlocked.Increment(ref _liveAgentProcesses);
        return new Lease();
    }

    /// <summary>Whether any agent session is live right now.</summary>
    public static bool AgentSessionActive => Volatile.Read(ref _liveAgentProcesses) > 0;

    /// <summary>Kills every process of the agent user (no-op where the sandbox is inactive).</summary>
    public static void TerminateAgentProcesses()
    {
        if (!IsActive)
            return;
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var pid) || pid == System.Environment.ProcessId)
                continue;
            try
            {
                // "Uid: <real> <effective> <saved> <fs>" (tab-separated) — any agent-owned identity counts.
                var uidLine = File.ReadLines(Path.Combine(directory, "status"))
                    .FirstOrDefault(l => l.StartsWith("Uid:", StringComparison.Ordinal));
                if (uidLine is null
                    || !uidLine.Split((char[])['\t', ' '], StringSplitOptions.RemoveEmptyEntries)
                        .Skip(1).Contains(Uid.ToString()))
                    continue;
                using var process = Process.GetProcessById(pid);
                process.Kill();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or ArgumentException or InvalidOperationException
                                           or System.ComponentModel.Win32Exception)
            {
                // Exited in between, or not ours to inspect — nothing to stop.
            }
        }
    }

    private sealed class Lease : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            if (Interlocked.Decrement(ref _liveAgentProcesses) == 0)
                TerminateAgentProcesses();
        }
    }

    private static bool ProbeActive()
    {
        if (!OperatingSystem.IsLinux() || System.Environment.UserName != "root" || !File.Exists("/usr/bin/setpriv"))
            return false;
        try
        {
            EnsureUser();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Creates the agent user and group (no password, no login shell needed) when absent.</summary>
    private static void EnsureUser()
    {
        const string passwd = "/etc/passwd";
        const string group = "/etc/group";
        if (!File.ReadLines(passwd).Any(l => l.StartsWith(UserName + ":", StringComparison.Ordinal)))
            File.AppendAllText(passwd, $"{UserName}:x:{Uid}:{Gid}::{Home}:/bin/bash\n");
        if (File.Exists(group) && !File.ReadLines(group).Any(l => l.StartsWith(UserName + ":", StringComparison.Ordinal)))
            File.AppendAllText(group, $"{UserName}:x:{Gid}:\n");
        Directory.CreateDirectory(Home);
        RunPrivileged("chown", $"{Uid}:{Gid}", Home);
    }

    private static int RunPrivileged(string program, params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo(program)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo);
            if (process is null)
                return -1;
            process.WaitForExit(TimeSpan.FromMinutes(2));
            return process.HasExited ? process.ExitCode : -1;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return -1;
        }
    }

    private static string Quote(string value)
        => value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '=' or '/' or '.' or ':')
            ? value
            : "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
