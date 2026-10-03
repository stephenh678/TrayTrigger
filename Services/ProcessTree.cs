using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TrayTrigger.Services;

/// <summary>
/// Opening a process by id, but only while it is still the process that started at a known moment:
/// an id is given to a new program as soon as the old one exits, and CPU Cores and Suspend must never
/// act on a stranger. Shared by <see cref="CpuTopologyService"/> and <see cref="ProcessSuspender"/>.
/// </summary>
internal static partial class ProcessHandles
{
    private const uint QueryLimitedInformation = 0x1000;
    private const uint StillActive = 259;

    internal enum OpenResult { Opened, Gone, Refused }

    /// <summary>
    /// Opens <paramref name="pid"/> with <paramref name="access"/> (and limited query rights) when it
    /// started at <paramref name="startedUtc"/>. Gone: no such process any more, or the id belongs to a
    /// newer one. Refused: Windows said no - a process running as administrator, or protected - with
    /// the reason in <paramref name="error"/>. The caller disposes the handle.
    /// </summary>
    internal static OpenResult OpenSame(int pid, DateTime startedUtc, uint access, out SafeProcessHandle? handle, out string? error)
    {
        handle = null;
        error = null;
        var opened = OpenProcess(access | QueryLimitedInformation, false, (uint)pid);
        if (opened.IsInvalid)
        {
            int code = Marshal.GetLastPInvokeError();
            opened.Dispose();
            // 87 (invalid parameter) is what OpenProcess says for an id no process has any more.
            if (code == 87) return OpenResult.Gone;
            error = new Win32Exception(code).Message.TrimEnd('.');
            return OpenResult.Refused;
        }

        if (!GetProcessTimes(opened, out long created, out _, out _, out _)
            || Math.Abs((DateTime.FromFileTimeUtc(created) - startedUtc).TotalSeconds) > 1)
        {
            opened.Dispose();
            return OpenResult.Gone;
        }
        handle = opened;
        return OpenResult.Opened;
    }

    /// <summary>The process has exited, though something still holds a handle to it.</summary>
    internal static bool HasExited(SafeProcessHandle handle) => GetExitCodeProcess(handle, out uint code) && code != StillActive;

    /// <summary>The process is still running and is still the one that started at <paramref name="startedUtc"/>.</summary>
    internal static bool IsRunning(int pid, DateTime startedUtc)
    {
        if (OpenSame(pid, startedUtc, 0, out var handle, out _) != OpenResult.Opened) return false;
        using (handle) return !HasExited(handle!);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
}

/// <summary>
/// A process held open by handle, for everything a tool's close reaches: while it's held its id can't
/// be given to another program, it can be waited on, and SYNCHRONIZE with limited query rights is all
/// it takes, so one running as administrator can be held too.
/// </summary>
internal sealed partial class HeldProcess : IDisposable
{
    private const uint Synchronize = 0x00100000;
    private const uint QueryLimitedInformation = 0x1000;
    private const uint WaitObject0 = 0;

    private readonly SafeProcessHandle _handle;

    private HeldProcess(int id, string name, SafeProcessHandle handle, DateTime startedUtc)
    {
        Id = id;
        Name = name;
        _handle = handle;
        StartedUtc = startedUtc;
    }

    public int Id { get; }
    /// <summary>For the log: the tool's name for a copy of it, the program's file name for what it started.</summary>
    public string Name { get; }
    public DateTime StartedUtc { get; }

    /// <summary>Opens the process, or null when it has already gone.</summary>
    public static HeldProcess? Open(int pid, string name)
    {
        var handle = OpenProcess(Synchronize | QueryLimitedInformation, false, (uint)pid);
        if (handle.IsInvalid || !GetProcessTimes(handle, out long created, out _, out _, out _))
        {
            handle.Dispose();
            return null;
        }
        return new HeldProcess(pid, name, handle, DateTime.FromFileTimeUtc(created));
    }

    public bool HasExited => WaitForSingleObject(_handle, 0) == WaitObject0;

    public bool WaitForExit(TimeSpan timeout) =>
        WaitForSingleObject(_handle, (uint)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue)) == WaitObject0;

    public void Dispose() => _handle.Dispose();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
}

