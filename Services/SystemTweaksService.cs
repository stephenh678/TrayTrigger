using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

public partial class SystemTweaksService
{
    private readonly Func<AppSettings>? _settingsProvider;
    private readonly Action? _saveSettings;

    public SystemTweaksService() { }

    /// <param name="settingsProvider">Where <see cref="AppSettings.TweakPriorState"/> lives - what a tweak found before it was applied.</param>
    /// <param name="saveSettings">Persists it after a capture/restore.</param>
    public SystemTweaksService(Func<AppSettings> settingsProvider, Action saveSettings)
    {
        _settingsProvider = settingsProvider;
        _saveSettings = saveSettings;
    }

    /// <summary>Windows 11 (build 22000+) - gates the Graphics-page settings that Windows 10 ignores.</summary>
    internal static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    // ---- Prior-state capture (see AppSettings.TweakPriorState) ---------------------------------

    /// <summary>Records what the machine had before a tweak is applied, unless already recorded
    /// (re-applying an already-applied tweak must not overwrite the real original).</summary>
    private void CapturePrior(string tweakId, string value)
    {
        var settings = _settingsProvider?.Invoke();
        if (settings == null) return;
        if (settings.TweakPriorState.ContainsKey(tweakId)) return;
        settings.TweakPriorState[tweakId] = value;
        _saveSettings?.Invoke();
    }

    /// <summary>Reads and forgets the recorded prior value (the revert consumes it).</summary>
    private string? TakePrior(string tweakId)
    {
        var settings = _settingsProvider?.Invoke();
        if (settings == null) return null;
        if (!settings.TweakPriorState.Remove(tweakId, out string? value)) return null;
        _saveSettings?.Invoke();
        return value;
    }

    private string? PeekPrior(string tweakId) =>
        _settingsProvider?.Invoke()?.TweakPriorState.GetValueOrDefault(tweakId);

    private const uint SPI_SETMOUSE = 0x0004;
    private const uint SPIF_UPDATEINIFILE = 0x0001;
    private const uint SPIF_SENDCHANGE = 0x0002;

    private const uint SPI_GETANIMATION = 0x0048;
    private const uint SPI_SETANIMATION = 0x0049;
    private const uint SPI_GETDROPSHADOW = 0x1024;
    private const uint SPI_SETDROPSHADOW = 0x1025;

    [StructLayout(LayoutKind.Sequential)]
    private struct ANIMATIONINFO
    {
        public uint cbSize;
        public int iMinAnimate; // nonzero = minimize/restore window animation enabled
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfoAnimation(uint uiAction, uint uiParam, ref ANIMATIONINFO pvParam, uint fWinIni);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);

    private const uint WM_SETTINGCHANGE = 0x001A;
    private static readonly IntPtr HWND_BROADCAST = (IntPtr)0xffff;

