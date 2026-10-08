namespace TrayTrigger.Services;

/// <summary>
/// The NVIDIA shader cache size behind System > "NVIDIA Shader Cache: Unlimited": the Global
/// profile's "Shader disk cache maximum size", the value "Manage 3D settings > Shader Cache Size"
/// writes. Capture and undo are <see cref="NvidiaGlobalSetting"/>'s.
/// </summary>
public sealed class NvidiaShaderCacheService(IDrsBackend backend)
{
    /// <summary>PS_SHADERDISKCACHE_MAX_SIZE_ID in NVIDIA's NvApiDriverSettings.h.</summary>
    public const uint SettingId = 0x00AC8497;

    /// <summary>The value the Control Panel writes for "Unlimited".</summary>
    public const uint Unlimited = 0xFFFFFFFF;

    internal const string AbsentToken = NvidiaGlobalSetting.AbsentToken;
    internal const string PredefinedToken = NvidiaGlobalSetting.PredefinedToken;

    private readonly NvidiaGlobalSetting _setting = new(backend, SettingId);

    public NvidiaShaderCacheService() : this(new NvApiDrsBackend()) { }

    /// <summary>What the driver says now. <see cref="IsAvailable"/> false: no NVIDIA driver, or one without the setting.</summary>
    public readonly record struct State(bool IsAvailable, bool IsUnlimited, string? Error);

    public State Read()
    {
        var state = _setting.Read();
        return new State(state.IsAvailable, state.Value == Unlimited, state.Error);
    }

    /// <summary>Sets Unlimited. <paramref name="priorToken"/> is what was there first; null when nothing was written.</summary>
    public bool Apply(out string? priorToken, out string? error) => _setting.Apply(Unlimited, out priorToken, out error);

    /// <summary>Puts back what <paramref name="priorToken"/> recorded, unless the size was changed since.</summary>
    public bool Restore(string? priorToken, out string? error) => _setting.Restore(Unlimited, priorToken, out error);

    internal static (Models.DlssSettingOrigin Origin, uint Value) ParseToken(string? token) => NvidiaGlobalSetting.ParseToken(token);
}
