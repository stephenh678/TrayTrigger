using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
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
/// What an elevated step did. <paramref name="Problem"/> says why the NVIDIA part failed, for the player.
/// <paramref name="Unfinished"/>: the elevated step started but did not finish in time, so what it did is not known and it may
/// still make its changes; a caller must keep whatever record it needs to put them back.
/// </summary>
public readonly record struct ElevatedResult(bool RegistryOk, bool NvidiaOk, string? Problem = null, bool Unfinished = false)
{
    public static readonly ElevatedResult Nothing = new(true, true);
}

/// <summary>How an elevated process ended: not started (the prompt was declined, or it could not be started), started but still running, or its exit code.</summary>
internal readonly record struct ElevatedExit(bool Started, int? Code);

/// <summary>
/// The one administrator prompt a Performance Profile asks for. Registry changes go through Windows' own
/// <c>reg.exe</c> as before; NVIDIA settings the driver only lets an administrator change need TrayTrigger itself, so
/// when there are any, a single elevated run of TrayTrigger (<see cref="Switch"/>) does both, under the same prompt.
///
/// <para>The request is fixed before the prompt and is not read from anywhere else after it. The NVIDIA changes are
/// arguments; the registry changes are in the guarded temporary .reg file the registry import already used. The helper
/// accepts only the <see cref="NvidiaAdminOp.Allowed"/> NVIDIA settings and values, and reads the .reg file once,
/// checks every line of it against the two Windows scheduler values a profile writes (<see cref="TryReadRegistryChanges"/>),
/// and applies exactly what it read: the file's name and place decide nothing.</para>
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

    /// <summary>A .reg file of the two values is a few hundred bytes; anything near this size is not one.</summary>
    private const long MaxRegFileBytes = 16 * 1024;

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

            var run = SystemTweaksService.RunElevatedWithRegFile(registry, regPath => BuildHelperStart(regPath, nvidia));
            return Interpret(registry.Count, run);
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Elevated", $"The elevated step failed: {ex.Message}");
            return new ElevatedResult(false, false, "The elevated step could not be run.");
        }
    }

    /// <summary>What an elevated run's end means for the registry part and the NVIDIA part, in words a player can read.</summary>
    internal static ElevatedResult Interpret(int registryCount, ElevatedExit run)
    {
        if (!run.Started)
            return new ElevatedResult(registryCount == 0, false, "The administrator prompt was declined, or the elevated step could not be started.");
        if (run.Code is not int code)
        {
            // Started and still running when the wait ended: it may yet make its changes, so nothing is known.
            return new ElevatedResult(registryCount == 0, false,
                "The elevated step did not finish in time, so it is not known what it changed.", Unfinished: true);
        }

        bool registryOk = (code & (RegistryFailedBit | BadRequestBit)) == 0;
        bool nvidiaOk = (code & (NvidiaFailedBit | BadRequestBit)) == 0;
        string? problem = nvidiaOk ? null
            : (code & BadRequestBit) != 0 ? "The elevated step refused the request; the log says why."
            : "The NVIDIA driver refused the change, even with administrator rights.";
        return new ElevatedResult(registryOk, nvidiaOk, problem);
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

            // Read and checked before anything is changed, so a request that is not entirely what TrayTrigger asks for does nothing at all.
            List<SystemTweaksService.RegFileEntry> registry = [];
            if (regPath != null && !TryReadRegFile(regPath, out registry, out string? why))
            {
                LoggingService.Error("Elevated", $"The elevated step was asked to import a file it does not accept ({why}); nothing was done.");
                return BadRequestBit;
            }

            int exit = 0;
            if (registry.Count > 0 && !SystemTweaksService.ApplyHklmEntries(registry)) exit |= RegistryFailedBit;
            if (ops.Count > 0 && !ApplyNvidiaOps(ops, new NvApiDrsBackend(), out _)) exit |= NvidiaFailedBit;
            return exit;
        }
        catch (Exception ex)
        {
            LoggingService.Error("Elevated", $"The elevated step failed: {ex.Message}", ex);
            return RegistryFailedBit | NvidiaFailedBit;
        }
    }

    /// <summary>
    /// Reads the .reg file the elevated step was pointed at, once, and returns what it holds if every line is one the step accepts.
    /// The name is TrayTrigger's own and the file is small; neither says what is in it, so the content is what is checked.
    /// </summary>
    internal static bool TryReadRegFile(string path, out List<SystemTweaksService.RegFileEntry> entries, out string? error)
    {
        entries = [];
        error = null;
        try
        {
            string full = Path.GetFullPath(path);
            if (!TempRegFileName.IsMatch(Path.GetFileName(full))) { error = "not named the way TrayTrigger names its files"; return false; }

            // Shared for reading only, as the guard TrayTrigger holds on it is: nothing can change it while it is read.
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxRegFileBytes) { error = "too large"; return false; }
            using var reader = new StreamReader(stream, Encoding.Unicode, detectEncodingFromByteOrderMarks: true);
            return TryReadRegistryChanges(reader.ReadToEnd(), out entries, out error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            LoggingService.Swallowed("Elevated", ex, "reading the .reg file; the step refuses it");
            error = "could not be read";
            return false;
        }
    }

    /// <summary>
    /// The registry changes a profile makes in the elevated step: two Windows scheduler values, each with the values Windows defines and
    /// nothing else. A line that is anything other than the file header, a blank line, one of the two keys or one of the two values
    /// makes the whole file refused, and nothing is applied.
    /// </summary>
    internal static bool TryReadRegistryChanges(string content, out List<SystemTweaksService.RegFileEntry> entries, out string? error)
    {
        entries = [];
        error = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = content.Split("\r\n");
        if (lines.Length == 0 || lines[0] != "Windows Registry Editor Version 5.00") { error = "not a registry file TrayTrigger writes"; return false; }

        const string KeyPrefix = @"[HKEY_LOCAL_MACHINE\";
        string? key = null;
        foreach (string line in lines.Skip(1))
        {
            if (line.Length == 0) continue;
            if (line.StartsWith(KeyPrefix, StringComparison.Ordinal) && line.EndsWith(']'))
            {
                string candidate = line[KeyPrefix.Length..^1];
                key = KnownKey(candidate);
                if (key == null) { entries = []; error = $"a key it does not change ({candidate})"; return false; }
                continue;
            }

            var m = RegistryLine.Match(line);
            if (key == null || !m.Success) { entries = []; error = "a line that is not one of its own"; return false; }
            string name = m.Groups["name"].Value;
            string value = m.Groups["value"].Value;

            SystemTweaksService.RegFileEntry entry;
            if (key == PerformanceProfileService.SystemResponsivenessPath && name == "SystemResponsiveness" && value.StartsWith("dword:", StringComparison.Ordinal))
            {
                int number = Convert.ToInt32(value["dword:".Length..], 16);
                if (!ProfileSnapshotValidator.IsValidSystemResponsiveness(number)) { entries = []; error = $"a System Responsiveness of {number}"; return false; }
                entry = new SystemTweaksService.RegFileEntry(key, name, number, RegistryValueKind.DWord, Delete: false);
            }
            else if (key == SystemTweaksService.MmcssGamesTaskPath && name == "Scheduling Category" && value.StartsWith('"'))
            {
                string category = value[1..^1];
                if (!ProfileSnapshotValidator.IsValidSchedulingCategory(category)) { entries = []; error = $"a Scheduling Category of '{category}'"; return false; }
                entry = new SystemTweaksService.RegFileEntry(key, name, category, RegistryValueKind.String, Delete: false);
            }
            else
            {
                entries = [];
                error = $"a value it does not change ({name})";
                return false;
            }

            if (!seen.Add(key + "|" + name)) { entries = []; error = $"{name} twice"; return false; }
            entries.Add(entry);
        }
        return true;
    }

    /// <summary>One value line of a .reg file as TrayTrigger writes it: a plain name, and a number or a plain quoted word.</summary>
    private static readonly Regex RegistryLine = new(
        "^\"(?<name>[A-Za-z ]+)\"=(?<value>dword:[0-9a-f]{8}|\"[A-Za-z ]*\")$", RegexOptions.CultureInvariant);

    private static string? KnownKey(string candidate) =>
        string.Equals(candidate, PerformanceProfileService.SystemResponsivenessPath, StringComparison.OrdinalIgnoreCase) ? PerformanceProfileService.SystemResponsivenessPath
        : string.Equals(candidate, SystemTweaksService.MmcssGamesTaskPath, StringComparison.OrdinalIgnoreCase) ? SystemTweaksService.MmcssGamesTaskPath
        : null;

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
