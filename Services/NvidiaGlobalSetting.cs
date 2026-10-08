using System;
using System.Globalization;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// One DWORD on the NVIDIA driver's Global profile - the layer every game inherits unless its own
/// profile says otherwise, and what "Manage 3D settings > Global Settings" writes. Used by the
/// System page's shader cache tweak (permanent); the Optimized and Aggressive profiles' NVIDIA
/// tweaks share its tokens and its restore step, batched into one session.
///
/// <para>Undo follows the DLSS override's rule (docs/dlss-plan.md): put back what was captured, and
/// only while the driver still reports the value TrayTrigger wrote. A value someone changed since -
/// NVIDIA App, the Control Panel, Profile Inspector - is theirs, and is left alone.</para>
///
/// <para>What was there travels as a short token, so it can sit in a settings dictionary or the
/// crash-recovery snapshot: "absent", "predefined" or "user:&lt;value&gt;". The snapshot is
/// user-writable, so <see cref="ParseToken"/> reads anything else as "absent".</para>
/// </summary>
public sealed class NvidiaGlobalSetting(IDrsBackend backend, uint settingId)
{
    /// <summary>
    /// A setting every NVIDIA driver names ("Power management mode"). The driver won't name its
    /// hidden settings - the Resizable BAR ones among them - so this one answering is what shows
    /// there is an NVIDIA driver to ask.
    /// </summary>
    internal const uint PresenceProbeId = PerformanceProfileService.NvPowerModeSettingId;

    internal const string AbsentToken = "absent";
    internal const string PredefinedToken = "predefined";
    private const string UserPrefix = "user:";

    private readonly IDrsBackend _backend = backend;

    public uint SettingId { get; } = settingId;

    /// <summary>
    /// What the Global profile holds. <see cref="IsAvailable"/> false: no NVIDIA driver, or one
    /// without this setting, or the read failed (then <see cref="Error"/> says why).
    /// <see cref="Reading"/> null: nothing of its own on the Global profile.
    /// </summary>
    public readonly record struct State(bool IsAvailable, DrsSettingReading? Reading, string? Error)
    {
        public uint? Value => Reading?.Value;
    }

    /// <summary>Session-free check first, so a PC without an NVIDIA driver answers without loading anything.</summary>
    public bool DriverHasSetting => _backend.GetSettingName(SettingId) != null;

    public State Read()
    {
        if (!DriverHasSetting) return new State(false, null, null);

        using var session = _backend.OpenSession(out string? error);
        if (session == null) return new State(false, null, error);

        var global = session.GetGlobalProfile(out error);
        if (global == null) return new State(false, null, error);

        var reading = session.GetSetting(global, SettingId, out error);
        return error != null ? new State(false, null, error) : new State(true, reading, null);
    }

    /// <summary>
    /// Writes <paramref name="value"/> to the Global profile and checks it landed.
    /// <paramref name="priorToken"/> is what was there first, for <see cref="Restore"/>, and is set
    /// as soon as the driver has saved the write - even when the check afterwards fails, since the
    /// value may well be in place and will need putting back. Null when nothing was saved.
    /// </summary>
    public bool Apply(uint value, out string? priorToken, out string? error)
    {
        priorToken = null;
        using (var session = _backend.OpenSession(out error))
        {
            if (session == null) return false;
            var global = session.GetGlobalProfile(out error);
            if (global == null) return false;

            var before = session.GetSetting(global, SettingId, out error);
            // A failed read is not "absent": recording it as such would make undo delete a value
            // the user had chosen.
            if (error != null) return false;

            if (!session.SetSetting(global, SettingId, value, out error)) return false;
            if (!session.Save(out error)) return false;
            priorToken = TokenFor(before);
        }

        // A second session: NVIDIA's DRS sessions don't merge, so the one that wrote would only
        // read back its own copy.
        var after = Read();
        if (after.Value == value) return true;
        error = after.Error ?? "The driver accepted the change but still reports the old value.";
        return false;
    }

    /// <summary>
    /// Puts back what <paramref name="priorToken"/> recorded, while the Global profile still holds
    /// <paramref name="writtenValue"/>. Null token (no record) means the driver's own default.
    /// A value changed since is left alone and counts as success: there is nothing of ours left.
    /// </summary>
    public bool Restore(uint writtenValue, string? priorToken, out string? error)
    {
        using var session = _backend.OpenSession(out error);
        if (session == null) return false;
        var global = session.GetGlobalProfile(out error);
        if (global == null) return false;

        return RestoreIn(session, global, SettingId, writtenValue, priorToken, out error) switch
        {
            RestoreResult.LeftAlone => true,
            RestoreResult.PutBack => session.Save(out error),
            _ => false,
        };
    }

    public enum RestoreResult
    {
        /// <summary>Changed in the session; the caller saves.</summary>
        PutBack,
        /// <summary>The driver no longer holds what was written: someone changed it since. Nothing to do.</summary>
        LeftAlone,
        /// <summary>The read or the change failed; the error says why.</summary>
        Failed,
    }

    /// <summary>
    /// The restore step inside a session the caller opened and will save, so several settings can
    /// go back with one load and one save.
    /// </summary>
    internal static RestoreResult RestoreIn(IDrsSession session, DrsProfileHandle global, uint settingId, uint writtenValue, string? priorToken, out string? error)
    {
        var now = session.GetSetting(global, settingId, out error);
        if (error != null) return RestoreResult.Failed;
        if (now?.Value != writtenValue)
        {
            LoggingService.Info("NvidiaSettings", $"Global setting 0x{settingId:X8} was changed outside TrayTrigger since it was set to {writtenValue}; left as it is.");
            return RestoreResult.LeftAlone;
        }

        bool ok = ParseToken(priorToken) switch
        {
            (DlssSettingOrigin.UserSet, uint value) => session.SetSetting(global, settingId, value, out error),
            (DlssSettingOrigin.Predefined, _) => session.RestoreSettingDefault(global, settingId, out error),
            _ => session.DeleteSetting(global, settingId, out error),
        };
        return ok ? RestoreResult.PutBack : RestoreResult.Failed;
    }

    internal static string TokenFor(DrsSettingReading? reading) => reading?.Origin switch
    {
        DlssSettingOrigin.UserSet => UserPrefix + reading.Value.ToString(CultureInfo.InvariantCulture),
        DlssSettingOrigin.Predefined => PredefinedToken,
        // Inherited or absent: nothing of its own on the Global profile, so undo removes ours.
        _ => AbsentToken,
    };

    internal static (DlssSettingOrigin Origin, uint Value) ParseToken(string? token)
    {
        if (token == PredefinedToken) return (DlssSettingOrigin.Predefined, 0);
        if (token != null && token.StartsWith(UserPrefix, StringComparison.Ordinal)
            && uint.TryParse(token.AsSpan(UserPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out uint value))
        {
            return (DlssSettingOrigin.UserSet, value);
        }
        return (DlssSettingOrigin.Absent, 0);
    }
}
