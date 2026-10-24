using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>What an NVIDIA setting that needs administrator rights is to be changed to.</summary>
public enum NvidiaAdminAction { Set, Delete, RestoreDefault }

/// <summary>
/// One change to an NVIDIA driver setting on the Global profile, for the elevated step. The driver only lets an
/// administrator change some settings, so the change is sent to <see cref="ElevatedHelper"/> instead of being
/// made by TrayTrigger itself. Only the settings named in <see cref="Allowed"/>, with the values listed there, are
/// accepted at either end: the helper runs with administrator rights, so it does only what is written down here.
/// </summary>
public readonly record struct NvidiaAdminOp(uint SettingId, NvidiaAdminAction Action, uint Value = 0)
{
    /// <summary>The settings the elevated step may change, and the values it may write to each.</summary>
    internal static readonly IReadOnlyDictionary<uint, Func<uint, bool>> Allowed = new Dictionary<uint, Func<uint, bool>>
    {
        // "rBAR - Feature": off or on.
        [PerformanceProfileService.NvResizableBarSettingId] = v => v is 0 or 1,
    };

    public bool IsAllowed => Allowed.TryGetValue(SettingId, out var valid) && (Action != NvidiaAdminAction.Set || valid(Value));

    /// <summary>"set:000F00BA:1", "delete:000F00BA" or "default:000F00BA": one command-line argument, digits and letters only.</summary>
    public string ToArgument() => Action switch
    {
        NvidiaAdminAction.Set => $"set:{SettingId:X8}:{Value.ToString(CultureInfo.InvariantCulture)}",
        NvidiaAdminAction.Delete => $"delete:{SettingId:X8}",
        _ => $"default:{SettingId:X8}",
    };

    public static bool TryParse(string? argument, out NvidiaAdminOp op)
    {
        op = default;
        var parts = (argument ?? string.Empty).Split(':');
        if (parts.Length < 2 || parts[1].Length != 8
            || !uint.TryParse(parts[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint id)) return false;

        switch (parts[0])
        {
            case "set" when parts.Length == 3 && uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out uint value):
                op = new NvidiaAdminOp(id, NvidiaAdminAction.Set, value);
                break;
            case "delete" when parts.Length == 2:
                op = new NvidiaAdminOp(id, NvidiaAdminAction.Delete);
                break;
            case "default" when parts.Length == 2:
                op = new NvidiaAdminOp(id, NvidiaAdminAction.RestoreDefault);
                break;
            default:
                return false;
        }
        return op.IsAllowed;
    }
}

/// <summary>
/// What an elevated step did. <paramref name="Problem"/> says why the NVIDIA part failed, for the player: the prompt was
/// declined, or the change was refused even with administrator rights.
/// </summary>
public readonly record struct ElevatedResult(bool RegistryOk, bool NvidiaOk, string? Problem = null)
{
    public static readonly ElevatedResult Nothing = new(true, true);
}

/// <summary>
/// The one administrator prompt a Performance Profile asks for. Registry changes go through Windows' own
/// <c>reg.exe</c> as before; NVIDIA settings the driver only lets an administrator change need TrayTrigger itself, so
/// when there are any, a single elevated run of TrayTrigger (<see cref="Switch"/>) does both, under the same prompt.
///
/// <para>The request is fixed before the prompt and is not read from anywhere else after it: the registry changes are in
/// the guarded temporary .reg file the registry import already used, the NVIDIA changes are arguments, and the helper
/// accepts only the <see cref="NvidiaAdminOp.Allowed"/> settings and values and a .reg file in the temp folder with
/// the name TrayTrigger gives it.</para>
/// </summary>
internal static class ElevatedHelper
{
    /// <summary>The command-line switch that makes TrayTrigger run as the elevated helper and exit.</summary>
    public const string Switch = "--elevated-apply";
    private const string RegSwitch = "--reg";
    private const string NvidiaSwitch = "--nvidia";

    // The helper's exit code: which parts failed.
    internal const int RegistryFailedBit = 1;
    internal const int NvidiaFailedBit = 2;
    internal const int BadRequestBit = 4;

    private static readonly Regex TempRegFileName = new(@"^TrayTrigger-[0-9a-f]{32}\.reg$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Makes the changes under one administrator prompt (none, when TrayTrigger is elevated already). Never throws.
    /// With no NVIDIA changes this is exactly the registry import it always was.
    /// </summary>
    public static ElevatedResult Run(IReadOnlyList<SystemTweaksService.RegFileEntry> registry, IReadOnlyList<NvidiaAdminOp> nvidia)
    {
        try
        {
            if (registry.Count == 0 && nvidia.Count == 0) return ElevatedResult.Nothing;
            if (nvidia.Any(o => !o.IsAllowed))
            {
                LoggingService.Error("Elevated", "An NVIDIA change that the elevated step does not allow was asked for; nothing was sent.");
                return new ElevatedResult(registry.Count == 0 || SystemTweaksService.ApplyHklmEntries(registry.ToList()), false, "That change is not one TrayTrigger makes.");
            }

            if (nvidia.Count == 0)
                return new ElevatedResult(SystemTweaksService.ApplyHklmEntries(registry.ToList()), true);

            // Already elevated: no prompt, and no second process to start.
            if (SystemTweaksService.IsElevated)
            {
                bool regOk = registry.Count == 0 || SystemTweaksService.ApplyHklmEntries(registry.ToList());
                bool nvOk = ApplyNvidiaOps(nvidia, new NvApiDrsBackend(), out string? error);
                return new ElevatedResult(regOk, nvOk, nvOk ? null : error);
            }

            int? exit = SystemTweaksService.RunElevatedWithRegFile(registry, regPath => BuildHelperStart(regPath, nvidia));
            if (exit is not int code)
            {
                return new ElevatedResult(registry.Count == 0, false, "The administrator prompt was declined, or the elevated step did not finish.");
            }
            bool registryOk = (code & (RegistryFailedBit | BadRequestBit)) == 0;
            bool nvidiaOk = (code & (NvidiaFailedBit | BadRequestBit)) == 0;
            return new ElevatedResult(registryOk, nvidiaOk, nvidiaOk ? null : "The NVIDIA driver refused the change, even with administrator rights.");
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Elevated", $"The elevated step failed: {ex.Message}");
            return new ElevatedResult(false, false, "The elevated step could not be run.");
        }
    }

    private static ProcessStartInfo BuildHelperStart(string? regPath, IReadOnlyList<NvidiaAdminOp> nvidia)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? throw new InvalidOperationException("TrayTrigger's own path is unknown."),
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        psi.ArgumentList.Add(Switch);
        if (regPath != null)
        {
            psi.ArgumentList.Add(RegSwitch);
            psi.ArgumentList.Add(regPath);
        }
        foreach (var op in nvidia)
        {
            psi.ArgumentList.Add(NvidiaSwitch);
            psi.ArgumentList.Add(op.ToArgument());
        }
        return psi;
    }