    private static void NotifySettingsChanged()
    {
        try
        {
            PostMessage(HWND_BROADCAST, WM_SETTINGCHANGE, UIntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    // =========================================================================
    // Get All Tweaks with Live System Detection
    // =========================================================================

    public List<SystemTweakItem> GetAllTweaks()
    {
        var list = new List<SystemTweakItem>();

        // ---------------------------------------------------------------------
        // Category 1: Input & Display
        // ---------------------------------------------------------------------

        bool mouseAccelDisabled = CheckMouseAccelerationDisabled();
        list.Add(new SystemTweakItem
        {
            Id = "mouse_accel",
            Name = "Disable Mouse Acceleration (Enhanced Pointer Precision)",
            Category = TweakCategory.InputAndDisplay,
            ShortDescription = "Enforces pure 1:1 linear mouse movement without velocity-based scaling.",
            WhyItMatters = "Critical for competitive shooters (CS2, Valorant, Apex). Standard Windows mouse acceleration varies crosshair speed depending on how fast you flick your hand, destroying muscle memory.",
            IsOptimal = mouseAccelDisabled,
            StatusText = mouseAccelDisabled ? "Optimal (1:1 Raw Input)" : "Standard (Acceleration On)",
            RequiresAdmin = false,
            RequiresReboot = false
        });

        var hags = HagsQuery.Query();
        bool hagsEnabled = CheckHagsEnabled();
        bool hagsSupported = !hags.Queried || hags.Supported;
        list.Add(new SystemTweakItem
        {
            Id = "hags",
            Name = "Hardware-Accelerated GPU Scheduling (HAGS)",
            Category = TweakCategory.InputAndDisplay,
            ShortDescription = "Offloads high-frequency graphics scheduling directly to the GPU's dedicated processor.",
            WhyItMatters = "Reduces CPU interrupt latency and driver overhead. Mandatory prerequisite for modern tech like DLSS 3 Frame Generation.",
            IsOptimal = hagsEnabled,
            StatusText = !hagsSupported
                ? "Not supported by this GPU or driver"
                : hagsEnabled ? "Optimal (HAGS Active)" : "Standard (CPU Scheduled)",
            RequiresAdmin = true,
            RequiresReboot = true,
            HasCustomAction = true,
            CustomActionLabel = "Open Graphics Settings",
            IsAvailable = hagsSupported,
            UnavailableReason = hagsSupported ? "" : "Your GPU or its driver reports no HAGS support (WDDM 2.7 capability query). Update the driver or check the Graphics settings page."
        });

        bool windowedOptsEnabled = CheckWindowedOptsEnabled();
        list.Add(new SystemTweakItem
        {
            Id = "windowed_opts",
            Name = "Optimizations for Windowed Games (DirectFlip Model)",
            Category = TweakCategory.InputAndDisplay,
            ShortDescription = "Upgrades borderless windowed DX10/DX11 games to the modern low-latency flip presentation.",
            WhyItMatters = "Delivers true exclusive fullscreen input latency while letting you seamlessly alt-tab without screen blackouts or monitor mode resets.",
            IsOptimal = windowedOptsEnabled,
            StatusText = !IsWindows11 ? "Windows 11 only" : windowedOptsEnabled ? "Optimal (DirectFlip Active)" : "Standard (Legacy Blt)",
            RequiresAdmin = false,
            RequiresReboot = false,
            IsAvailable = IsWindows11,
            UnavailableReason = IsWindows11 ? "" : "This Graphics setting exists only on Windows 11; Windows 10 ignores the value."
        });

        bool vrrEnabled = CheckVrrEnabled();
        list.Add(new SystemTweakItem
        {
            Id = "vrr_global",
            Name = "Variable Refresh Rate for Windowed Games",
            Category = TweakCategory.InputAndDisplay,
            ShortDescription = "Lets G-SYNC / FreeSync / Adaptive-Sync engage for windowed and borderless DX11 games, not just exclusive fullscreen.",
            WhyItMatters = "Windows 11's own Graphics setting. Without it, a game that runs borderless (most modern titles) on a VRR monitor can fall back to fixed refresh and tearing/stutter unless the GPU driver forces windowed VRR. Costs nothing when the display isn't VRR-capable.",
            IsOptimal = vrrEnabled,
            StatusText = !IsWindows11 ? "Windows 11 only" : vrrEnabled ? "Optimal (Windowed VRR On)" : "Standard (Fullscreen VRR only)",
            RequiresAdmin = false,
            RequiresReboot = false,
            IsAvailable = IsWindows11,
            UnavailableReason = IsWindows11 ? "" : "This Graphics setting exists only on Windows 11."
        });

        bool autoHdrEnabled = CheckAutoHdrEnabled();
        bool hasHdrDisplay = HasHdrCapableDisplay();
        list.Add(new SystemTweakItem
        {
            Id = "auto_hdr",
            Name = "Auto HDR",
            Category = TweakCategory.InputAndDisplay,
            ShortDescription = "Lets Windows 11 render DX11/DX12 SDR games in HDR on an HDR display.",
            WhyItMatters = "Microsoft's own Auto HDR feature: games that only render SDR get their highlights and colour range expanded to HDR at the compositor, the same tone-mapping Xbox uses. Purely a visual upgrade - it does not raise frame rates and can look wrong on a display with poor HDR, so it's opt-in. Only meaningful when the display has HDR turned on (see the HDR option under Performance Profiles).",
            IsOptimal = autoHdrEnabled,
            StatusText = !IsWindows11 ? "Windows 11 only" : !hasHdrDisplay ? "No HDR-capable display detected" : autoHdrEnabled ? "Auto HDR On" : "Auto HDR Off",
            RequiresAdmin = false,
            RequiresReboot = false,
            IsOptIn = true,
            IsAvailable = IsWindows11 && hasHdrDisplay,
            UnavailableReason = !IsWindows11 ? "Auto HDR exists only on Windows 11." : !hasHdrDisplay ? "No connected display reports HDR support." : ""
        });

        bool stickyKeysDisabled = CheckAccessibilityShortcutsDisabled();
        list.Add(new SystemTweakItem
        {
            Id = "sticky_keys",
            Name = "Disable Sticky / Filter / Toggle Keys Shortcuts",
            Category = TweakCategory.InputAndDisplay,
            ShortDescription = "Stops five Shift taps, an 8-second Shift hold, or a 5-second Num Lock hold from popping an accessibility dialog over your game.",
            WhyItMatters = "Tapping Shift five times mid-match opens the Sticky Keys prompt and steals focus; holding Right Shift for eight seconds turns on Filter Keys and starts dropping keystrokes. Both are the accessibility shortcuts' defaults. This turns off only the keyboard shortcuts (the features stay available from Settings > Accessibility), which is exactly what the Ease of Access page offers.",
            IsOptimal = stickyKeysDisabled,
            StatusText = stickyKeysDisabled ? "Optimal (Shortcuts Off)" : "Standard (Shortcuts Active)",
            RequiresAdmin = false,
            RequiresReboot = false
        });

        bool mpoDisabled = CheckMpoDisabled();
        list.Add(new SystemTweakItem
        {
            Id = "mpo_disable",
            Name = "Disable Multiplane Overlay (MPO)",
            Category = TweakCategory.InputAndDisplay,
            ShortDescription = "Turns off the DWM's multiplane overlay path - NVIDIA's documented workaround for stutter, flicker, and black screens in windowed/borderless games.",
            WhyItMatters = "Multiplane overlays let the display controller compose a game window without the desktop compositor. On some GPU/driver/monitor combinations that path causes intermittent stutter, flickering, or a momentary black screen when a borderless game is on top of other windows; NVIDIA's support article recommends this exact registry value as the fix. Newer drivers have resolved most cases and the overlay path normally saves a little GPU work and latency, so this is opt-in: turn it on only if you see those symptoms.",
            IsOptimal = mpoDisabled,
            StatusText = mpoDisabled ? "MPO Disabled" : "Standard (MPO Active)",
            RequiresAdmin = true,
            RequiresReboot = true,
            IsOptIn = true
        });

        bool fseDisabled = CheckFseDisabled();
        list.Add(new SystemTweakItem
        {
            Id = "fse_behavior",
            Name = "Disable Fullscreen Optimization Compatibility Shims",
            Category = TweakCategory.InputAndDisplay,
            ShortDescription = "Bypasses legacy DWM compatibility hooks on older DirectX 9 / 11 engines.",
            WhyItMatters = "Prevents micro-stutters, unexpected frame rate caps, and frame-pacing judder in older competitive game engines.",
            IsOptimal = fseDisabled,
            StatusText = fseDisabled ? "Shims Disabled" : "Standard (Default Shims)",
            RequiresAdmin = false,
            RequiresReboot = false,
            // Opt-in: on Windows 11 the fullscreen-optimization path IS the flip-model path that
            // Auto HDR and windowed VRR ride on, so disabling it globally helps a few old DX9/11
            // engines and hurts most modern ones. Per-exe (Compatibility tab) is the better tool.
            IsOptIn = true
        });

        // ---------------------------------------------------------------------
        // Category 2: CPU & Scheduling
        // Game Mode thread prioritization and system timer resolution. Power plan, System
        // Responsiveness, and MMCSS Games priority moved to per-game Performance Profiles
        // (see PerformanceProfileService) and are no longer permanent System tweaks.
        // ---------------------------------------------------------------------

        bool ultimatePlanActive = CheckUltimatePlanActive();
        list.Add(new SystemTweakItem
        {
            Id = "power_plan",
            Name = "\"Ultimate Plan - TrayTrigger\" Power Plan",
            Category = TweakCategory.CpuAndPower,
            ShortDescription = "Keeps the CPU at full clock with core parking and PCIe/USB power saving off, all the time - not just during a game session."
                + (SystemInfoService.HasBattery() ? " Laptop detected: this applies on battery too and will cut battery life and raise temperatures." : ""),
            WhyItMatters = "The Windows 'Balanced' plan downclocks cores and parks idle ones during quiet moments, taking 5-15ms to ramp back up. This pins the CPU at 100% min/max state and disables PCIe/USB power-saving states so nothing stutters through a power-state transition. Running this 24/7 (rather than only during a game session via a Performance Profile) trades away idle power savings, heat, and laptop battery life for that headroom at all times, so it's off by default.",
            IsOptimal = ultimatePlanActive,
            StatusText = ultimatePlanActive ? "Optimal (Ultimate Plan - TrayTrigger Active)" : $"Standard ({GetActivePlanFriendlyName()})",
            RequiresAdmin = false,
            RequiresReboot = false,
            IsOptIn = true
        });

        bool gameModeEnabled = CheckGameModeEnabled();
        list.Add(new SystemTweakItem
        {
            Id = "game_mode",
            Name = "Windows Game Mode",
            Category = TweakCategory.CpuAndPower,
            ShortDescription = "Prioritizes game process threads on high-performance cores and halts background updates.",
            WhyItMatters = "Prevents Windows Update from downloading or installing drivers mid-match and isolates game threads on primary CPU cores.",
            IsOptimal = gameModeEnabled,
            StatusText = gameModeEnabled ? "Optimal (Game Mode On)" : "Standard (Game Mode Off)",
            RequiresAdmin = false,
            RequiresReboot = false
        });

        bool timerResolutionOptimal = CheckTimerResolutionOptimal();
        list.Add(new SystemTweakItem
        {
            Id = "timer_resolution",
            Name = "System Timer Resolution",
            Category = TweakCategory.CpuAndPower,
            ShortDescription = "Lets any program's request for a fine system timer apply system-wide again, instead of only to itself - so games that don't ask for one no longer sit on the coarse 15.6 ms tick.",
            WhyItMatters = "Windows 10 (2004+) and 11 changed timer resolution requests to apply per-process instead of system-wide. Games/engines that don't request a high-resolution timer themselves can silently fall back to the coarse default tick, showing up as stutter or an effective low frame-pacing ceiling. This restores the old system-wide high-precision behavior.",
            IsOptimal = timerResolutionOptimal,
            StatusText = timerResolutionOptimal ? "Optimal (System-Wide High Precision)" : "Standard (Per-Process Default)",
            RequiresAdmin = true,
            RequiresReboot = true
        });

        bool visualFxPerformance = CheckVisualFxPerformance();
        list.Add(new SystemTweakItem
        {
            Id = "visual_fx",
            Name = "Windows Visual Effects (Performance Mode for DWM)",
            Category = TweakCategory.CpuAndPower,
            ShortDescription = "Disables desktop window minimize animations and drop shadows to reduce compositor load.",
            WhyItMatters = "Frees a small amount of Desktop Window Manager (DWM) GPU overhead. On modern GPUs/compositors the gaming performance impact is marginal - this is mostly a visual-polish-for-a-small-gain tradeoff, so it's off by default and left to you to decide.",
            IsOptimal = visualFxPerformance,
            StatusText = visualFxPerformance ? "Optimal (Performance Profile)" : "Standard (Visual Effects On)",
            RequiresAdmin = false,
            RequiresReboot = false,
            IsOptIn = true
        });

        // ---------------------------------------------------------------------
        // Category 3: Network & Background
        // Ordered: raw TCP/IP stack tweaks first, then background bandwidth users,
        // then background compute users, then Game Bar-related features last.
        // ---------------------------------------------------------------------

        bool netThrottlingDisabled = CheckNetworkThrottlingDisabled();
        list.Add(new SystemTweakItem
        {
            Id = "net_throttling",
            Name = "Disable MMCSS Network Throttling",
            Category = TweakCategory.NetworkAndBackground,
            ShortDescription = "Lifts the packet-rate cap Windows applies to non-multimedia traffic while audio or video is playing.",
            WhyItMatters = "MMCSS's NetworkThrottlingIndex can cap non-multimedia network throughput while a multimedia task (like game audio) is active - potentially relevant on 64/128-tick competitive servers when Discord or audio is also running. The real-world gaming benefit isn't guaranteed and varies by system; some testing has also found disabling it can increase NDIS DPC activity, so treat this as a situational tweak rather than a sure win.",
            IsOptimal = netThrottlingDisabled,
            StatusText = netThrottlingDisabled ? "Optimal (Uncapped Packet Rate)" : "Standard (Throttled)",
            RequiresAdmin = true,
            // MMCSS reads its SystemProfile values at start-up; a restart is what makes this real.
            RequiresReboot = true
        });

        bool nagleDisabled = CheckNagleDisabled();
        list.Add(new SystemTweakItem
        {
            Id = "nagle_disable",
            Name = "Disable Nagle's Algorithm (TCP Send Delay)",
            Category = TweakCategory.NetworkAndBackground,
            ShortDescription = "Sends small TCP packets immediately instead of batching them. Rarely matters for games, which use UDP.",
            WhyItMatters = "Real-time multiplayer games (FPS/MOBA-style) overwhelmingly use UDP for latency-sensitive traffic specifically to avoid Nagle/ACK-delay coupling in the first place - TCP in a modern game is typically reserved for matchmaking/chat/patching, where the delay doesn't matter. A TCP-based app that actually cares about latency can also request TCP_NODELAY itself. This makes the tweak largely situational rather than a broad win, so it's off by default.",
            IsOptimal = nagleDisabled,
            StatusText = nagleDisabled ? "Nagle Disabled" : "Standard (Nagle Enabled)",
            RequiresAdmin = true,
            // TcpAckFrequency/TCPNoDelay are read when the interface binds - restart (or
            // disable/enable the adapter) to apply.
            RequiresReboot = true,
            IsOptIn = true
        });

        bool deliveryOptDisabled = CheckDeliveryOptimizationDisabled();
        list.Add(new SystemTweakItem
        {
            Id = "delivery_opt",
            Name = "Disable Delivery Optimization (P2P Ping Spikes)",
            Category = TweakCategory.NetworkAndBackground,
            ShortDescription = "Stops Windows from silently seeding and uploading updates to other computers.",
            WhyItMatters = "Windows P2P update sharing can secretly saturate your upstream bandwidth, causing sudden 150ms+ ping spikes and packet loss mid-match.",
            IsOptimal = deliveryOptDisabled,
            StatusText = deliveryOptDisabled ? "Optimal (P2P Uploads Disabled)" : "Standard (P2P Uploading Active)",
            RequiresAdmin = true,
            RequiresReboot = false
        });

        bool gameDvrDisabled = CheckGameDvrDisabled();
        list.Add(new SystemTweakItem
        {
            Id = "game_dvr",
            Name = "Disable Game Bar Captures & Background Recording",
            Category = TweakCategory.NetworkAndBackground,
            ShortDescription = "Turns off Game Bar's capture feature, including the continuous background recording loop that writes clips to disk.",
            WhyItMatters = "Background recording (\"Record what happened\") constantly ties up NVENC/AMD GPU encoders and writes temporary files to your SSD, introducing encoder contention and frame dips. This disables Game Bar capture as a whole - background recording, Win+Alt+R manual recording, and Game Bar screenshots - so use OBS/ShadowPlay/ReLive for clips if you want them.",
            IsOptimal = gameDvrDisabled,
            StatusText = gameDvrDisabled ? "Optimal (Background DVR Off)" : "Standard (Recording In Background)",
            RequiresAdmin = false,
            RequiresReboot = false
        });

        bool telemetryDisabled = CheckTelemetryDisabled();
        list.Add(new SystemTweakItem
        {
            Id = "telemetry_sweeps",
            Name = "Disable Diagnostic Telemetry Scheduled Sweeps",
            Category = TweakCategory.NetworkAndBackground,
            ShortDescription = "Sets the telemetry policy to its lowest level to reduce automated background collection tasks.",
            WhyItMatters = "Lowers how often Windows CompatTelRunner and related diagnostic tasks spin up CPU threads and disk I/O in the background. On Home/Pro editions Windows silently floors this policy at \"Basic\" rather than fully off (only Enterprise/Education can reach zero), so treat this as a reduction, not a complete elimination, of telemetry activity.",
            IsOptimal = telemetryDisabled,
            StatusText = telemetryDisabled ? "Optimal (Telemetry Minimal)" : "Standard (Full Telemetry)",
            RequiresAdmin = true,
            RequiresReboot = false
        });

        bool gameBarDisabled = CheckGameBarDisabled();
        list.Add(new SystemTweakItem
        {
            Id = "game_bar_overlay",
            Name = "Disable Xbox Game Bar Overlay",
            Category = TweakCategory.NetworkAndBackground,
            ShortDescription = "Disables the background GameBar overlay processes if you use Steam/Discord overlays.",
            WhyItMatters = "Frees ~150MB of RAM and prevents overlay hook collisions with OBS, RivaTuner, or Discord.",
            IsOptimal = gameBarDisabled,
            StatusText = gameBarDisabled ? "Optimal (Overlay Disabled)" : "Standard (Overlay Active)",
            RequiresAdmin = false,
            RequiresReboot = false
        });

        var wuDriversExcluded = CheckWuDriversExcluded();
        list.Add(new SystemTweakItem
        {
            Id = "wu_driver_exclude",
            Name = "Exclude Drivers from Windows Update",
            Category = TweakCategory.NetworkAndBackground,
            ShortDescription = "Stops Windows Update from replacing your GPU, chipset, and audio drivers with whatever version it has on offer.",
            WhyItMatters = "Windows Update can swap a pinned GPU driver for an older or newer one with no warning - a classic cause of \"my game started stuttering this week\". This is Microsoft's documented group policy (Exclude drivers from quality updates). Trade-off: you now own driver updates yourself, through the GPU vendor's app or the manufacturer's site, so it's opt-in.",
            IsOptimal = wuDriversExcluded,
            StatusText = wuDriversExcluded ? "Drivers Excluded from WU" : "Standard (WU may install drivers)",
            RequiresAdmin = true,
            RequiresReboot = false,
            IsOptIn = true
        });

        bool prioritySeparationOptimal = CheckPrioritySeparationOptimal();
        list.Add(new SystemTweakItem
        {
            Id = "priority_separation",
            Name = "Foreground Priority Boost (Win32PrioritySeparation)",
            Category = TweakCategory.CpuAndPower,
            ShortDescription = "Gives the foreground program short, variable CPU quanta with a 3:1 boost over background processes.",
            WhyItMatters = "The documented scheduler setting behind System Properties > Performance > \"Adjust for best performance of: Programs\". Windows client already favours the foreground (0x02); 0x26 sharpens it with shorter quanta so a CPU-bound game keeps the core when Discord, a browser, or an updater wants it. Modest, real, and reversible to the exact prior value - but it can make heavy background work (encoding, compiling) slower while a game has focus, so it's opt-in.",
            IsOptimal = prioritySeparationOptimal,
            StatusText = prioritySeparationOptimal ? "Foreground Boost (0x26)" : "Standard scheduling",
            RequiresAdmin = true,
            RequiresReboot = false,
            IsOptIn = true
        });

        // ---------------------------------------------------------------------
        // Category 4: Security & Advanced
        // ---------------------------------------------------------------------

        bool hvciRunning = CheckHvciActive();
        list.Add(new SystemTweakItem
        {
            Id = "core_isolation",
            Name = "Core Isolation / Memory Integrity (HVCI) Status",
            Category = TweakCategory.SecurityAndAdvanced,
            ShortDescription = "Kernel driver protection that costs some CPU headroom in games. Status only - change it in Windows Security.",
            WhyItMatters = "Microsoft has documented a CPU frame rate penalty (roughly 3-8%) in certain games while HVCI is enabled, but it's also a real security boundary against kernel-level exploits and vulnerable driver attacks. This is informational only - weigh the tradeoff yourself and change it in Windows Security if you want to. The state shown is what is running now (Win32_DeviceGuard), which lags the Windows Security switch until you restart.",
            // Informational: IsOptimal here means "feature is ON"; the row shows a neutral ON/OFF
            // badge and is excluded from the optimization score, so turning a security boundary
            // off is never presented as an "optimization".
            IsOptimal = hvciRunning,
            StatusText = hvciRunning ? "Memory Integrity running" : "Memory Integrity off",
            // Status-only: TrayTrigger never writes this, so no ADMIN / RESTART badge - those
            // badges describe what *changing* a row costs, and this row cannot be changed here.
            RequiresAdmin = false,
            RequiresReboot = false,
            CanToggle = false, // Must be changed in Windows Defender GUI safely
            HasCustomAction = true,
            CustomActionLabel = "Open Core Isolation Settings",
            IsInformational = true
        });

        return list;
    }

    // =========================================================================
    // System Restore Point (safety net before bulk tweak changes)
    // =========================================================================

    /// <summary>
    /// Creates a Windows System Restore checkpoint via PowerShell's Checkpoint-Computer so bulk
    /// tweak changes (preset apply / reset-all) can be rolled back from Windows' own Recovery UI
    /// if something goes wrong. Best-effort: returns false (never throws) if System Restore is
    /// disabled for the volume, the user cancels an elevation prompt, or Windows silently skips
    /// the checkpoint under its built-in "one restore point per 24h" throttle.
    /// </summary>
    public static bool CreateSystemRestorePoint(string description)
    {
        try
        {
            string safeDescription = description.Replace("'", "''");
            string psCommand = $"try {{ Checkpoint-Computer -Description '{safeDescription}' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop }} catch {{ exit 1 }}";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{psCommand}\"",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            if (IsElevated)
            {
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
            }
            else
            {
                psi.UseShellExecute = true;
                psi.Verb = "runas";
            }

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            // Snapshot creation can take several seconds - give it a generous timeout, matching
            // the elevated-write pattern used elsewhere in this service.
            proc.WaitForExit(60000);
            if (!proc.HasExited) return false;
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SystemTweaksService", $"CreateSystemRestorePoint failed: {ex.Message}");
            return false;
        }
    }

    // =========================================================================
    // Tweak Application & Reversion
    // =========================================================================

    public bool ApplyTweak(string tweakId, bool enableOptimal)
    {
        try
        {
            bool result = tweakId switch
            {
                "mouse_accel" => SetMouseAcceleration(!enableOptimal),
                "hags" => SetHags(enableOptimal),
                "windowed_opts" => SetWindowedOpts(enableOptimal),
                "fse_behavior" => SetFseDisabled(enableOptimal),
                "power_plan" => SetPowerPlan(enableOptimal),
                "game_mode" => SetGameMode(enableOptimal),
                "timer_resolution" => SetTimerResolution(enableOptimal),
                "visual_fx" => SetVisualFx(enableOptimal),
                "net_throttling" => SetNetworkThrottling(enableOptimal),
                "nagle_disable" => SetNagleDisabled(enableOptimal),
                "delivery_opt" => SetDeliveryOptimization(enableOptimal),
                "game_dvr" => SetGameDvr(!enableOptimal),
                "telemetry_sweeps" => SetTelemetry(!enableOptimal),
                "game_bar_overlay" => SetGameBar(!enableOptimal),
                "vrr_global" => SetVrr(enableOptimal),
                "auto_hdr" => SetAutoHdr(enableOptimal),
                "sticky_keys" => SetAccessibilityShortcutsDisabled(enableOptimal),
                "mpo_disable" => SetMpoDisabled(enableOptimal),
                "wu_driver_exclude" => SetWuDriversExcluded(enableOptimal),
                "priority_separation" => SetPrioritySeparation(enableOptimal),
                _ => false
            };

            LoggingService.Info("SystemTweaksService", $"Tweak '{tweakId}' -> {(enableOptimal ? "Optimal" : "Default")}: {(result ? "succeeded" : "failed")}.");
            return result;
        }
        catch (Exception ex)
        {
            LoggingService.Error("SystemTweaksService", $"Failed to toggle tweak '{tweakId}'", ex);
            return false;
        }
    }

    /// <summary>
    /// Re-reads the real current state of a tweak straight from the system, the same Check used
    /// to build <see cref="GetAllTweaks"/>. Callers should trust this over an <see cref="ApplyTweak"/>
    /// return value, since an elevated write's success/failure can be reported wrong (e.g. a slow
    /// UAC prompt) while the underlying registry/system state is the ground truth.
    /// </summary>
    public bool GetTweakState(string tweakId)
    {
        return tweakId switch
        {
            "mouse_accel" => CheckMouseAccelerationDisabled(),
            "hags" => CheckHagsEnabled(),
            "windowed_opts" => CheckWindowedOptsEnabled(),
            "fse_behavior" => CheckFseDisabled(),
            "power_plan" => CheckUltimatePlanActive(),
            "game_mode" => CheckGameModeEnabled(),
            "timer_resolution" => CheckTimerResolutionOptimal(),
            "visual_fx" => CheckVisualFxPerformance(),
            "net_throttling" => CheckNetworkThrottlingDisabled(),
            "nagle_disable" => CheckNagleDisabled(),
            "delivery_opt" => CheckDeliveryOptimizationDisabled(),
            "game_dvr" => CheckGameDvrDisabled(),
            "telemetry_sweeps" => CheckTelemetryDisabled(),
            "game_bar_overlay" => CheckGameBarDisabled(),
            "vrr_global" => CheckVrrEnabled(),
            "auto_hdr" => CheckAutoHdrEnabled(),
            "sticky_keys" => CheckAccessibilityShortcutsDisabled(),
            "mpo_disable" => CheckMpoDisabled(),
            "wu_driver_exclude" => CheckWuDriversExcluded(),
            "priority_separation" => CheckPrioritySeparationOptimal(),
            "core_isolation" => CheckHvciActive(),
            _ => false
        };
    }

    /// <summary>
    /// Tweak IDs the preset/reset actions apply that require a restart to fully take effect -
    /// used by the UI to decide whether to show a single consolidated restart prompt.
    /// </summary>
    public static readonly string[] RebootRequiredTweakIds = { "hags", "timer_resolution", "net_throttling", "nagle_disable", "mpo_disable" };

    private const string SystemProfileKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    private const string DeliveryOptPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization";
    private const string DataCollectionPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\DataCollection";
    private const string GraphicsDriversKey = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string KernelKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel";
    private const string DwmKey = @"SOFTWARE\Microsoft\Windows\Dwm";
    private const string WindowsUpdatePolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    private const string PriorityControlKey = @"SYSTEM\CurrentControlSet\Control\PriorityControl";

    /// <summary>
    /// Applies every recommended tweak (available, toggleable, not opt-in, not informational - see
    /// <see cref="SystemTweakItem.IsRecommended"/>) that isn't already optimal. HKCU/SPI tweaks
    /// go one by one; every HKLM write is folded into a single elevated reg import so the preset
    /// costs at most one UAC prompt.
    /// </summary>
    public void ApplyRecommendedPerformancePreset()
    {
        var recommended = GetAllTweaks().Where(t => t.IsRecommended && !t.IsOptimal).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        if (recommended.Count == 0) return;

        foreach (var id in new[] { "mouse_accel", "windowed_opts", "vrr_global", "game_mode", "game_dvr", "game_bar_overlay", "sticky_keys" })
        {
            if (recommended.Contains(id)) ApplyTweak(id, true);
        }

        var writes = new List<(string SubKey, string ValueName, object Value, RegistryValueKind Kind)>();
        if (recommended.Contains("net_throttling")) writes.Add((SystemProfileKey, "NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF), RegistryValueKind.DWord));
        if (recommended.Contains("delivery_opt")) writes.Add((DeliveryOptPolicyKey, "DODownloadMode", 0, RegistryValueKind.DWord));
        if (recommended.Contains("telemetry_sweeps")) writes.Add((DataCollectionPolicyKey, "AllowTelemetry", 0, RegistryValueKind.DWord));
        if (recommended.Contains("hags")) writes.Add((GraphicsDriversKey, "HwSchMode", 2, RegistryValueKind.DWord));
        if (recommended.Contains("timer_resolution")) writes.Add((KernelKey, "GlobalTimerResolutionRequests", 1, RegistryValueKind.DWord));
        if (writes.Count > 0)
        {
            bool ok = SetHklmValuesBatch(writes.ToArray());
            LoggingService.Info("SystemTweaksService", $"Preset: {writes.Count} HKLM value(s) {(ok ? "written" : "failed - UAC cancelled or reg import error")}.");
        }
    }

    /// <summary>
    /// Reverts every tweak that is currently applied (and only those - a Balanced plan is not
    /// forced onto a machine that never used the power-plan tweak). HKCU/SPI/powercfg reverts run
    /// one by one; every HKLM revert (writes and deletes) is folded into a single elevated reg
    /// import so the whole reset costs at most one UAC prompt instead of six.
    /// </summary>
    public void ResetAllToDefaults() => ResetToDefaults(GetAllTweaks().Where(t => t.IsOptimal && t.CanToggle && !t.IsInformational).Select(t => t.Id));

    public void ResetToDefaults(IEnumerable<string> tweakIds)
    {
        var ids = tweakIds.ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0) return;

        foreach (var id in new[] { "mouse_accel", "power_plan", "windowed_opts", "vrr_global", "auto_hdr", "fse_behavior", "game_mode", "game_dvr", "visual_fx", "game_bar_overlay", "sticky_keys" })
        {
            if (ids.Contains(id)) ApplyTweak(id, false);
        }

        // Policy values and driver-decides switches: on a stock machine they are simply absent, so
        // "reset" restores that absence rather than writing a different fixed number.
        var entries = new List<RegFileEntry>();
        if (ids.Contains("hags")) entries.Add(new RegFileEntry(GraphicsDriversKey, "HwSchMode", null, RegistryValueKind.None, Delete: true));
        if (ids.Contains("timer_resolution")) entries.Add(new RegFileEntry(KernelKey, "GlobalTimerResolutionRequests", 0, RegistryValueKind.DWord, Delete: false));
        if (ids.Contains("net_throttling")) entries.Add(new RegFileEntry(SystemProfileKey, "NetworkThrottlingIndex", 10, RegistryValueKind.DWord, Delete: false));
        if (ids.Contains("delivery_opt")) entries.Add(new RegFileEntry(DeliveryOptPolicyKey, "DODownloadMode", null, RegistryValueKind.None, Delete: true));
        if (ids.Contains("telemetry_sweeps")) entries.Add(new RegFileEntry(DataCollectionPolicyKey, "AllowTelemetry", null, RegistryValueKind.None, Delete: true));
        if (ids.Contains("mpo_disable")) entries.Add(new RegFileEntry(DwmKey, "OverlayTestMode", null, RegistryValueKind.None, Delete: true));
        if (ids.Contains("wu_driver_exclude")) entries.Add(new RegFileEntry(WindowsUpdatePolicyKey, "ExcludeWUDriversInQualityUpdate", null, RegistryValueKind.None, Delete: true));
        if (ids.Contains("priority_separation"))
        {
            string? prior = TakePrior("priority_separation");
            entries.Add(int.TryParse(prior, out int priorValue)
                ? new RegFileEntry(PriorityControlKey, "Win32PrioritySeparation", priorValue, RegistryValueKind.DWord, Delete: false)
                : new RegFileEntry(PriorityControlKey, "Win32PrioritySeparation", 2, RegistryValueKind.DWord, Delete: false));
        }
        if (ids.Contains("nagle_disable"))
        {
            foreach (var name in GetTcpInterfaceNames())
            {
                entries.Add(new RegFileEntry($@"{TcpInterfacesPath}\{name}", "TcpAckFrequency", null, RegistryValueKind.None, Delete: true));
                entries.Add(new RegFileEntry($@"{TcpInterfacesPath}\{name}", "TCPNoDelay", null, RegistryValueKind.None, Delete: true));
            }
        }

        if (entries.Count > 0)
        {
            bool ok = ApplyHklmEntries(entries);
            LoggingService.Info("SystemTweaksService", $"Reset: {entries.Count} HKLM change(s) {(ok ? "applied" : "failed - UAC cancelled or reg import error")}.");
        }
    }

    /// <summary>Writes and deletes together: direct when elevated, one reg import (one prompt) otherwise.</summary>
    private static bool ApplyHklmEntries(List<RegFileEntry> entries)
    {
        if (entries.Count == 0) return true;
        if (!IsElevated) return RunElevatedRegImport(entries);

        bool allOk = true;
        foreach (var e in entries)
        {
            allOk &= e.Delete
                ? DeleteHklmValue(e.SubKey, e.ValueName)
                : SetHklmValuesBatch((e.SubKey, e.ValueName, e.Value!, e.Kind));
        }
        return allOk;
    }

    private static List<string> GetTcpInterfaceNames()
    {
        try
        {
            using var interfacesKey = Registry.LocalMachine.OpenSubKey(TcpInterfacesPath);
            return interfacesKey == null ? new List<string>() : new List<string>(interfacesKey.GetSubKeyNames());
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SystemTweaksService", $"Could not enumerate TCP interfaces: {ex.Message}");
            return new List<string>();
        }
    }

    private static bool ResetHagsToDefault() => DeleteHklmValue(GraphicsDriversKey, "HwSchMode");
    private static bool ResetDeliveryOptimizationToDefault() => DeleteHklmValue(DeliveryOptPolicyKey, "DODownloadMode");
    private static bool ResetTelemetryToDefault() => DeleteHklmValue(DataCollectionPolicyKey, "AllowTelemetry");

    private static bool ResetNagleToDefault()
    {
        try
        {
            List<string> interfaceNames;
            using (var interfacesKey = Registry.LocalMachine.OpenSubKey(TcpInterfacesPath))
            {
                if (interfacesKey == null) return false;
                interfaceNames = new List<string>(interfacesKey.GetSubKeyNames());
            }

            if (interfaceNames.Count == 0) return false;

            var deletes = new List<(string SubKey, string ValueName)>();
            foreach (var name in interfaceNames)
            {
                string subKey = $@"{TcpInterfacesPath}\{name}";
                deletes.Add((subKey, "TcpAckFrequency"));
                deletes.Add((subKey, "TCPNoDelay"));
            }

            return DeleteHklmValuesBatch(deletes.ToArray());
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SystemTweaksService", $"ResetNagleToDefault failed: {ex.Message}");
            return false;
        }
    }

    // =========================================================================
    // Check Implementations
    // =========================================================================

    private static bool CheckMouseAccelerationDisabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse");
            if (key != null)
            {
                string? speed = key.GetValue("MouseSpeed") as string;
                string? t1 = key.GetValue("MouseThreshold1") as string;
                string? t2 = key.GetValue("MouseThreshold2") as string;
                return speed == "0" && t1 == "0" && t2 == "0";
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// What the driver reports (D3DKMT WDDM 2.7 caps) when the query works; the registry request
    /// only as a fallback. The registry alone is wrong in both directions: "2" on an unsupported
    /// GPU changes nothing, and an absent value on a modern driver often means HAGS is on by default.
    /// </summary>
    private static bool CheckHagsEnabled()
    {
        var state = HagsQuery.Query();
        if (state.Queried)
        {
            return state.Supported && state.Enabled;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(GraphicsDriversKey);
            return key?.GetValue("HwSchMode") is int i && i == 2;
        }
        catch { }
        return false;
    }

    private static bool CheckWindowedOptsEnabled() => IsWindows11 && DirectXGlobalSettings.IsEnabled(DirectXGlobalSettings.SwapEffectUpgrade);
    private static bool CheckVrrEnabled() => IsWindows11 && DirectXGlobalSettings.IsEnabled(DirectXGlobalSettings.Vrr);
    private static bool CheckAutoHdrEnabled() => IsWindows11 && DirectXGlobalSettings.IsEnabled(DirectXGlobalSettings.AutoHdr);

    private static bool HasHdrCapableDisplay()
    {
        try { return HdrControlService.GetDisplayStates().Any(s => s.Supported); }
        catch { return false; }
    }

    private static bool CheckFseDisabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore");
            if (key != null)
            {
                // GameDVR_FSEBehavior only takes effect when GameDVR_HonorUserFSEBehaviorMode=1 -
                // without it Windows ignores the behavior override, so both must be checked.
                var behavior = key.GetValue("GameDVR_FSEBehavior");
                var honor = key.GetValue("GameDVR_HonorUserFSEBehaviorMode");
                return behavior is int b && b == 2 && honor is int h && h == 1;
            }
        }
        catch { }
        return false;
    }

    private static bool CheckUltimatePlanActive()
    {
        try
        {
            string? activeGuid = GetActivePowerSchemeGuid();
            if (!string.IsNullOrWhiteSpace(activeGuid))
            {
                using var schemeKey = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\{activeGuid}");
                string? friendlyName = schemeKey?.GetValue("FriendlyName") as string;
                if (string.Equals(friendlyName, UltimatePlanName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch { }

        // Fallback: query via powercfg /getactivescheme directly
        try
        {
            string output = RunPowercfg("/getactivescheme");
            if (output.Contains(UltimatePlanName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch { }

        return false;
    }

    private static string GetActivePlanFriendlyName()
    {
        try
        {
            string? activeGuid = GetActivePowerSchemeGuid();
            if (!string.IsNullOrWhiteSpace(activeGuid))
            {
                using var schemeKey = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\{activeGuid}");
                string? friendlyName = schemeKey?.GetValue("FriendlyName") as string;
                if (!string.IsNullOrWhiteSpace(friendlyName) && !friendlyName.StartsWith('@'))
                {
                    return friendlyName;
                }
            }
        }
        catch { }

        try
        {
            string output = RunPowercfg("/getactivescheme");
            int startParen = output.IndexOf('(');
            int endParen = output.LastIndexOf(')');
            if (startParen >= 0 && endParen > startParen)
            {
                string name = output.Substring(startParen + 1, endParen - startParen - 1).Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }
        }
        catch { }

        return "Balanced";
    }

    private static bool CheckGameModeEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\GameBar");
            if (key != null)
            {
                var val = key.GetValue("AllowAutoGameMode");
                return val == null || (val is int i && i != 0);
            }
        }
        catch { }
        return true;
    }

    internal const string UltimatePlanName = "Ultimate Plan - TrayTrigger";
    internal const string MmcssGamesTaskPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games";

    private static bool CheckTimerResolutionOptimal()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\kernel");
            if (key != null)
            {
                var val = key.GetValue("GlobalTimerResolutionRequests");
                return val is int i && i == 1;
            }
        }
        catch { }
        return false;
    }

    private static bool CheckVisualFxPerformance()
    {
        // VisualFXSetting alone is just a status marker Windows' own dialog writes - it doesn't
        // reliably reflect whether animations/shadows are actually off. Query the live OS state
        // of the two effects this tweak actually controls instead.
        try
        {
            return !GetMinimizeAnimationEnabled() && !GetDropShadowEnabled();
        }
        catch { }
        return false;
    }

    private static bool GetMinimizeAnimationEnabled()
    {
        var info = new ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<ANIMATIONINFO>() };
        if (SystemParametersInfoAnimation(SPI_GETANIMATION, info.cbSize, ref info, 0))
        {
            return info.iMinAnimate != 0;
        }
        return true; // assume Windows' default (on) if the query fails
    }

    private static bool GetDropShadowEnabled()
    {
        IntPtr buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(buffer, 1);
            if (SystemParametersInfo(SPI_GETDROPSHADOW, 0, buffer, 0))
            {
                return Marshal.ReadInt32(buffer) != 0;
            }
            return true; // assume Windows' default (on) if the query fails
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool CheckNetworkThrottlingDisabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile");
            if (key != null)
            {
                var val = key.GetValue("NetworkThrottlingIndex");
                if (val is int i) return (uint)i == 0xFFFFFFFF || i == -1;
            }
        }
        catch { }
        return false;
    }

    private const string TcpInterfacesPath = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";

    private static bool CheckNagleDisabled()
    {
        try
        {
            using var interfacesKey = Registry.LocalMachine.OpenSubKey(TcpInterfacesPath);
            if (interfacesKey == null) return false;

            var subKeyNames = interfacesKey.GetSubKeyNames();
            if (subKeyNames.Length == 0) return false;

            // Optimal only when every network adapter interface has both values set - a single
            // untouched adapter (e.g. a VPN or virtual adapter added later) means Nagle is still
            // in effect for traffic routed through it.
            foreach (var name in subKeyNames)
            {
                using var ifaceKey = interfacesKey.OpenSubKey(name);
                if (ifaceKey == null) continue;

                var ack = ifaceKey.GetValue("TcpAckFrequency");
                var noDelay = ifaceKey.GetValue("TCPNoDelay");
                bool ackOk = ack is int a && a == 1;
                bool noDelayOk = noDelay is int nd && nd == 1;
                if (!ackOk || !noDelayOk) return false;
            }
            return true;
        }
        catch { }
        return false;
    }

    private static bool CheckDeliveryOptimizationDisabled()
    {
        // Either the policy TrayTrigger writes, or the user's own Settings choice ("Allow
        // downloads from other PCs" off, stored under CurrentVersion\...\Config) counts.
        try
        {
            using var policy = Registry.LocalMachine.OpenSubKey(DeliveryOptPolicyKey);
            if (policy?.GetValue("DODownloadMode") is int p && (p == 0 || p == 99 || p == 100)) return true;
        }
        catch { }
        try
        {
            using var config = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config");
            if (config?.GetValue("DODownloadMode") is int c && (c == 0 || c == 99 || c == 100)) return true;
        }
        catch { }
        return false;
    }

    private static bool CheckGameDvrDisabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore");
            if (key != null)
            {
                var val = key.GetValue("GameDVR_Enabled");
                return val is int i && i == 0;
            }
        }
        catch { }
        return false;
    }

    private static bool CheckTelemetryDisabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection");
            if (key != null)
            {
                var val = key.GetValue("AllowTelemetry");
                return val is int i && i == 0;
            }
        }
        catch { }
        return false;
    }

