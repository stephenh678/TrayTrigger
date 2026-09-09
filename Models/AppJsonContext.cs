using System.Collections.Generic;
using System.Text.Json.Serialization;
using TrayTrigger.Models;

namespace TrayTrigger.Models;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<GameEntry>))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(GitHubReleaseInfo))]
[JsonSerializable(typeof(List<GitHubReleaseInfo>))]
[JsonSerializable(typeof(GitHubReleaseAsset))]
[JsonSerializable(typeof(List<GitHubReleaseAsset>))]
[JsonSerializable(typeof(PerformanceProfileSessionSnapshot))]
[JsonSerializable(typeof(OptimizedProfileTweakConfig))]
[JsonSerializable(typeof(AggressiveProfileTweakConfig))]
[JsonSerializable(typeof(PerGameProfileSnapshot))]
[JsonSerializable(typeof(List<PerGameProfileSnapshot>))]
[JsonSerializable(typeof(HdrDisplaySnapshot))]
[JsonSerializable(typeof(List<HdrDisplaySnapshot>))]
[JsonSerializable(typeof(ScanLocation))]
[JsonSerializable(typeof(List<ScanLocation>))]
[JsonSerializable(typeof(IgnoredGamePath))]
[JsonSerializable(typeof(List<IgnoredGamePath>))]
public partial class AppJsonContext : JsonSerializerContext
{
}
