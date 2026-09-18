namespace TrayTrigger.Services;

/// <summary>Shared by the Steam, SteamGridDB and RAWG clients, which buffer whole responses.</summary>
internal static class MetadataHttpLimits
{
    /// <summary>32 MB: several times the largest 2x poster, far below anything that hurts.</summary>
    public const long MaxResponseBytes = 32L * 1024 * 1024;
}