    private static bool CheckGameBarDisabled()
    {
        try
        {
            // UseNexusForGameBarEnabled is the actual Xbox Game Bar overlay toggle.
            // (AppCaptureEnabled under CurrentVersion\GameDVR is a different setting - it's
            // Game DVR clip recording, owned by the separate "game_dvr" tweak.)
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\GameBar");
            if (key != null)
            {
                var val = key.GetValue("UseNexusForGameBarEnabled");
                return val is int i && i == 0;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Whether Memory Integrity is *running* now (Win32_DeviceGuard.SecurityServicesRunning
    /// contains 2), which is what a game actually pays for. The registry "Enabled" value is only
    /// the switch position and lags a change in Windows Security until the next restart, so it's
    /// the fallback.
    /// </summary>
    private static bool CheckHvciActive()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                @"root\Microsoft\Windows\DeviceGuard", "SELECT SecurityServicesRunning FROM Win32_DeviceGuard");
            foreach (System.Management.ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    if (obj["SecurityServicesRunning"] is uint[] running)
                    {
                        return Array.IndexOf(running, 2u) >= 0;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("SystemTweaksService", $"Win32_DeviceGuard query failed, falling back to registry: {ex.Message}");
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
            return key?.GetValue("Enabled") is int i && i == 1;
        }
        catch { }
        return false;
    }

    // ---- New in 1.3.6: accessibility shortcuts, MPO, WU driver policy, priority separation ----

    private const string StickyKeysPath = @"Control Panel\Accessibility\StickyKeys";
    private const string FilterKeysPath = @"Control Panel\Accessibility\Keyboard Response";
    private const string ToggleKeysPath = @"Control Panel\Accessibility\ToggleKeys";
    // Windows' defaults are 510 / 126 / 62; clearing the "hotkey active" (0x4) and "hotkey
    // confirmation/sound" (0x8/0x10) bits leaves the features available from Settings but no
    // keyboard shortcut can turn them on: 506 / 122 / 58.
    private const string StickyKeysOff = "506", StickyKeysDefault = "510";
    private const string FilterKeysOff = "122", FilterKeysDefault = "126";
    private const string ToggleKeysOff = "58", ToggleKeysDefault = "62";
    private const int HotkeyActiveFlag = 0x4;

    private static bool CheckAccessibilityShortcutsDisabled()
    {
        try
        {
            return !HotkeyActive(StickyKeysPath) && !HotkeyActive(FilterKeysPath) && !HotkeyActive(ToggleKeysPath);
        }
        catch { return false; }

        static bool HotkeyActive(string path)
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            // Absent = Windows default (hotkey on).
            if (key?.GetValue("Flags") is not string s || !int.TryParse(s, out int flags)) return true;
            return (flags & HotkeyActiveFlag) != 0;
        }
    }

    private static bool SetAccessibilityShortcutsDisabled(bool disable)
    {
        try
        {
            Write(StickyKeysPath, disable ? StickyKeysOff : StickyKeysDefault);
            Write(FilterKeysPath, disable ? FilterKeysOff : FilterKeysDefault);
            Write(ToggleKeysPath, disable ? ToggleKeysOff : ToggleKeysDefault);
            NotifySettingsChanged();
            return true;

            static void Write(string path, string flags)
            {
                using var key = Registry.CurrentUser.CreateSubKey(path);
                key?.SetValue("Flags", flags, RegistryValueKind.String);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SystemTweaksService", $"SetAccessibilityShortcutsDisabled failed: {ex.Message}");
            return false;
        }
    }

    private static bool CheckMpoDisabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(DwmKey);
            return key?.GetValue("OverlayTestMode") is int i && i == 5;
        }
        catch { return false; }
    }

    private static bool SetMpoDisabled(bool disable) =>
        disable ? SetHklmDword(DwmKey, "OverlayTestMode", 5) : DeleteHklmValue(DwmKey, "OverlayTestMode");

    private static bool CheckWuDriversExcluded()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(WindowsUpdatePolicyKey);
            return key?.GetValue("ExcludeWUDriversInQualityUpdate") is int i && i == 1;
        }
        catch { return false; }
    }

    private static bool SetWuDriversExcluded(bool exclude) =>
        exclude ? SetHklmDword(WindowsUpdatePolicyKey, "ExcludeWUDriversInQualityUpdate", 1) : DeleteHklmValue(WindowsUpdatePolicyKey, "ExcludeWUDriversInQualityUpdate");

    private const int PrioritySeparationBoost = 0x26;
    private const int PrioritySeparationClientDefault = 0x02;

    private static int? ReadPrioritySeparation()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PriorityControlKey);
            return key?.GetValue("Win32PrioritySeparation") is int i ? i : null;
        }
        catch { return null; }
    }

    private static bool CheckPrioritySeparationOptimal() => ReadPrioritySeparation() == PrioritySeparationBoost;

    private bool SetPrioritySeparation(bool boost)
    {
        if (boost)
        {
            CapturePrior("priority_separation", (ReadPrioritySeparation() ?? PrioritySeparationClientDefault).ToString());
            return SetHklmDword(PriorityControlKey, "Win32PrioritySeparation", PrioritySeparationBoost);
        }

        string? prior = TakePrior("priority_separation");
        int restore = int.TryParse(prior, out int p) ? p : PrioritySeparationClientDefault;
        return SetHklmDword(PriorityControlKey, "Win32PrioritySeparation", restore);
    }

    // =========================================================================
    // Set / Toggle Actions
    // =========================================================================

    private static bool SetMouseAcceleration(bool enableAccel)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Mouse");
            if (key != null)
            {
                if (enableAccel)
                {
                    key.SetValue("MouseSpeed", "1");
                    key.SetValue("MouseThreshold1", "6");
                    key.SetValue("MouseThreshold2", "10");
                }
                else
                {
                    key.SetValue("MouseSpeed", "0");
                    key.SetValue("MouseThreshold1", "0");
                    key.SetValue("MouseThreshold2", "0");
                }
            }

            // Update live Windows mouse parameters
            int[] vals = enableAccel ? new[] { 6, 10, 1 } : new[] { 0, 0, 0 };
            IntPtr ptr = Marshal.AllocHGlobal(vals.Length * sizeof(int));
            try
            {
                Marshal.Copy(vals, 0, ptr, vals.Length);
                SystemParametersInfo(SPI_SETMOUSE, 0, ptr, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SystemTweaksService", $"SetMouseAcceleration failed: {ex.Message}");
            return false;
        }
    }

    private static bool SetHags(bool enable)
    {
        int mode = enable ? 2 : 1;
        return SetHklmDword(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", mode);
    }

    // The three Graphics-page toggles share one "key=value;" string; DirectXGlobalSettings
    // rewrites a single token so enabling after Windows disabled it can't leave both "=0" and
    // "=1" behind. Revert removes the token (Windows' own "not set" default).
    private static bool SetWindowedOpts(bool enable) => DirectXGlobalSettings.Write(DirectXGlobalSettings.SwapEffectUpgrade, enable ? "1" : null);
    private static bool SetVrr(bool enable) => DirectXGlobalSettings.Write(DirectXGlobalSettings.Vrr, enable ? "1" : null);
    private static bool SetAutoHdr(bool enable) => DirectXGlobalSettings.Write(DirectXGlobalSettings.AutoHdr, enable ? "1" : null);

    private static bool SetFseDisabled(bool disableFse)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"System\GameConfigStore");
            if (key != null)
            {
                key.SetValue("GameDVR_FSEBehavior", disableFse ? 2 : 0, RegistryValueKind.DWord);
                key.SetValue("GameDVR_HonorUserFSEBehaviorMode", disableFse ? 1 : 0, RegistryValueKind.DWord);
                return true;
            }
        }
        catch { }
        return false;
    }

    private static bool SetGameMode(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\GameBar");
            if (key != null)
            {
                key.SetValue("AllowAutoGameMode", enable ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("AutoGameModeEnabled", enable ? 1 : 0, RegistryValueKind.DWord);
                return true;
            }
        }
        catch { }
        return false;
    }

    // Windows' built-in hidden "Ultimate Performance" scheme, used as the base we duplicate.
    private const string UltimatePerformanceBaseGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    private const string BalancedPlanGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";

    [GeneratedRegex(@"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})")]
    private static partial Regex GuidRegex();

    internal static string? GetActivePowerSchemeGuid()
    {
        try
        {
            using var schemesKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes");
            string? activeGuid = schemesKey?.GetValue("ActivePowerScheme") as string;
            if (!string.IsNullOrWhiteSpace(activeGuid))
            {
                return activeGuid.Trim('{', '}');
            }
        }
        catch { }

        try
        {
            string output = RunPowercfg("/getactivescheme");
            var match = GuidRegex().Match(output);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }
        catch { }

        return null;
    }

    private static List<string> FindAllUltimatePlanGuids()
    {
        var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Check registry
        try
        {
            using var schemesRoot = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes");
            if (schemesRoot != null)
            {
                foreach (var subKeyName in schemesRoot.GetSubKeyNames())
                {
                    using var schemeKey = schemesRoot.OpenSubKey(subKeyName);
                    string? friendlyName = schemeKey?.GetValue("FriendlyName") as string;
                    if (string.Equals(friendlyName, UltimatePlanName, StringComparison.OrdinalIgnoreCase))
                    {
                        guids.Add(subKeyName);
                    }
                }
            }
        }
        catch { }

        // 2. Fallback check: parse powercfg /list
        try
        {
            string output = RunPowercfg("/list");
            foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Contains(UltimatePlanName, StringComparison.OrdinalIgnoreCase))
                {
                    var match = GuidRegex().Match(line);
                    if (match.Success)
                    {
                        guids.Add(match.Groups[1].Value);
                    }
                }
            }
        }
        catch { }

        return guids.ToList();
    }

    internal static string? FindExistingUltimatePlanGuid()
    {
        var allGuids = FindAllUltimatePlanGuids();
        if (allGuids.Count == 0) return null;

        // If the active scheme is one of them, keep that one!
        string? activeGuid = GetActivePowerSchemeGuid();
        string primaryGuid = allGuids.FirstOrDefault(g => string.Equals(g, activeGuid, StringComparison.OrdinalIgnoreCase)) 
                             ?? allGuids[0];

        // Clean up any extraneous duplicate plans if more than one exists
        if (allGuids.Count > 1)
        {
            foreach (var extraGuid in allGuids)
            {
                if (!string.Equals(extraGuid, primaryGuid, StringComparison.OrdinalIgnoreCase))
                {
                    LoggingService.Info("SystemTweaksService", $"Deleting duplicate '{UltimatePlanName}' scheme: {extraGuid}");
                    RunPowercfg($"/delete {extraGuid}");
                }
            }
        }

        return primaryGuid;
    }

    internal static string? CreateUltimateTrayTriggerPlan()
    {
        try
        {
            string output = RunPowercfg($"/duplicatescheme {UltimatePerformanceBaseGuid}");
            var match = GuidRegex().Match(output);
            if (!match.Success)
            {
                LoggingService.Warn("SystemTweaksService", $"Could not parse GUID from powercfg duplicatescheme output: '{output}'");
                return null;
            }

            string newGuid = match.Groups[1].Value;
            RunPowercfg($"/changename {newGuid} \"{UltimatePlanName}\" \"Maximum performance profile created by TrayTrigger for competitive gaming.\"");
            return newGuid;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SystemTweaksService", $"Failed to create '{UltimatePlanName}' power scheme: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Enabling records the scheme that was active first, so reverting puts *that* back (a user
    /// on "High performance" or an OEM plan does not get Balanced instead). Balanced is only the
    /// fallback when nothing was recorded, e.g. a library from before this was tracked.
    /// </summary>
    private bool SetPowerPlan(bool useUltimatePlan)
    {
        try
        {
            if (!useUltimatePlan)
            {
                string? prior = TakePrior("power_plan");
                string target = Guid.TryParseExact(prior, "D", out _) && !IsUltimatePlanGuid(prior!) ? prior! : BalancedPlanGuid;
                RunPowercfg($"/setactive {target}");
                NotifySettingsChanged();
                bool restored = string.Equals(GetActivePowerSchemeGuid(), target, StringComparison.OrdinalIgnoreCase);
                if (!restored && !string.Equals(target, BalancedPlanGuid, StringComparison.OrdinalIgnoreCase))
                {
                    // The recorded scheme no longer exists (deleted since) - fall back to Balanced.
                    LoggingService.Warn("SystemTweaksService", $"Prior power scheme {target} could not be activated; falling back to Balanced.");
                    RunPowercfg($"/setactive {BalancedPlanGuid}");
                    restored = string.Equals(GetActivePowerSchemeGuid(), BalancedPlanGuid, StringComparison.OrdinalIgnoreCase);
                }
                return restored;
            }

            string? active = GetActivePowerSchemeGuid();
            if (active != null && !IsUltimatePlanGuid(active))
            {
                CapturePrior("power_plan", active);
            }

            string? schemeGuid = FindExistingUltimatePlanGuid() ?? CreateUltimateTrayTriggerPlan();
            if (string.IsNullOrWhiteSpace(schemeGuid))
            {
                LoggingService.Warn("SystemTweaksService", "Could not create or locate the 'Ultimate Plan - TrayTrigger' power scheme.");
                return false;
            }

            bool tweaksApplied = ApplyUltimatePlanTweaks(schemeGuid);
            RunPowercfg($"/setactive {schemeGuid}");
            NotifySettingsChanged();
            bool activated = string.Equals(GetActivePowerSchemeGuid(), schemeGuid, StringComparison.OrdinalIgnoreCase);
            return tweaksApplied && activated;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SystemTweaksService", $"SetPowerPlan failed: {ex.Message}");
            return false;
        }
    }

    private static bool IsUltimatePlanGuid(string guid) =>
        FindAllUltimatePlanGuids().Any(g => string.Equals(g, guid, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Each powercfg call runs on its own and is checked on its own: the previous "cmd /c a & b &
    /// ..." chain reported only the last command's exit code, so a rejected hidden setting
    /// (CPMINCORES, PERFBOOSTMODE) failed silently.
    /// </summary>
    internal static bool ApplyUltimatePlanTweaks(string schemeGuid)
    {
        if (!Guid.TryParseExact(schemeGuid, "D", out _)) return false;

        var settings = new (string Subgroup, string Setting, int Val)[]
        {
            ("SUB_PROCESSOR", "PROCTHROTTLEMIN", 100),   // Minimum processor state: 100%
            ("SUB_PROCESSOR", "PROCTHROTTLEMAX", 100),   // Maximum processor state: 100%
            ("SUB_PROCESSOR", "SYSCOOLPOL", 1),          // System cooling policy: Active
            ("SUB_PROCESSOR", "CPMINCORES", 100),        // Core parking: disabled (100% unparked)
            ("SUB_PROCESSOR", "PERFBOOSTMODE", 2),       // Processor performance boost mode: Aggressive
            ("SUB_PCIEXPRESS", "ASPM", 0),               // PCI Express link state power management: Off
            ("SUB_USB", "USBSELECTSUSPEND", 0)           // USB selective suspend: Disabled
        };

        bool allOk = true;
        foreach (var (subgroup, setting, val) in settings)
        {
            foreach (var verb in new[] { "/setacvalueindex", "/setdcvalueindex" })
            {
                if (!RunPowercfgChecked($"{verb} {schemeGuid} {subgroup} {setting} {val}"))
                {
                    LoggingService.Warn("SystemTweaksService", $"powercfg {verb} {subgroup} {setting}={val} failed.");
                    allOk = false;
                }
            }
        }
        return allOk;
    }

    private static bool RunPowercfgChecked(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);
            if (!proc.HasExited) return false;
            if (proc.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
            {
                LoggingService.Verbose("SystemTweaksService", $"powercfg {arguments}: {stderr.Trim()}");
            }
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SystemTweaksService", $"powercfg {arguments} failed: {ex.Message}");
            return false;
        }
    }

    internal static string RunPowercfg(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return string.Empty;

            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            return output;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SystemTweaksService", $"powercfg {arguments} failed: {ex.Message}");
            return string.Empty;
        }
    }

    private static bool SetTimerResolution(bool enableGlobal)
    {
        return SetHklmDword(@"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests", enableGlobal ? 1 : 0);
    }

    private bool SetVisualFx(bool performanceMode)
    {
        bool ok = true;

        // Revert restores what the user actually had (a user who already had shadows off keeps
        // them off), falling back to Windows' defaults (both on) when nothing was recorded.
        bool animationOn = true, shadowOn = true;
        if (performanceMode)
        {
            CapturePrior("visual_fx", $"anim={(GetMinimizeAnimationEnabled() ? 1 : 0)};shadow={(GetDropShadowEnabled() ? 1 : 0)}");
        }
        else
        {
            string? prior = TakePrior("visual_fx");
            if (prior != null)
            {
                animationOn = !prior.Contains("anim=0", StringComparison.Ordinal);
                shadowOn = !prior.Contains("shadow=0", StringComparison.Ordinal);
            }
        }

        // Status marker Windows' own Performance Options dialog reads to pick a radio button.
        // 3 = "Custom": only two effects are changed here, so claiming "Adjust for best
        // performance" (2) would make that dialog apply the full set (font smoothing off, etc.)
        // the next time the user clicks OK in it.
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects");
            key?.SetValue("VisualFXSetting", 3, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SystemTweaksService", $"SetVisualFx: failed to write VisualFXSetting marker: {ex.Message}");
            ok = false;
        }

        // The actual effects: minimize/restore window animation and window drop shadows.
        try
        {
            var info = new ANIMATIONINFO
            {
                cbSize = (uint)Marshal.SizeOf<ANIMATIONINFO>(),
                iMinAnimate = performanceMode ? 0 : (animationOn ? 1 : 0)
            };
            ok &= SystemParametersInfoAnimation(SPI_SETANIMATION, info.cbSize, ref info, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SystemTweaksService", $"SetVisualFx: failed to set minimize animation: {ex.Message}");
            ok = false;
        }

        try
        {
            var dropShadowValue = new IntPtr(performanceMode ? 0 : (shadowOn ? 1 : 0));
            ok &= SystemParametersInfo(SPI_SETDROPSHADOW, 0, dropShadowValue, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SystemTweaksService", $"SetVisualFx: failed to set drop shadow: {ex.Message}");
            ok = false;
        }

        return ok;
    }

    private static bool SetNetworkThrottling(bool disableThrottle)
    {
        int val = disableThrottle ? unchecked((int)0xFFFFFFFF) : 10;
        return SetHklmDword(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", val);
    }

    private static bool SetNagleDisabled(bool disableNagle)
    {
        // Reverting to "Nagle enabled" means restoring the machine default of no value
        // present, not writing 0 (TcpAckFrequency=0 is outside the documented 1-255 range).
        if (!disableNagle)
        {
            return ResetNagleToDefault();
        }

        try
        {
            List<string> interfaceNames;
            using (var interfacesKey = Registry.LocalMachine.OpenSubKey(TcpInterfacesPath))
            {
                if (interfacesKey == null) return false;
                interfaceNames = new List<string>(interfacesKey.GetSubKeyNames());
            }

            if (interfaceNames.Count == 0) return false;

            var writes = new List<(string, string, object, RegistryValueKind)>();
            foreach (var name in interfaceNames)
            {
                string subKey = $@"{TcpInterfacesPath}\{name}";
                writes.Add((subKey, "TcpAckFrequency", 1, RegistryValueKind.DWord));
                writes.Add((subKey, "TCPNoDelay", 1, RegistryValueKind.DWord));
            }

            return SetHklmValuesBatch(writes.ToArray());
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SystemTweaksService", $"SetNagleDisabled failed: {ex.Message}");
            return false;
        }
    }

    // Policy tweaks: revert REMOVES the policy value so Windows Settings gives the control back to
    // the user. Writing a different policy value (the old 3) kept "managed by your organization"
    // on the page - and, for telemetry, forced "Optional" data on, which is more than the default.
    private static bool SetDeliveryOptimization(bool disableP2P) =>
        disableP2P ? SetHklmDword(DeliveryOptPolicyKey, "DODownloadMode", 0) : ResetDeliveryOptimizationToDefault();

    private static bool SetGameDvr(bool enableDvr)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"System\GameConfigStore");
            if (key != null)
            {
                key.SetValue("GameDVR_Enabled", enableDvr ? 1 : 0, RegistryValueKind.DWord);
            }

            using var appKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\GameDVR");
            if (appKey != null)
            {
                appKey.SetValue("AppCaptureEnabled", enableDvr ? 1 : 0, RegistryValueKind.DWord);
                // The specific "Record what happened" (background recording) switch on Windows 11.
                appKey.SetValue("HistoricalCaptureEnabled", enableDvr ? 1 : 0, RegistryValueKind.DWord);
            }
            return true;
        }
        catch { }
        return false;
    }

    private static bool SetTelemetry(bool enableTelemetry) =>
        enableTelemetry ? ResetTelemetryToDefault() : SetHklmDword(DataCollectionPolicyKey, "AllowTelemetry", 0);

    private static bool SetGameBar(bool enableGameBar)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\GameBar");
            if (key != null)
            {
                // UseNexusForGameBarEnabled is the actual overlay on/off switch.
                key.SetValue("UseNexusForGameBarEnabled", enableGameBar ? 1 : 0, RegistryValueKind.DWord);
                // ShowStartupPanel only silences the "Do you want to open Game Bar?" tips
                // popup - kept as a secondary courtesy write, not the primary signal.
                key.SetValue("ShowStartupPanel", enableGameBar ? 1 : 0, RegistryValueKind.DWord);
            }
            return true;
        }
        catch { }
        return false;
    }

    // =========================================================================
    // Safe HKLM Modifier (Direct if admin, elevated reg.exe if not)
    // =========================================================================

    internal static bool SetHklmDword(string subKey, string valueName, int value)
    {
        return SetHklmValuesBatch((subKey, valueName, value, RegistryValueKind.DWord));
    }

    /// <summary>
    /// Writes multiple HKLM values (DWORD or String). When already elevated, each is written
    /// directly with no prompt. When not elevated, all writes are chained into a single elevated
    /// reg.exe/cmd.exe invocation so the caller only sees one UAC prompt instead of one per value.
    /// </summary>
    internal static bool SetHklmValuesBatch(params (string SubKey, string ValueName, object Value, RegistryValueKind Kind)[] writes)
    {
        if (writes.Length == 0) return true;

        if (IsElevated)
        {
            bool allOk = true;
            foreach (var (subKey, valueName, value, kind) in writes)
            {
                try
                {
                    using var key = Registry.LocalMachine.CreateSubKey(subKey, writable: true);
                    if (key != null)
                    {
                        key.SetValue(valueName, value, kind);
                    }
                    else
                    {
                        allOk = false;
                    }
                }
                catch (Exception ex)
                {
                    LoggingService.Warn("SystemTweaksService", $"Direct HKLM write failed for '{subKey}\\{valueName}': {ex.Message}");
                    allOk = false;
                }
            }
            return allOk;
        }

        // Not elevated: one .reg file, one elevated "reg.exe import", one UAC prompt. Unlike the
        // previous "cmd.exe /c reg add ... & reg add ..." chain, no value data ever becomes part
        // of a shell command line - these values can come from the crash-recovery snapshot on
        // disk, which is user-writable, so string concatenation here was an elevation primitive.
        var entries = new List<RegFileEntry>();
        foreach (var (subKey, valueName, value, kind) in writes)
        {
            entries.Add(new RegFileEntry(subKey, valueName, value, kind, Delete: false));
        }
        return RunElevatedRegImport(entries);
    }

    internal static bool DeleteHklmValue(string subKey, string valueName)
    {
        return DeleteHklmValuesBatch((subKey, valueName));
    }

    /// <summary>
    /// Deletes multiple HKLM values, restoring them to "absent" (the real Windows default for
    /// policy values and driver-decides hardware switches). A value that is already absent counts
    /// as success. Mirrors <see cref="SetHklmValuesBatch"/>'s elevated/non-elevated split.
    /// </summary>
    private static bool DeleteHklmValuesBatch(params (string SubKey, string ValueName)[] deletes)
    {
        if (deletes.Length == 0) return true;

        if (IsElevated)
        {
            bool allOk = true;
            foreach (var (subKey, valueName) in deletes)
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(subKey, writable: true);
                    key?.DeleteValue(valueName, throwOnMissingValue: false);
                }
                catch (Exception ex)
                {
                    LoggingService.Warn("SystemTweaksService", $"Direct HKLM delete failed for '{subKey}\\{valueName}': {ex.Message}");
                    allOk = false;
                }
            }
            return allOk;
        }

        // "Name"=- in a .reg file deletes the value, and a value that is already absent is not an
        // error for reg.exe import - which is exactly the "restore to default" semantics wanted.
        var entries = new List<RegFileEntry>();
        foreach (var (subKey, valueName) in deletes)
        {
            entries.Add(new RegFileEntry(subKey, valueName, null, RegistryValueKind.None, Delete: true));
        }
        return RunElevatedRegImport(entries);
    }

    /// <summary>One line of a generated .reg file - see <see cref="BuildRegFileContent"/>.</summary>
    internal readonly record struct RegFileEntry(string SubKey, string ValueName, object? Value, RegistryValueKind Kind, bool Delete);

    /// <summary>
    /// Serialises HKLM writes/deletes into Registry Editor 5.00 format. Pure so it can be unit
    /// tested. The only characters with meaning inside a quoted .reg string are the backslash and
    /// the double quote, and both are escaped, so arbitrary value data - including data read back
    /// from the on-disk crash-recovery snapshot - can never break out of its own line. Line breaks
    /// and ']' in a key path are rejected outright rather than escaped, since the callers only ever
    /// pass compile-time constant key paths and value names.
    /// </summary>
    internal static string BuildRegFileContent(IEnumerable<RegFileEntry> entries)
    {
        static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        static bool HasLineBreak(string s) => s.IndexOf('\r') >= 0 || s.IndexOf('\n') >= 0;

        var sb = new System.Text.StringBuilder();
        sb.Append("Windows Registry Editor Version 5.00\r\n");

        foreach (var group in entries.GroupBy(e => e.SubKey.Trim('\\', '/'), StringComparer.OrdinalIgnoreCase))
        {
            string keyPath = group.Key;
            if (HasLineBreak(keyPath) || keyPath.Contains(']'))
            {
                throw new ArgumentException($"Registry key path is not valid in a .reg file: '{keyPath}'.");
            }

            sb.Append("\r\n[HKEY_LOCAL_MACHINE\\").Append(keyPath).Append("]\r\n");
            foreach (var entry in group)
            {
                if (HasLineBreak(entry.ValueName))
                {
                    throw new ArgumentException($"Registry value name contains a line break: '{entry.ValueName}'.");
                }

                sb.Append(Quote(entry.ValueName)).Append('=');
                if (entry.Delete)
                {
                    sb.Append('-');
                }
                else if (entry.Kind == RegistryValueKind.String)
                {
                    string text = Convert.ToString(entry.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                    if (HasLineBreak(text))
                    {
                        throw new ArgumentException($"REG_SZ value for '{entry.ValueName}' contains a line break.");
                    }
                    sb.Append(Quote(text));
                }
                else if (entry.Kind == RegistryValueKind.DWord)
                {
                    uint dword = unchecked((uint)Convert.ToInt64(entry.Value, System.Globalization.CultureInfo.InvariantCulture));
                    sb.Append("dword:").Append(dword.ToString("x8"));
                }
                else
                {
                    throw new NotSupportedException($"Registry kind {entry.Kind} is not supported for elevated batch writes.");
                }
                sb.Append("\r\n");
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Applies a batch of HKLM changes through one elevated "reg.exe import &lt;file&gt;" (a single
    /// UAC prompt). The .reg file is written to the user's temp folder and deleted afterwards.
    /// </summary>
    private static bool RunElevatedRegImport(List<RegFileEntry> entries)
    {
        string? tempFile = null;
        try
        {
            string content = BuildRegFileContent(entries);
            tempFile = Path.Combine(Path.GetTempPath(), $"TrayTrigger-{Guid.NewGuid():N}.reg");
            // reg.exe expects UTF-16 LE with a BOM for "Version 5.00" files; Encoding.Unicode emits one.
            File.WriteAllText(tempFile, content, System.Text.Encoding.Unicode);

            var psi = new ProcessStartInfo
            {
                FileName = "reg.exe",
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            psi.ArgumentList.Add("import");
            psi.ArgumentList.Add(tempFile);

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            // A generous timeout: WaitForExit(5000) used to return while the user was still
            // looking at the UAC prompt, after which reading ExitCode on a still-running
            // process threw and got swallowed by the catch below as a false "failed".
            proc.WaitForExit(120000);
            if (!proc.HasExited) return false;
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            // Most commonly the user cancelled the UAC prompt (Win32Exception 1223).
            LoggingService.Warn("SystemTweaksService", $"Elevated registry import failed: {ex.Message}");
            return false;
        }
        finally
        {
            if (tempFile != null)
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }
}
