using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TrayTrigger.Models;

/// <summary>
/// Source-generated serializer metadata for every JSON file TrayTrigger reads or writes. Only the
/// root types are listed; the generator emits the nested ones (GameEntry, ScanLocation,
/// GitHubReleaseAsset, the tweak configs and snapshots, SteamNewsItem, ...) from these.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<GameEntry>))]
[JsonSerializable(typeof(List<ToolEntry>))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(GitHubReleaseInfo))]
[JsonSerializable(typeof(List<GitHubReleaseInfo>))]
[JsonSerializable(typeof(PerformanceProfileSessionSnapshot))]
[JsonSerializable(typeof(Dictionary<string, SteamAppDetails>))]
[JsonSerializable(typeof(Dictionary<int, RawgGameDetails>))]
public partial class AppJsonContext : JsonSerializerContext
{
}
