using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

public partial class SystemTweaksService
{
    private const uint SPI_SETMOUSE = 0x0004;
    private const uint SPIF_UPDATEINIFILE = 0x0001;
    private const uint SPIF_SENDCHANGE = 0x0002;

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

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

        bool hagsEnabled = CheckHagsEnabled();
        list.Add(new SystemTweakItem
        {
            Id = "hags",
            Name = "Hardware-Accelerated GPU Scheduling (HAGS)",
            Category = TweakCategory.InputAndDisplay,
            ShortDescription = "Offloads high-frequency graphics scheduling directly to the GPU's dedicated processor.",
            WhyItMatters = "Reduces CPU interrupt latency and driver overhead. Mandatory prerequisite for modern tech like DLSS 3 Frame Generation.",
            IsOptimal = hagsEnabled,
            StatusText = hagsEnabled ? "Optimal (HAGS Active)" : "Standard (CPU Scheduled)",
            RequiresAdmin = true,
            RequiresReboot = true,
            HasCustomAction = true,
            CustomActionLabel = "Open Graphics Settings"
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
            StatusText = windowedOptsEnabled ? "Optimal (DirectFlip Active)" : "Standard (Legacy Blt)",
            RequiresAdmin = false,
            RequiresReboot = false
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
            StatusText = fseDisabled ? "Optimal (Shims Disabled)" : "Standard (Default Shims)",
            RequiresAdmin = false,
            RequiresReboot = false
        });

        // ---------------------------------------------------------------------
        // Category 2: CPU & Scheduling
        // Ordered from foundational (power plan, Game Mode) down to fine-grained
        // scheduler/timer tuning, ending with the purely cosmetic visual effects tweak.
        // ---------------------------------------------------------------------

        bool ultimatePlanActive = CheckUltimatePlanActive();
        list.Add(new SystemTweakItem
        {
            Id = "power_plan",
            Name = "\"Ultimate Plan - TrayTrigger\" Power Plan",
            Category = TweakCategory.CpuAndPower,
            ShortDescription = "Creates and activates a custom power plan (based on Windows' hidden Ultimate Performance scheme) with CPU locked at 100%, core parking disabled, and PCIe/USB power-saving states turned off.",
            WhyItMatters = "The Windows 'Balanced' plan downclocks cores and parks idle ones during quiet moments, taking 5–15ms to ramp back up and inducing 1% low frame drops when action begins. \"Ultimate Plan - TrayTrigger\" pins the CPU at 100% min/max state with aggressive boost and active cooling, and disables PCIe Link State Power Management and USB selective suspend so the GPU and input devices never stutter through a power-state transition mid-match.",
            IsOptimal = ultimatePlanActive,
            StatusText = ultimatePlanActive ? "Optimal (Ultimate Plan - TrayTrigger Active)" : $"Standard ({GetActivePlanFriendlyName()})",
            RequiresAdmin = false,
            RequiresReboot = false
        });

        bool gameModeEnabled = CheckGameModeEnabled();
        list.Add(new SystemTweakItem
        {
            Id = "game_mode",
            Name = "Windows Game Mode Thread Priority",
            Category = TweakCategory.CpuAndPower,
            ShortDescription = "Prioritizes game process threads on high-performance cores and halts background updates.",
            WhyItMatters = "Prevents Windows Update from downloading or installing drivers mid-match and isolates game threads on primary CPU cores.",
            IsOptimal = gameModeEnabled,
            StatusText = gameModeEnabled ? "Optimal (Game Mode On)" : "Standard (Game Mode Off)",
            RequiresAdmin = false,
            RequiresReboot = false
        });

        bool sysResponsivenessOptimal = CheckSystemResponsivenessOptimal();
        list.Add(new SystemTweakItem
        {
            Id = "sys_responsiveness",
            Name = "System Responsiveness (MMCSS Gaming Reserve)",
            Category = TweakCategory.CpuAndPower,
            ShortDescription = "Reduces the CPU reserve for lower-priority MMCSS tasks to the supported minimum.",
            WhyItMatters = "Windows reserves 20% of CPU resources for low-priority background tasks by default. Microsoft's MMCSS documentation clamps any value below 10 back up to 20, so 10 is the lowest reserve Windows actually honors - it leaves more scheduling headroom for latency-sensitive foreground workloads like games.",
            IsOptimal = sysResponsivenessOptimal,
            StatusText = sysResponsivenessOptimal ? "Optimal (10% Reserved - Minimum Supported)" : "Standard (20% Reserved)",
            RequiresAdmin = true,
            RequiresReboot = false
        });

