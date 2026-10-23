using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace TrayTrigger.Services;

/// <summary>
/// <see cref="ICursorBackend"/> over user32: ClipCursor to confine the mouse, a foreground-window
/// event hook (with a slow timer as a safety net) to know when the window in front changes, and the
/// system's display and session events. Watching must be switched on from a thread with a message
/// loop, which TrayTrigger's own window thread is.
/// </summary>
internal sealed class Win32CursorBackend : ICursorBackend
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint TH32CS_SNAPPROCESS = 0x2;
    private static readonly TimeSpan SafetyNetInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TreeCacheLife = TimeSpan.FromSeconds(10);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public uint Size; public Rect Monitor; public Rect Work; public uint Flags; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClipCursor(ref Rect rect);
    [DllImport("user32.dll", EntryPoint = "ClipCursor")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClipCursorFree(IntPtr none);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClipCursor(out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    /// <summary>How many monitors are attached to the desktop.</summary>
    public static int MonitorCount() => GetSystemMetrics(80);

    public event Action? Changed;

    private readonly WinEventProc _callback;
    private readonly object _watchLock = new();
    private bool _wanted;
    private IntPtr _hook;
    private Timer? _safetyNet;
    private readonly Dictionary<(int Ancestor, int Pid), (bool Result, DateTime At)> _treeCache = new();

    public Win32CursorBackend()
    {
        _callback = (_, _, _, _, _, _, _) => Changed?.Invoke();
    }

    public void Watch(bool on)
    {
        lock (_watchLock)
        {
            if (_wanted == on) return;
            _wanted = on;
            if (on)
            {
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnSystemChange;
                Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
                _safetyNet = new Timer(_ => Changed?.Invoke(), null, SafetyNetInterval, SafetyNetInterval);
            }
            else
            {
                _safetyNet?.Dispose();
                _safetyNet = null;
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnSystemChange;
                Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
                lock (_treeCache) _treeCache.Clear();
            }
        }
        // The hook belongs to the window thread. It is set and removed there, one after the other, each time
        // as the latest Watch asked for, so a quick on-then-off can't leave one behind; and this call never waits.
        PostToUiThread(ApplyHook);
    }

    private void ApplyHook()
    {
        bool wanted;
        lock (_watchLock) wanted = _wanted;
        if (wanted && _hook == IntPtr.Zero)
        {
            _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT);
        }
        else if (!wanted && _hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }

    public void Forget(int processId)
    {
        lock (_treeCache)
        {
            foreach (var key in _treeCache.Keys.Where(k => k.Pid == processId || k.Ancestor == processId).ToList()) _treeCache.Remove(key);
        }
    }
    private void OnSystemChange(object? sender, EventArgs e) => Changed?.Invoke();
    private void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e) => Changed?.Invoke();

    /// <summary>The hook belongs to the thread that sets it and needs that thread's message loop; without one (a test), it runs here.</summary>
    private static void PostToUiThread(Action action) => WindowThread.Post(action);

    public ForegroundWindow? Foreground()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        if (GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid == 0) return null;
        return new ForegroundWindow((int)pid, hwnd);
    }

    public bool IsSameOrDescendant(int ancestor, int pid)
    {
        if (ancestor == pid) return true;
        var key = (ancestor, pid);
        lock (_treeCache)
        {
            if (_treeCache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < TreeCacheLife) return cached.Result;
        }
        bool result = ParentChainContains(pid, ancestor);
        lock (_treeCache) _treeCache[key] = (result, DateTime.UtcNow);
        return result;
    }

    /// <summary>Walks up from <paramref name="pid"/> through the processes that started it, looking for <paramref name="ancestor"/>.</summary>
    private static bool ParentChainContains(int pid, int ancestor)
    {
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return false;
        try
        {
            var parents = new Dictionary<uint, uint>();
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (bool more = Process32First(snapshot, ref entry); more; more = Process32Next(snapshot, ref entry))
            {
                parents[entry.ProcessId] = entry.ParentProcessId;
            }
            uint current = (uint)pid;
            for (int depth = 0; depth < 16 && parents.TryGetValue(current, out uint parent) && parent != 0; depth++)
            {
                if (parent == (uint)ancestor) return true;
                current = parent;
            }
            return false;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    public ScreenRect? MonitorOf(IntPtr window)
    {
        using var physical = new PhysicalPixels();
        IntPtr monitor = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return null;
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return null;
        return new ScreenRect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom);
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    public IReadOnlyList<ScreenRect> AllMonitors()
    {
        using var physical = new PhysicalPixels();
        var list = new List<ScreenRect>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data) =>
        {
            list.Add(new ScreenRect(rect.Left, rect.Top, rect.Right, rect.Bottom));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public ScreenRect? CurrentClip()
    {
        using var physical = new PhysicalPixels();
        return GetClipCursor(out Rect r) ? new ScreenRect(r.Left, r.Top, r.Right, r.Bottom) : null;
    }

    public bool Clip(ScreenRect? rect)
    {
        using var physical = new PhysicalPixels();
        if (rect is not { } r) return ClipCursorFree(IntPtr.Zero);
        var native = new Rect { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };
        return ClipCursor(ref native);
    }
}
