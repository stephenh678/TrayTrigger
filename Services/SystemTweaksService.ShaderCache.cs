using System;
using Microsoft.Win32;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// The two shader cache tweaks. Both are about the same stutter: a game compiles a shader the first
/// time it needs it and caches the result, and anything that empties the cache brings the
/// compiling - and the hitches of a first run - back.
/// </summary>
public partial class SystemTweaksService
{
    /// <summary>Replaceable for tests, which have no NVIDIA driver to talk to.</summary>
    internal NvidiaShaderCacheService NvShaderCache { get; init; } = new();

    private const string DxShaderCacheHandlerKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches\D3D Shader Cache";
    private const string AutorunValue = "Autorun";

    /// <summary>Windows ships the handler with Autorun=1: automatic cleanups may empty it.</summary>
    private const int DxShaderCacheAutorunDefault = 1;

    private SystemTweakItem BuildNvShaderCacheTweak()
    {
        var state = NvShaderCache.Read();
        return new SystemTweakItem
        {
            Id = "nv_shader_cache",
            Name = "NVIDIA Shader Cache: Unlimited",
            Category = TweakCategory.InputAndDisplay,
            ShortDescription = "Lets the NVIDIA driver keep every game's compiled shaders, instead of deleting the oldest when the cache reaches its size limit.",
            WhyItMatters = "The driver keeps the shaders a game compiles on disk, so the next session doesn't compile them again. When the cache is full it deletes the oldest, and a game you come back to compiles them again mid-play - the hitches and frame-time spikes of a first run, back again. A large library fills the default limit quickly. Unlimited is the Control Panel's own choice, set on the Global profile every game inherits; a game's own profile can still say otherwise. Trade-off: the cache folder (%LOCALAPPDATA%\\NVIDIA\\DXCache) keeps growing, which with a big library can reach tens of GB. A driver update clears it either way.",
            IsOptimal = state.IsUnlimited,
            StatusText = !state.IsAvailable ? "No NVIDIA driver" : state.IsUnlimited ? "Optimal (Unlimited)" : "Standard (Size Limited)",
            RequiresAdmin = false,
            RequiresReboot = false,
            IsAvailable = state.IsAvailable,
            UnavailableReason = state.IsAvailable ? ""
                : state.Error != null ? $"The NVIDIA driver's settings could not be read: {state.Error}"
                : "Needs an NVIDIA GPU and driver. AMD and Intel drivers manage their own shader caches."
        };
    }

    private SystemTweakItem BuildDxShaderCacheTweak()
    {
        bool present = DxShaderCacheHandlerPresent();
        bool kept = CheckDxShaderCacheKept();
        return new SystemTweakItem
        {
            Id = "dx_shader_cache",
            Name = "Keep the DirectX Shader Cache",
            Category = TweakCategory.InputAndDisplay,
            ShortDescription = "Stops Windows' automatic cleanups, such as Storage Sense, from deleting the DirectX shader cache.",
            WhyItMatters = "Windows keeps the shaders DirectX games compile in a cache (D3DSCache) so they aren't compiled again. Its automatic cleanups - Storage Sense, and the scheduled Disk Cleanup when a drive runs low - count that cache as junk and delete it, after which every game rebuilds its shaders with the stutter of a first run. This takes the cache out of the automatic runs only: Disk Cleanup still lists it when you run it yourself, so you can clear it if a game's cache ever goes bad.",
            IsOptimal = kept,
            StatusText = !present ? "Cleanup handler not found" : kept ? "Optimal (Cache Kept)" : "Standard (Auto-Cleanup Deletes It)",
            RequiresAdmin = true,
            RequiresReboot = false,
            IsAvailable = present,
            UnavailableReason = present ? "" : "This copy of Windows has no DirectX Shader Cache cleanup handler, so there is nothing that deletes the cache automatically."
        };
    }

    // ---- NVIDIA --------------------------------------------------------------------------------

    private bool CheckNvShaderCacheUnlimited() => NvShaderCache.Read().IsUnlimited;

    private bool SetNvShaderCacheUnlimited(bool unlimited)
    {
        if (unlimited)
        {
            bool applied = NvShaderCache.Apply(out string? prior, out string? error);
            // Recorded whenever the driver saved the write, even if the check afterwards failed: the
            // size may be Unlimited now, and Restore Previous needs to know what it was.
            if (prior != null) CapturePrior("nv_shader_cache", prior);
            if (!applied) LoggingService.Warn("SystemTweaks", $"Setting the NVIDIA shader cache to Unlimited failed: {error}");
            return applied;
        }

        // Peek, then take only once it worked: a failed restore must keep the record for the next try.
        if (!NvShaderCache.Restore(PeekPrior("nv_shader_cache"), out string? restoreError))
        {
            LoggingService.Warn("SystemTweaks", $"Putting the NVIDIA shader cache size back failed: {restoreError}");
            return false;
        }
        TakePrior("nv_shader_cache");
        return true;
    }

    // ---- DirectX -------------------------------------------------------------------------------

    private static bool DxShaderCacheHandlerPresent()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(DxShaderCacheHandlerKey);
            return key != null;
        }
        catch (Exception ex) { LoggingService.Swallowed("SystemTweaks", ex, "looking for the DirectX Shader Cache cleanup handler"); return false; }
    }

    private static int? ReadDxShaderCacheAutorun()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(DxShaderCacheHandlerKey);
            return key?.GetValue(AutorunValue) is int i ? i : null;
        }
        catch (Exception ex) { LoggingService.Swallowed("SystemTweaks", ex, "reading the DirectX Shader Cache cleanup setting"); return null; }
    }

    private static bool CheckDxShaderCacheKept() => ReadDxShaderCacheAutorun() == 0;

    /// <summary>Records what the handler had, once, before the first write.</summary>
    private void CaptureDxShaderCachePrior() =>
        CapturePrior("dx_shader_cache", ReadDxShaderCacheAutorun()?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "absent");

    private bool SetDxShaderCacheKept(bool keep)
    {
        if (keep)
        {
            CaptureDxShaderCachePrior();
            return SetHklmDword(DxShaderCacheHandlerKey, AutorunValue, 0);
        }
        if (!ApplyHklmEntries([RestoreDxShaderCacheEntry()])) return false;
        TakePrior("dx_shader_cache");
        return true;
    }

    /// <summary>
    /// The value found first; absent stays absent, and with no record, Windows' own 1. Only reads
    /// the record: the caller forgets it once the write has worked, so a cancelled administrator
    /// prompt leaves it for the next try.
    /// </summary>
    private RegFileEntry RestoreDxShaderCacheEntry()
    {
        string? prior = PeekPrior("dx_shader_cache");
        if (prior == "absent") return new RegFileEntry(DxShaderCacheHandlerKey, AutorunValue, null, RegistryValueKind.None, Delete: true);
        int value = int.TryParse(prior, out int p) ? p : DxShaderCacheAutorunDefault;
        return new RegFileEntry(DxShaderCacheHandlerKey, AutorunValue, value, RegistryValueKind.DWord, Delete: false);
    }
}
