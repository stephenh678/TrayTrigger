using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;
using System.Windows.Interop;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

public partial class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int MANAGE_WINDOW_HOTKEY_ID = 1;
    private const int PROBE_HOTKEY_ID = 2;
    private const int GAME_HOTKEY_BASE_ID = 1000;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    /// <summary>The owner id the show/hide window hotkey is registered under; games use their <see cref="GameEntry.Id"/>.</summary>
    public const string ManageOwnerId = "__manage__";

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _hwndSource;
    private readonly Dictionary<int, string> _registeredGameHotkeys = new();
    /// <summary>Every combo currently held, by (modifiers, virtual key) -> who holds it. What the recorder checks against.</summary>
    private readonly Dictionary<(uint Mod, uint Vk), (string OwnerId, string OwnerName)> _owners = new();
    private bool _isManageHotkeyRegistered;

    public event Action? ManageHotkeyTriggered;
    public event Action<string>? GameHotkeyTriggered;

    /// <summary>
    /// The app's one manager, for the hotkey recorder control: it validates a combo against what is
    /// already held and asks Windows whether the rest of the system has it, without the dialogs
    /// needing the manager passed down through every view model.
    /// </summary>
    public static HotkeyManager? Instance { get; private set; }

    public HotkeyManager()
    {
        var parameters = new HwndSourceParameters("TrayTrigger_HotkeyListener")
        {
            HwndSourceHook = WndProc,
            ParentWindow = new IntPtr(-3) // HWND_MESSAGE
        };
        _hwndSource = new HwndSource(parameters);
        Instance = this;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == MANAGE_WINDOW_HOTKEY_ID)
            {
                LoggingService.Info("HotkeyManager", "Global manage-window hotkey triggered.");
                ManageHotkeyTriggered?.Invoke();
                handled = true;
            }
            else if (_registeredGameHotkeys.TryGetValue(id, out string? gameId))
            {
                LoggingService.Info("HotkeyManager", $"Hotkey triggered for game id '{gameId}'.");
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
                _owners[(mod, vk)] = (ManageOwnerId, "the show/hide window hotkey");
                LoggingService.Verbose("HotkeyManager", $"Registered global manage-window hotkey: {globalManageHotkeyStr}");
            }
            else
            {
                LoggingService.Warn("HotkeyManager", $"Failed to register global hotkey '{globalManageHotkeyStr}': {DescribeLastError()}");
            }
        }
        else if (!string.IsNullOrWhiteSpace(globalManageHotkeyStr))
        {
            LoggingService.Warn("HotkeyManager", $"Global hotkey '{globalManageHotkeyStr}' failed to parse - not registered.");
        }

        // Register Per-Game Hotkeys
        int currentId = GAME_HOTKEY_BASE_ID;
        foreach (var game in games)
        {
            if (!string.IsNullOrWhiteSpace(game.Hotkey))
            {
                if (ParseHotkey(game.Hotkey, out uint gMod, out uint gVk))
                {
                    if (RegisterHotKey(_hwndSource.Handle, currentId, gMod | MOD_NOREPEAT, gVk))
                    {
                        _registeredGameHotkeys[currentId] = game.Id;
                        _owners[(gMod, gVk)] = (game.Id, game.Name);
                        LoggingService.Verbose("HotkeyManager", $"Registered hotkey '{game.Hotkey}' for game '{game.Name}'.");
                        currentId++;
                    }
                    else
                    {
                        // 1409 here is usually our own earlier registration: two games with the
                        // same combo, or one matching the window hotkey. The first one wins.
                        LoggingService.Warn("HotkeyManager", $"Failed to register hotkey '{game.Hotkey}' for game '{game.Name}': {DescribeLastError(gMod, gVk)}");
                    }
                }
                else
                {
                    LoggingService.Warn("HotkeyManager", $"Hotkey '{game.Hotkey}' for game '{game.Name}' failed to parse - not registered.");
                }
            }
        }

        LoggingService.Info("HotkeyManager", $"Hotkey registration complete: global={(_isManageHotkeyRegistered ? "on" : "off")}, {_registeredGameHotkeys.Count} game hotkey(s) active.");
    }

    /// <summary>
    /// Why the last RegisterHotKey call failed, in words. When the combo is one this manager
    /// already holds, names the holder rather than blaming another app.
    /// </summary>
    private string DescribeLastError(uint mod = 0, uint vk = 0)
    {
        int error = Marshal.GetLastWin32Error();
        if (error == ERROR_HOTKEY_ALREADY_REGISTERED)
        {
            return _owners.TryGetValue((mod, vk), out var owner)
                ? $"already registered here for {owner.OwnerName}"
                : "already registered by another app (Win32 error 1409)";
        }
        return $"Win32 error {error}";
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

        int unregisteredCount = _registeredGameHotkeys.Count;
        foreach (var id in _registeredGameHotkeys.Keys)
        {
            UnregisterHotKey(_hwndSource.Handle, id);
        }
        _registeredGameHotkeys.Clear();
        _owners.Clear();

        if (unregisteredCount > 0)
        {
            LoggingService.Verbose("HotkeyManager", $"Unregistered {unregisteredCount} game hotkey(s).");
        }
    }

    // ------------------------------------------------------------------ recorder support

    /// <summary>
    /// Whether <paramref name="hotkeyStr"/> can be used by <paramref name="ownerId"/> right now.
    /// Checks, in order: it parses; nothing else in TrayTrigger holds it (the owner's own current
    /// combo is fine); and Windows will grant it - a short-lived probe registration, released at
    /// once, is the only way to learn that another app has it. <paramref name="reason"/> is
    /// written for the dialog when the answer is no.
    /// </summary>
    public bool CheckAvailability(string hotkeyStr, string ownerId, out string reason)
    {
        reason = string.Empty;
        if (!ParseHotkey(hotkeyStr, out uint mod, out uint vk))
        {
            reason = "That isn't a usable shortcut. Use Ctrl, Alt or Shift plus a key, or a function key on its own.";
            return false;
        }

        if (_owners.TryGetValue((mod, vk), out var owner))
        {
            if (string.Equals(owner.OwnerId, ownerId, StringComparison.Ordinal)) return true;
            reason = owner.OwnerId == ManageOwnerId
                ? "Already used to show and hide the TrayTrigger window."
                : $"Already used to launch {owner.OwnerName}.";
            return false;
        }

        if (_hwndSource.Handle == IntPtr.Zero) return true;
        if (RegisterHotKey(_hwndSource.Handle, PROBE_HOTKEY_ID, mod | MOD_NOREPEAT, vk))
        {
            UnregisterHotKey(_hwndSource.Handle, PROBE_HOTKEY_ID);
            return true;
        }

        int error = Marshal.GetLastWin32Error();
        reason = error == ERROR_HOTKEY_ALREADY_REGISTERED
            ? "Another app already uses this shortcut."
            : $"Windows refused this shortcut (error {error}).";
        return false;
    }

    /// <summary>
    /// Combos Windows itself answers to. RegisterHotKey grants most of them (they are handled by
    /// the shell after the keystroke, not registered as hotkeys), and claiming one would take it
    /// away from the whole machine while TrayTrigger runs: no closing windows, no task switching.
    /// Ctrl+Alt+Delete and Win+ combos are refused by Windows anyway; listed for a clear message.
    /// </summary>
    public static bool IsReservedByWindows(ModifierKeys modifiers, Key key)
    {
        if (modifiers.HasFlag(ModifierKeys.Windows)) return true;
        if (key == Key.Snapshot) return true; // PrintScreen with anything: Snipping Tool and Game Bar capture.
        return (modifiers, key) switch
        {
            (ModifierKeys.Alt, Key.F4) => true,
            (ModifierKeys.Alt, Key.Tab) => true,
            (ModifierKeys.Alt, Key.Escape) => true,
            (ModifierKeys.Alt, Key.Space) => true,
            (ModifierKeys.Alt, Key.Enter) => true,
            (ModifierKeys.Control, Key.Escape) => true,
            (ModifierKeys.Control | ModifierKeys.Shift, Key.Escape) => true,
            (ModifierKeys.Control | ModifierKeys.Alt, Key.Delete) => true,
            (ModifierKeys.Alt | ModifierKeys.Shift, Key.Tab) => true,
            (ModifierKeys.Control | ModifierKeys.Alt, Key.Tab) => true,
            _ => false
        };
    }

    /// <summary>
    /// A single modifier plus a letter, digit or Enter - Ctrl+S, Alt+D, Shift+5 - is almost always
    /// an application's own shortcut, and a global hotkey on it stops that shortcut working in
    /// every app. Allowed, but the recorder warns first. Two modifiers, or a function key, pass.
    /// </summary>
    public static bool IsCommonAppShortcut(ModifierKeys modifiers, Key key)
    {
        bool singleModifier = modifiers is ModifierKeys.Control or ModifierKeys.Alt or ModifierKeys.Shift;
        if (!singleModifier) return false;
        bool letter = key >= Key.A && key <= Key.Z;
        bool digit = key >= Key.D0 && key <= Key.D9;
        return letter || digit || key == Key.Enter || key == Key.Back || key == Key.Delete || key == Key.Space;
    }

    /// <summary>
    /// The canonical text for a combo, in the form <see cref="ParseHotkey"/> reads back: modifiers in
    /// the order Ctrl, Alt, Shift, Win, then the key by its Key enum name - except the number row,
    /// which is written as the digit ("Ctrl+Alt+5"). Null when the key is a modifier or maps to
    /// no virtual key.
    /// </summary>
    public static string? Format(ModifierKeys modifiers, Key key)
    {
        if (key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System or Key.DeadCharProcessed)
        {
            return null;
        }
        if (KeyInterop.VirtualKeyFromKey(key) == 0) return null;

        var text = new StringBuilder();
        if (modifiers.HasFlag(ModifierKeys.Control)) text.Append("Ctrl+");
        if (modifiers.HasFlag(ModifierKeys.Alt)) text.Append("Alt+");
        if (modifiers.HasFlag(ModifierKeys.Shift)) text.Append("Shift+");
        if (modifiers.HasFlag(ModifierKeys.Windows)) text.Append("Win+");
        text.Append(key >= Key.D0 && key <= Key.D9 ? ((char)('0' + (key - Key.D0))).ToString() : key.ToString());
        return text.ToString();
    }

    /// <summary>
    /// A typed or stored hotkey rewritten in <see cref="Format"/>'s canonical form ("ctrl + alt+g"
    /// becomes "Ctrl+Alt+G"), or null when it doesn't parse. Lets two spellings of one combo
    /// compare equal and display the same on the card and in the tray.
    /// </summary>
    public static string? Normalize(string? hotkeyStr)
    {
        if (string.IsNullOrWhiteSpace(hotkeyStr) || !ParseHotkey(hotkeyStr, out uint mod, out uint vk)) return null;
        var modifiers = ModifierKeys.None;
        if ((mod & MOD_CONTROL) != 0) modifiers |= ModifierKeys.Control;
        if ((mod & MOD_ALT) != 0) modifiers |= ModifierKeys.Alt;
        if ((mod & MOD_SHIFT) != 0) modifiers |= ModifierKeys.Shift;
        if ((mod & MOD_WIN) != 0) modifiers |= ModifierKeys.Windows;
        return Format(modifiers, KeyInterop.KeyFromVirtualKey((int)vk));
    }

    public static bool ParseHotkey(string hotkeyStr, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;

        if (string.IsNullOrWhiteSpace(hotkeyStr))
            return false;

        var tokens = hotkeyStr.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            LoggingService.Verbose("HotkeyManager", $"ParseHotkey('{hotkeyStr}'): no tokens after split - rejected.");
            return false;
        }

        string mainKeyStr = tokens[^1];

        for (int i = 0; i < tokens.Length - 1; i++)
        {
            string mod = tokens[i].ToLowerInvariant();
            if (mod is "ctrl" or "control") modifiers |= MOD_CONTROL;
            else if (mod is "alt") modifiers |= MOD_ALT;
            else if (mod is "shift") modifiers |= MOD_SHIFT;
            else if (mod is "win" or "windows") modifiers |= MOD_WIN;
            else
            {
                // "Cmd+G" used to register as a bare G (then be refused for having no modifier),
                // and "Ctrl+Foo+G" as Ctrl+G with the typo silently dropped.
                LoggingService.Verbose("HotkeyManager", $"ParseHotkey('{hotkeyStr}'): unknown modifier '{tokens[i]}' - rejected.");
                return false;
            }
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
            LoggingService.Verbose("HotkeyManager", $"ParseHotkey('{hotkeyStr}'): multi-digit main key '{mainKeyStr}' has no matching key - rejected.");
            return false;
        }
        else if (!Enum.TryParse(mainKeyStr, true, out key))
        {
            LoggingService.Verbose("HotkeyManager", $"ParseHotkey('{hotkeyStr}'): unrecognized main key '{mainKeyStr}' - rejected.");
            return false;
        }

        // Require a modifier unless the key is a standalone function key (F1-F24),
        // otherwise a partially-typed string like "c" would register a bare key globally.
        bool isFunctionKey = key >= Key.F1 && key <= Key.F24;
        if (modifiers == 0 && !isFunctionKey)
        {
            LoggingService.Verbose("HotkeyManager", $"ParseHotkey('{hotkeyStr}'): no modifier and '{mainKeyStr}' is not a standalone-allowed function key - rejected.");
            return false;
        }

        virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0)
        {
            LoggingService.Verbose("HotkeyManager", $"ParseHotkey('{hotkeyStr}'): key '{key}' has no virtual-key mapping - rejected.");
        }
        return virtualKey != 0;
    }

    public void Dispose()
    {
        UnregisterAll();
        _hwndSource?.Dispose();
        if (ReferenceEquals(Instance, this)) Instance = null;
    }
}
