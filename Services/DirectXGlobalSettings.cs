using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace TrayTrigger.Services;

/// <summary>
/// The "key=value;key=value;" list Windows keeps in
/// HKCU\Software\Microsoft\DirectX\UserGpuPreferences\DirectXUserGlobalSettings - the backing store
/// for Settings &gt; Display &gt; Graphics &gt; Default settings (Optimizations for windowed games,
/// Auto HDR, Variable refresh rate). Parsed and re-serialised as a whole so setting one token
/// never duplicates it or clobbers another (the old string-append code produced
/// "...SwapEffectUpgradeEnable=0;SwapEffectUpgradeEnable=1;" after Windows had disabled it).
/// Pure string functions are internal for tests; the registry wrappers are thin.
/// </summary>
public static class DirectXGlobalSettings
{
    public const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    public const string ValueName = "DirectXUserGlobalSettings";

    public const string SwapEffectUpgrade = "SwapEffectUpgradeEnable";
    public const string AutoHdr = "AutoHDREnable";
    public const string Vrr = "VRROptimizeEnable";

    internal static List<KeyValuePair<string, string>> Parse(string? raw)
    {
        var list = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrWhiteSpace(raw)) return list;
        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            string key = (eq >= 0 ? part[..eq] : part).Trim();
            string value = eq >= 0 ? part[(eq + 1)..].Trim() : string.Empty;
            if (key.Length == 0) continue;
            int existing = list.FindIndex(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0) list[existing] = new(key, value);   // last one wins, duplicates collapse
            else list.Add(new(key, value));
        }
        return list;
    }

    internal static string Serialize(IEnumerable<KeyValuePair<string, string>> tokens) =>
        string.Concat(tokens.Select(kv => kv.Key + "=" + kv.Value + ";"));

    /// <summary>Returns the string with <paramref name="key"/> set to <paramref name="value"/>, or removed when value is null.</summary>
    internal static string SetToken(string? raw, string key, string? value)
    {
        var tokens = Parse(raw);
        tokens.RemoveAll(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase));
        if (value != null) tokens.Add(new(key, value));
        return Serialize(tokens);
    }

    internal static string? GetToken(string? raw, string key) =>
        Parse(raw).FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    public static string? ReadRaw()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) as string;
        }
        catch { return null; }
    }

    /// <summary>True when the token is present and equals "1".</summary>
    public static bool IsEnabled(string token) => GetToken(ReadRaw(), token) == "1";

    /// <summary>Writes the token; null removes it (Windows' "not set" default).</summary>
    public static bool Write(string token, string? value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            if (key == null) return false;
            string updated = SetToken(key.GetValue(ValueName) as string, token, value);
            if (updated.Length == 0) key.DeleteValue(ValueName, throwOnMissingValue: false);
            else key.SetValue(ValueName, updated, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("DirectXGlobalSettings", $"Write {token}={value ?? "<removed>"} failed: {ex.Message}");
            return false;
        }
    }
}
