namespace TrayTrigger.Models;

/// <summary>
/// Per-game HDR choice - see <see cref="GameEntry.Hdr"/>. Stored as a number in games.json, so new
/// values only ever go on the end.
/// </summary>
public enum HdrMode
{
    /// <summary>Whatever the Optimized profile's Enable HDR switch says (the 1.5.1 behaviour).</summary>
    ProfileDefault,
    /// <summary>Turn Windows' HDR on for this game and back off after, whatever its profile - even Off.</summary>
    On,
    /// <summary>Leave HDR alone for this game, even when the profile would turn it on.</summary>
    Off
}

public static class HdrModes
{
    /// <summary>The choice as Edit Game and Activity &amp; History name it.</summary>
    public static string Label(HdrMode mode) => mode switch
    {
        HdrMode.On => "On for this game",
        HdrMode.Off => "Off for this game",
        _ => "Profile setting",
    };
}
