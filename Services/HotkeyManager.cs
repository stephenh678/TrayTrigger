using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

public partial class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int MANAGE_WINDOW_HOTKEY_ID = 1;
    private const int GAME_HOTKEY_BASE_ID = 1000;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _hwndSource;
    private readonly Dictionary<int, string> _registeredGameHotkeys = new();
    private bool _isManageHotkeyRegistered;

    public event Action? ManageHotkeyTriggered;
    public event Action<string>? GameHotkeyTriggered;

    public HotkeyManager()
    {
        var parameters = new HwndSourceParameters("TrayTrigger_HotkeyListener")
        {
            HwndSourceHook = WndProc,
            ParentWindow = new IntPtr(-3) // HWND_MESSAGE
        };
        _hwndSource = new HwndSource(parameters);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == MANAGE_WINDOW_HOTKEY_ID)
            {
                ManageHotkeyTriggered?.Invoke();
                handled = true;
            }
            else if (_registeredGameHotkeys.TryGetValue(id, out string? gameId))
            {
                GameHotkeyTriggered?.Invoke(gameId);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void RegisterHotkeys(string globalManageHotkeyStr, IEnumerable<GameEntry> games)
    {
        UnregisterAll();

        // Register Global Manage Window Hotkey
        if (!string.IsNullOrWhiteSpace(globalManageHotkeyStr) && ParseHotkey(globalManageHotkeyStr, out uint mod, out uint vk))
        {
            if (RegisterHotKey(_hwndSource.Handle, MANAGE_WINDOW_HOTKEY_ID, mod | MOD_NOREPEAT, vk))
            {
                _isManageHotkeyRegistered = true;
            }
            else
            {
                LoggingService.Warn("HotkeyManager", $"Failed to register global hotkey: {globalManageHotkeyStr}");
            }
        }

        // Register Per-Game Hotkeys
        int currentId = GAME_HOTKEY_BASE_ID;
        foreach (var game in games)
        {
            if (!string.IsNullOrWhiteSpace(game.Hotkey) && ParseHotkey(game.Hotkey, out uint gMod, out uint gVk))
            {
                if (RegisterHotKey(_hwndSource.Handle, currentId, gMod | MOD_NOREPEAT, gVk))
                {
                    _registeredGameHotkeys[currentId] = game.Id;
                    currentId++;
                }
                else
                {
                    LoggingService.Warn("HotkeyManager", $"Failed to register hotkey '{game.Hotkey}' for game '{game.Name}'");
                }
            }
        }
    }

    public void UnregisterAll()
    {
        if (_hwndSource == null || _hwndSource.Handle == IntPtr.Zero)
            return;

        if (_isManageHotkeyRegistered)
        {
            UnregisterHotKey(_hwndSource.Handle, MANAGE_WINDOW_HOTKEY_ID);
            _isManageHotkeyRegistered = false;
        }

        foreach (var id in _registeredGameHotkeys.Keys)
        {
            UnregisterHotKey(_hwndSource.Handle, id);
        }
        _registeredGameHotkeys.Clear();
    }

    public static bool ParseHotkey(string hotkeyStr, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;

        if (string.IsNullOrWhiteSpace(hotkeyStr))
            return false;

        var tokens = hotkeyStr.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return false;

        string mainKeyStr = tokens[^1];

        for (int i = 0; i < tokens.Length - 1; i++)
        {
            string mod = tokens[i].ToLowerInvariant();
            if (mod is "ctrl" or "control") modifiers |= MOD_CONTROL;
            else if (mod is "alt") modifiers |= MOD_ALT;
            else if (mod is "shift") modifiers |= MOD_SHIFT;
            else if (mod is "win" or "windows") modifiers |= MOD_WIN;
        }

        Key key;
        if (mainKeyStr.Length == 1 && char.IsAsciiDigit(mainKeyStr[0]))
        {
            // A single digit must map to the number-row key (D5), not to Enum.TryParse's
            // raw underlying-value parse, which would give an unrelated key (e.g. Key.Clear for "5").
            key = Key.D0 + (mainKeyStr[0] - '0');
        }
        else if (int.TryParse(mainKeyStr, out _))
        {
            // No real key corresponds to a multi-digit number; reject rather than let
            // Enum.TryParse map it to whatever enum value happens to share that ordinal.
            return false;
        }
        else if (!Enum.TryParse(mainKeyStr, true, out key))
        {
            return false;
        }

        // Require a modifier unless the key is a standalone function key (F1-F24),
        // otherwise a partially-typed string like "c" would register a bare key globally.
        bool isFunctionKey = key >= Key.F1 && key <= Key.F24;
        if (modifiers == 0 && !isFunctionKey)
            return false;

        virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        return virtualKey != 0;
    }

    public void Dispose()
    {
        UnregisterAll();
        _hwndSource?.Dispose();
    }
}