    /// <summary>
    /// The elevated run itself: validates what it was asked for, does it, and returns the exit code. Called at startup
    /// when <see cref="Switch"/> is on the command line, before anything else of TrayTrigger starts.
    /// </summary>
    public static int RunAsHelper(string[] args)
    {
        try
        {
            string? regPath = null;
            var ops = new List<NvidiaAdminOp>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals(RegSwitch, StringComparison.Ordinal) && i + 1 < args.Length) regPath = args[++i];
                else if (args[i].Equals(NvidiaSwitch, StringComparison.Ordinal) && i + 1 < args.Length)
                {
                    if (!NvidiaAdminOp.TryParse(args[++i], out var op))
                    {
                        LoggingService.Error("Elevated", $"The elevated step was asked for an NVIDIA change it does not allow ('{args[i]}'); nothing was done.");
                        return BadRequestBit;
                    }
                    ops.Add(op);
                }
            }

            if (!SystemTweaksService.IsElevated)
            {
                LoggingService.Error("Elevated", "The elevated step was started without administrator rights; nothing was done.");
                return BadRequestBit;
            }
            if (regPath != null && !IsOurTempRegFile(regPath))
            {
                LoggingService.Error("Elevated", "The elevated step was asked to import a file that is not one TrayTrigger made; nothing was done.");
                return BadRequestBit;
            }

            int exit = 0;
            if (regPath != null && !ImportRegFile(regPath)) exit |= RegistryFailedBit;
            if (ops.Count > 0 && !ApplyNvidiaOps(ops, new NvApiDrsBackend(), out _)) exit |= NvidiaFailedBit;
            return exit;
        }
        catch (Exception ex)
        {
            LoggingService.Error("Elevated", $"The elevated step failed: {ex.Message}", ex);
            return RegistryFailedBit | NvidiaFailedBit;
        }
    }

    /// <summary>A .reg file directly in the temp folder, named the way TrayTrigger names its own.</summary>
    internal static bool IsOurTempRegFile(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\', '/');
            return string.Equals(Path.GetDirectoryName(full), temp, StringComparison.OrdinalIgnoreCase)
                   && TempRegFileName.IsMatch(Path.GetFileName(full))
                   && File.Exists(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            LoggingService.Swallowed("Elevated", ex, "checking the path of the .reg file; it is refused");
            return false;
        }
    }

    private static bool ImportRegFile(string regPath)
    {
        // Already elevated, so this child needs no prompt of its own.
        var psi = new ProcessStartInfo { FileName = SystemExecutables.Reg, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("import");
        psi.ArgumentList.Add(regPath);
        using var proc = Process.Start(psi);
        if (proc == null) return false;
        proc.WaitForExit(120000);
        return proc.HasExited && proc.ExitCode == 0;
    }

    /// <summary>
    /// Makes the changes on the driver's Global profile in one session and one save, then reads each back through a second
    /// session, since sessions don't merge. Pure over <paramref name="drs"/>, so a fake driver can stand in for the real one.
    /// </summary>
    internal static bool ApplyNvidiaOps(IReadOnlyList<NvidiaAdminOp> ops, IDrsBackend drs, out string? error)
    {
        error = null;
        if (ops.Any(o => !o.IsAllowed)) { error = "That change is not allowed."; return false; }

        using (var session = drs.OpenSession(out error))
        {
            if (session == null) return false;
            var global = session.GetGlobalProfile(out error);
            if (global == null) return false;

            foreach (var op in ops)
            {
                bool ok = op.Action switch
                {
                    NvidiaAdminAction.Set => session.SetSetting(global, op.SettingId, op.Value, out error),
                    NvidiaAdminAction.RestoreDefault => session.RestoreSettingDefault(global, op.SettingId, out error),
                    _ => session.DeleteSetting(global, op.SettingId, out error),
                };
                if (!ok) return false;
            }
            if (!session.Save(out error)) return false;
        }

        using (var check = drs.OpenSession(out error))
        {
            var global = check?.GetGlobalProfile(out error);
            if (check == null || global == null) return false;
            foreach (var op in ops.Where(o => o.Action == NvidiaAdminAction.Set))
            {
                uint? now = check.GetSetting(global, op.SettingId, out error)?.Value;
                if (now != op.Value)
                {
                    error = $"The driver accepted the change but reads back {now?.ToString(CultureInfo.InvariantCulture) ?? "nothing"}.";
                    return false;
                }
            }
        }
        return true;
    }
}