        bool mmcssGamesPriorityOptimal = CheckMmcssGamesPriorityOptimal();
        list.Add(new SystemTweakItem
        {
            Id = "mmcss_games_priority",
            Name = "MMCSS \"Games\" Task Scheduling Tuning",
            Category = TweakCategory.CpuAndPower,
            ShortDescription = "Raises the Multimedia Class Scheduler's built-in \"Games\" task from its Medium default to High.",
            WhyItMatters = "Officially documented by Microsoft, MMCSS grants time-sensitive threads registered under the \"Games\" task category prioritized CPU access - the same mechanism game engines request via AvSetMmThreadCharacteristics. Windows ships this task at Scheduling Category=Medium by default; raising it to High uses the same sanctioned mechanism with more headroom. This is scheduling tuning, not a guaranteed FPS boost - the effect depends on what else is contending for the CPU. (Other fields some optimizer tools also touch here, like SFIO Priority, are documented by Microsoft as not used, so this tweak leaves them alone.)",
            IsOptimal = mmcssGamesPriorityOptimal,
            StatusText = mmcssGamesPriorityOptimal ? "Optimal (High Priority)" : "Standard (Medium Priority)",
            RequiresAdmin = true,
            RequiresReboot = true
        });

        bool timerResolutionOptimal = CheckTimerResolutionOptimal();
        list.Add(new SystemTweakItem
        {
            Id = "timer_resolution",
            Name = "System Timer Resolution",
            Category = TweakCategory.CpuAndPower,
            ShortDescription = "Forces Windows to use its highest-precision system timer instead of falling back to the default 15.6ms tick.",
            WhyItMatters = "Windows 10 (2004+) and 11 changed timer resolution requests to apply per-process instead of system-wide. Games/engines that don't request a high-resolution timer themselves can silently fall back to the coarse default tick, showing up as stutter or an effective low frame-pacing ceiling. This restores the old system-wide high-precision behavior.",
            IsOptimal = timerResolutionOptimal,
            StatusText = timerResolutionOptimal ? "Optimal (System-Wide High Precision)" : "Standard (Per-Process Default)",
            RequiresAdmin = true,
            RequiresReboot = true
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
            ShortDescription = "Removes MMCSS's legacy network packet limit (default ~10,000 packets/sec) instead of 10.",
            WhyItMatters = "MMCSS's NetworkThrottlingIndex can cap non-multimedia network throughput while a multimedia task (like game audio) is active - potentially relevant on 64/128-tick competitive servers when Discord or audio is also running. The real-world gaming benefit isn't guaranteed and varies by system; some testing has also found disabling it can increase NDIS DPC activity, so treat this as a situational tweak rather than a sure win.",
            IsOptimal = netThrottlingDisabled,
            StatusText = netThrottlingDisabled ? "Optimal (Uncapped Packet Rate)" : "Standard (Throttled)",
            RequiresAdmin = true,
            RequiresReboot = false
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
            Name = "Disable Background Game DVR Clip Recording",
            Category = TweakCategory.NetworkAndBackground,
            ShortDescription = "Turns off the continuous hardware encoding loop that writes clips to disk.",
            WhyItMatters = "Background recording constantly ties up NVENC/AMD GPU encoders and writes temporary files to your SSD, introducing encoder contention and frame dips.",
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

        // ---------------------------------------------------------------------
        // Category 4: Security & Advanced
        // ---------------------------------------------------------------------

        bool hvciActive = CheckHvciActive();
        list.Add(new SystemTweakItem
        {
            Id = "core_isolation",
            Name = "Core Isolation / Memory Integrity (HVCI) Status",
            Category = TweakCategory.SecurityAndAdvanced,
            ShortDescription = "Microsoft's hypervisor-enforced code integrity check for kernel drivers - status only, not changed here.",
            WhyItMatters = "Microsoft has documented a CPU frame rate penalty (roughly 3-8%) in certain games while HVCI is enabled, but it's also a real security boundary against kernel-level exploits and vulnerable driver attacks. This is informational only - weigh the tradeoff yourself and change it in Windows Security if you want to.",
            IsOptimal = !hvciActive,
            StatusText = hvciActive ? "Security Enabled (HVCI On)" : "HVCI Off (Reduced Protection)",
            RequiresAdmin = true,
            RequiresReboot = true,
            CanToggle = false, // Must be changed in Windows Defender GUI safely
            HasCustomAction = true,
            CustomActionLabel = "Open Core Isolation Settings"
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
            switch (tweakId)
            {
                case "mouse_accel":
                    return SetMouseAcceleration(!enableOptimal);

                case "hags":
                    return SetHags(enableOptimal);

                case "windowed_opts":
                    return SetWindowedOpts(enableOptimal);

                case "fse_behavior":
                    return SetFseDisabled(enableOptimal);

                case "game_mode":
                    return SetGameMode(enableOptimal);

                case "power_plan":
                    return SetPowerPlan(enableOptimal);

                case "sys_responsiveness":
                    return SetSystemResponsiveness(enableOptimal);

                case "mmcss_games_priority":
                    return SetMmcssGamesPriority(enableOptimal);

                case "timer_resolution":
                    return SetTimerResolution(enableOptimal);

                case "net_throttling":
                    return SetNetworkThrottling(enableOptimal);

                case "delivery_opt":
                    return SetDeliveryOptimization(enableOptimal);

                case "game_dvr":
                    return SetGameDvr(!enableOptimal);

                case "telemetry_sweeps":
                    return SetTelemetry(!enableOptimal);

                case "game_bar_overlay":
                    return SetGameBar(!enableOptimal);

                default:
                    return false;
            }
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
            "game_mode" => CheckGameModeEnabled(),
            "power_plan" => CheckUltimatePlanActive(),
            "sys_responsiveness" => CheckSystemResponsivenessOptimal(),
            "mmcss_games_priority" => CheckMmcssGamesPriorityOptimal(),
            "timer_resolution" => CheckTimerResolutionOptimal(),
            "net_throttling" => CheckNetworkThrottlingDisabled(),
            "delivery_opt" => CheckDeliveryOptimizationDisabled(),
            "game_dvr" => CheckGameDvrDisabled(),
            "telemetry_sweeps" => CheckTelemetryDisabled(),
            "game_bar_overlay" => CheckGameBarDisabled(),
            "core_isolation" => !CheckHvciActive(),
            _ => false
        };
    }

    /// <summary>
    /// Tweak IDs the preset/reset actions apply that require a restart to fully take effect -
    /// used by the UI to decide whether to show a single consolidated restart prompt.
    /// </summary>
    public static readonly string[] RebootRequiredTweakIds = { "hags", "mmcss_games_priority", "timer_resolution" };

    public void ApplyRecommendedPerformancePreset()
    {
        // Tweaks that write directly to HKCU / SPI - no elevation needed, safe to apply individually.
        ApplyTweak("mouse_accel", true);
        ApplyTweak("windowed_opts", true);
        ApplyTweak("fse_behavior", true);
        ApplyTweak("game_mode", true);
        ApplyTweak("power_plan", true);
        ApplyTweak("game_dvr", true);

        // Tweaks that write to HKLM. Applying each individually via ApplyTweak would spawn a
        // separate elevated reg.exe (and UAC prompt) per tweak when not already running as
        // admin - batch them into a single elevated call so the preset needs at most one prompt.
        SetHklmValuesBatch(
            (@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", 10, RegistryValueKind.DWord),
            (@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF), RegistryValueKind.DWord),
            (@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0, RegistryValueKind.DWord),
            (@"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, RegistryValueKind.DWord),
            (@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, RegistryValueKind.DWord),
            (@"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests", 1, RegistryValueKind.DWord),
            (MmcssGamesTaskPath, "Scheduling Category", "High", RegistryValueKind.String));
    }

    public void ResetAllToDefaults()
    {
        ApplyTweak("mouse_accel", false);
        ResetHagsToDefault();
        ApplyTweak("windowed_opts", false);
        ApplyTweak("fse_behavior", false);
        ApplyTweak("game_mode", false);
        ApplyTweak("power_plan", false);
        ApplyTweak("game_dvr", false);
        ApplyTweak("sys_responsiveness", false);
        ApplyTweak("mmcss_games_priority", false);
        ApplyTweak("timer_resolution", false);
        ApplyTweak("net_throttling", false);
        ResetDeliveryOptimizationToDefault();
        ResetTelemetryToDefault();
        ApplyTweak("game_bar_overlay", false);
    }

    // These four tweaks write a Windows *policy* value or force a hardware feature off; on a
    // stock machine the value is simply absent (Windows/the driver decides). "Reset" must restore
    // that absence, not write a different fixed number - unlike disabling the tweak from its own
    // toggle, which is a deliberate "force off" and legitimately writes a value. Until a full
    // snapshot/restore of prior values exists, deleting these four values is the minimum safe reset.
    private static bool ResetHagsToDefault() =>
        DeleteHklmValue(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");

    private static bool ResetDeliveryOptimizationToDefault() =>
        DeleteHklmValue(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode");

    private static bool ResetTelemetryToDefault() =>
        DeleteHklmValue(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry");

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

    private static bool CheckHagsEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
            if (key != null)
            {
                var val = key.GetValue("HwSchMode");
                return val is int i && i == 2;
            }
        }
        catch { }
        return false;
    }

    private static bool CheckWindowedOptsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
            if (key != null)
            {
                string? val = key.GetValue("DirectXUserGlobalSettings") as string;
                return val != null && val.Contains("SwapEffectUpgradeEnable=1");
            }
        }
        catch { }
        return false;
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

    private const string UltimatePlanName = "Ultimate Plan - TrayTrigger";

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

    private static bool CheckSystemResponsivenessOptimal()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile");
            if (key != null)
            {
                var val = key.GetValue("SystemResponsiveness");
                return val is int i && i == 10;
            }
        }
        catch { }
        return false;
    }

    private const string MmcssGamesTaskPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games";

    private static bool CheckMmcssGamesPriorityOptimal()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(MmcssGamesTaskPath);
            if (key != null)
            {
                string? category = key.GetValue("Scheduling Category") as string;
                return string.Equals(category, "High", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { }
        return false;
    }

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

    private static bool CheckDeliveryOptimizationDisabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization");
            if (key != null)
            {
                var val = key.GetValue("DODownloadMode");
                return val is int i && (i == 0 || i == 99 || i == 100);
            }
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

    private static bool CheckHvciActive()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
            if (key != null)
            {
                var val = key.GetValue("Enabled");
                return val is int i && i == 1;
            }
        }
        catch { }
        return false;
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

    private static bool SetWindowedOpts(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
            if (key != null)
            {
                string current = key.GetValue("DirectXUserGlobalSettings") as string ?? "";
                if (enable)
                {
                    if (!current.Contains("SwapEffectUpgradeEnable=1"))
                    {
                        current = current.TrimEnd(';') + (string.IsNullOrEmpty(current) ? "" : ";") + "SwapEffectUpgradeEnable=1;";
                        key.SetValue("DirectXUserGlobalSettings", current, RegistryValueKind.String);
                    }
                }
                else
                {
                    current = current.Replace("SwapEffectUpgradeEnable=1;", "").Replace("SwapEffectUpgradeEnable=1", "").Trim();
                    key.SetValue("DirectXUserGlobalSettings", current, RegistryValueKind.String);
                }
                return true;
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("SystemTweaksService", $"SetWindowedOpts failed: {ex.Message}");
        }
        return false;
    }

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

    private static bool SetPowerPlan(bool useUltimatePlan)
    {
        try
        {
            if (!useUltimatePlan)
            {
                RunPowercfg($"/setactive {BalancedPlanGuid}");
                NotifySettingsChanged();
                return string.Equals(GetActivePowerSchemeGuid(), BalancedPlanGuid, StringComparison.OrdinalIgnoreCase);
            }

            string? schemeGuid = FindExistingUltimatePlanGuid() ?? CreateUltimateTrayTriggerPlan();
            if (string.IsNullOrWhiteSpace(schemeGuid))
            {
                LoggingService.Warn("SystemTweaksService", "Could not create or locate the 'Ultimate Plan - TrayTrigger' power scheme.");
                return false;
            }

            // Tuned per community gaming guidance (drxoptimizer.com/blog/best-power-plan-gaming):
            // lock CPU min/max state at 100%, disable core parking, aggressive turbo boost,
            // active cooling, and disable PCIe/USB power-saving states that otherwise cause
            // frame-time spikes and input lag when cores or devices wake from an idle state.
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

    private static string? GetActivePowerSchemeGuid()
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

    private static string? FindExistingUltimatePlanGuid()
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

    private static string? CreateUltimateTrayTriggerPlan()
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

    private static bool ApplyUltimatePlanTweaks(string schemeGuid)
    {
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

        var commands = new List<string>();
        foreach (var (subgroup, setting, val) in settings)
        {
            commands.Add($"powercfg /setacvalueindex {schemeGuid} {subgroup} {setting} {val}");
            commands.Add($"powercfg /setdcvalueindex {schemeGuid} {subgroup} {setting} {val}");
        }

        return RunCommandBatch(commands);
    }

    private static bool RunCommandBatch(IEnumerable<string> commands)
    {
        try
        {
            string combined = string.Join(" & ", commands);
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{combined}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(5000);
            return proc.HasExited && proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string RunPowercfg(string arguments)
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

    private static bool SetSystemResponsiveness(bool optimal)
    {
        // Microsoft's MMCSS docs: values below 10 are clamped back up to 20, so 10 - not 0 -
        // is the lowest reserve Windows actually honors. See CheckSystemResponsivenessOptimal.
        int val = optimal ? 10 : 20;
        return SetHklmDword(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", val);
    }

    private static bool SetMmcssGamesPriority(bool highPriority)
    {
        // SFIO Priority is intentionally not written here - Microsoft's MMCSS docs state it
        // "is not used", so setting it would be a no-op that only misrepresents what this does.
        string category = highPriority ? "High" : "Medium";
        return SetHklmValuesBatch(
            (MmcssGamesTaskPath, "Scheduling Category", category, RegistryValueKind.String));
    }

    private static bool SetTimerResolution(bool enableGlobal)
    {
        return SetHklmDword(@"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests", enableGlobal ? 1 : 0);
    }

    private static bool SetNetworkThrottling(bool disableThrottle)
    {
        int val = disableThrottle ? unchecked((int)0xFFFFFFFF) : 10;
        return SetHklmDword(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", val);
    }

    private static bool SetDeliveryOptimization(bool disableP2P)
    {
        int val = disableP2P ? 0 : 3;
        return SetHklmDword(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", val);
    }

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
            }
            return true;
        }
        catch { }
        return false;
    }

    private static bool SetTelemetry(bool enableTelemetry)
    {
        int val = enableTelemetry ? 3 : 0;
        return SetHklmDword(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", val);
    }

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

    private static bool SetHklmDword(string subKey, string valueName, int value)
    {
        return SetHklmValuesBatch((subKey, valueName, value, RegistryValueKind.DWord));
    }

    /// <summary>
    /// Writes multiple HKLM values (DWORD or String). When already elevated, each is written
    /// directly with no prompt. When not elevated, all writes are chained into a single elevated
    /// reg.exe/cmd.exe invocation so the caller only sees one UAC prompt instead of one per value.
    /// </summary>
    private static bool SetHklmValuesBatch(params (string SubKey, string ValueName, object Value, RegistryValueKind Kind)[] writes)
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

        try
        {
            // Chain with "&" (not "&&") so a failure on one value doesn't skip the rest -
            // matches the best-effort, per-tweak semantics of a single-value write.
            var commands = new List<string>();
            foreach (var (subKey, valueName, value, kind) in writes)
            {
                string fullPath = @"HKLM\" + subKey;
                string typeFlag = kind == RegistryValueKind.String ? "REG_SZ" : "REG_DWORD";
                string dataArg = kind == RegistryValueKind.String
                    ? $"\"{value}\""
                    : ((uint)Convert.ToInt64(value)).ToString();
                commands.Add($"reg add \"{fullPath}\" /v \"{valueName}\" /t {typeFlag} /d {dataArg} /f");
            }
            string combined = string.Join(" & ", commands);

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{combined}\"",
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;

            // A generous timeout: WaitForExit(5000) used to return while the user was still
            // looking at the UAC prompt, after which reading ExitCode on a still-running
            // process threw and got swallowed by the catch below as a false "failed".
            proc.WaitForExit(120000);
            if (!proc.HasExited) return false;
            return proc.ExitCode == 0;
        }
        catch
        {
            // User likely cancelled UAC prompt
            return false;
        }
    }

    private static bool DeleteHklmValue(string subKey, string valueName)
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

        try
        {
            var commands = new List<string>();
            foreach (var (subKey, valueName) in deletes)
            {
                string fullPath = @"HKLM\" + subKey;
                commands.Add($"reg delete \"{fullPath}\" /v \"{valueName}\" /f");
            }
            // A missing value is not a failure here (it means the default was already restored),
            // so make the overall exit code reflect that the deletes were attempted, not whether
            // every one of them found something to remove.
            string combined = string.Join(" & ", commands) + " & exit /b 0";

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{combined}\"",
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;

            // A generous timeout: WaitForExit(5000) used to return while the user was still
            // looking at the UAC prompt, after which reading ExitCode on a still-running
            // process threw and got swallowed by the catch below as a false "failed".
            proc.WaitForExit(120000);
            if (!proc.HasExited) return false;
            return proc.ExitCode == 0;
        }
        catch
        {
            // User likely cancelled UAC prompt
            return false;
        }
    }
}
