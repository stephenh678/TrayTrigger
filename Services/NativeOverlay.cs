using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TrayTrigger.Services;

/// <summary>
/// A plain Win32 window laid over part of the desktop, in physical pixels: always on top, see-through to the
/// mouse, never takes the focus and has no taskbar button. Used to dim a monitor and to name monitors. Native on
/// purpose: a WPF window is resized by Windows when it moves between monitors of different scaling, and the
/// overlay must cover exactly the rectangle it was given.
///
/// <para>Everything here must run on the thread with the message loop (the window thread). A window dies with the
/// process, so a crash can't leave one behind.</para>
/// </summary>
internal static class NativeOverlay
{
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
    private const uint LWA_ALPHA = 0x2;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint WM_PAINT = 0xF, WM_ERASEBKGND = 0x14, WM_NCHITTEST = 0x84, WM_MOUSEACTIVATE = 0x21;
    private const uint DT_CENTER = 0x1, DT_VCENTER = 0x4, DT_SINGLELINE = 0x20, DT_NOPREFIX = 0x800;
    private const int HTTRANSPARENT = -1, MA_NOACTIVATE = 3, TRANSPARENT = 1, BLACK_BRUSH = 4;
    private const string ClassName = "TrayTriggerOverlay";

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public IntPtr Hdc; public int Erase; public Rect Paint; public int Restore, IncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved;
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint Style; public WndProc Proc; public int ClsExtra, WndExtra; public IntPtr Instance, Icon, Cursor, Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassW(ref WndClass cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string? title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, out PaintStruct ps);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EndPaint(IntPtr hwnd, ref PaintStruct ps);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr hdc, ref Rect rect, IntPtr brush);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawTextW(IntPtr hdc, string text, int count, ref Rect rect, uint format);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr hdc, uint color);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrecision, uint clipPrecision, uint quality, uint pitchFamily, string face);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);

    private static readonly WndProc s_proc = Proc;
    private static bool s_registered;
    private static readonly Dictionary<IntPtr, (string? Number, string? Caption)> s_text = new();

    /// <summary>Lays a window over <paramref name="area"/>. <paramref name="alpha"/> is 0 (invisible) to 255 (solid); a number and caption are drawn on it when given. Null if Windows refuses.</summary>
    public static IntPtr Create(ScreenRect area, byte alpha, string? number = null, string? caption = null)
    {
        if (!s_registered)
        {
            var cls = new WndClass { Proc = s_proc, Instance = GetModuleHandleW(null), ClassName = ClassName, Background = GetStockObject(BLACK_BRUSH) };
            if (RegisterClassW(ref cls) == 0 && Marshal.GetLastWin32Error() != 1410 /* class already exists */) return IntPtr.Zero;
            s_registered = true;
        }
        IntPtr hwnd = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE, ClassName, null, WS_POPUP,
            area.Left, area.Top, area.Right - area.Left, area.Bottom - area.Top, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        s_text[hwnd] = (number, caption);
        SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        return hwnd;
    }

    public static void Destroy(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        s_text.Remove(hwnd);
        DestroyWindow(hwnd);
    }

    private static IntPtr Proc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WM_NCHITTEST: return (IntPtr)HTTRANSPARENT;
            case WM_MOUSEACTIVATE: return (IntPtr)MA_NOACTIVATE;
            case WM_ERASEBKGND: return (IntPtr)1;
            case WM_PAINT:
            {
                BeginPaint(hwnd, out var ps);
                GetClientRect(hwnd, out var rect);
                FillRect(ps.Hdc, ref rect, GetStockObject(BLACK_BRUSH));
                if (s_text.TryGetValue(hwnd, out var text) && !string.IsNullOrEmpty(text.Number))
                {
                    int height = rect.Bottom - rect.Top;
                    SetBkMode(ps.Hdc, TRANSPARENT);
                    SetTextColor(ps.Hdc, 0x00FFFFFF);
                    DrawCentered(ps.Hdc, text.Number!, new Rect { Left = rect.Left, Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom - height / 4 }, height * 55 / 100, 700);
                    if (!string.IsNullOrEmpty(text.Caption))
                    {
                        DrawCentered(ps.Hdc, text.Caption!, new Rect { Left = rect.Left, Top = rect.Bottom - height / 4, Right = rect.Right, Bottom = rect.Bottom }, height / 12, 400);
                    }
                }
                EndPaint(hwnd, ref ps);
                return IntPtr.Zero;
            }
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private static void DrawCentered(IntPtr hdc, string text, Rect area, int fontHeight, int weight)
    {
        IntPtr font = CreateFontW(-Math.Max(fontHeight, 10), 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5 /* clear type */, 0, "Segoe UI");
        IntPtr previous = SelectObject(hdc, font);
        DrawTextW(hdc, text, -1, ref area, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
        SelectObject(hdc, previous);
        DeleteObject(font);
    }
}