/// <summary>Which programs a process started, found through Windows' process list (Toolhelp).</summary>
internal static partial class ProcessTree
{
    /// <summary>
    /// The programs <paramref name="root"/> started as it started up - created within
    /// <paramref name="window"/> of it - and theirs in turn, held open. Afterburner's RTSS is one; a
    /// program opened from it later, like a browser for a link, isn't. A child <paramref name="keep"/>
    /// turns down is left out with everything under it: a browser the tool opened takes its own
    /// renderer processes with it.
    /// </summary>
    public static List<HeldProcess> StartupDescendants(HeldProcess root, TimeSpan window, Func<HeldProcess, bool> keep)
    {
        var found = new List<HeldProcess>();
        var everyone = Snapshot("Tools", "only the tool itself will be closed");
        DateTime latest = root.StartedUtc + window;
        var seen = new HashSet<int> { root.Id };
        var parents = new Queue<HeldProcess>();
        parents.Enqueue(root);

        while (parents.Count > 0)
        {
            var parent = parents.Dequeue();
            foreach (var (pid, entry) in everyone)
            {
                if (entry.ParentId != parent.Id || !seen.Add(pid)) continue;
                var child = HeldProcess.Open(pid, entry.Name);
                if (child == null) continue;
                if (!IsStartupChild(parent.StartedUtc, child.StartedUtc, latest) || !keep(child))
                {
                    child.Dispose();
                    continue;
                }
                found.Add(child);
                parents.Enqueue(child);
            }
        }
        return found;
    }

    /// <summary>
    /// Every process <paramref name="rootPid"/> started, whenever it started it, and theirs in turn:
    /// a game's crash handler, its renderer, the real game a launcher stub hands off to. Each is
    /// checked to be younger than its parent, so a process that took over an old parent's id isn't
    /// counted. Nothing is held open; a caller that acts on one opens it again and checks
    /// <c>StartedUtc</c>.
    /// </summary>
    public static List<(int Pid, string Name, DateTime StartedUtc)> Descendants(int rootPid, DateTime rootStartedUtc, string logCategory)
    {
        var found = new List<(int Pid, string Name, DateTime StartedUtc)>();
        var everyone = Snapshot(logCategory, "its child processes are left out");
        var seen = new HashSet<int> { rootPid };
        var parents = new Queue<(int Pid, DateTime StartedUtc)>();
        parents.Enqueue((rootPid, rootStartedUtc));

        while (parents.Count > 0)
        {
            var parent = parents.Dequeue();
            foreach (var (pid, entry) in everyone)
            {
                if (entry.ParentId != parent.Pid || !seen.Add(pid)) continue;
                using var child = HeldProcess.Open(pid, entry.Name);
                if (child == null || child.StartedUtc < parent.StartedUtc) continue;
                found.Add((pid, entry.Name, child.StartedUtc));
                parents.Enqueue((pid, child.StartedUtc));
            }
        }
        return found;
    }

    /// <summary>
    /// A child is always created after its parent, so one that names this parent but is older was the
    /// child of an earlier process that had the same id. And one created after <paramref name="latest"/>
    /// was opened from the program later, not started by it as it started up.
    /// </summary>
    internal static bool IsStartupChild(DateTime parentStartedUtc, DateTime childStartedUtc, DateTime latest) =>
        childStartedUtc >= parentStartedUtc && childStartedUtc <= latest;

    private const uint SnapProcesses = 0x00000002;
    private static readonly IntPtr InvalidHandle = new(-1);

    private unsafe struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        public fixed char ExeFile[260];
    }

    /// <summary>Every process's parent id and file name, at one moment. Empty when Windows won't give the list.</summary>
    private static unsafe Dictionary<int, (int ParentId, string Name)> Snapshot(string logCategory, string consequence)
    {
        var result = new Dictionary<int, (int ParentId, string Name)>();
        IntPtr snapshot = CreateToolhelp32Snapshot(SnapProcesses, 0);
        if (snapshot == InvalidHandle || snapshot == IntPtr.Zero)
        {
            LoggingService.Verbose(logCategory, $"Couldn't list running processes (error {Marshal.GetLastPInvokeError()}); {consequence}.");
            return result;
        }
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)sizeof(ProcessEntry32) };
            for (bool more = Process32First(snapshot, ref entry); more; more = Process32Next(snapshot, ref entry))
            {
                result[(int)entry.ProcessId] = ((int)entry.ParentProcessId, new string(entry.ExeFile));
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return result;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
