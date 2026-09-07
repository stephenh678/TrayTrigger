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
[JsonSerializable(typeof(GitHubReleaseAsset))]
[JsonSerializable(typeof(List<GitHubReleaseAsset>))]
[JsonSerializable(typeof(PerformanceProfileSessionSnapshot))]
[JsonSerializable(typeof(OptimizedProfileTweakConfig))]
[JsonSerializable(typeof(AggressiveProfileTweakConfig))]
[JsonSerializable(typeof(PerGameProfileSnapshot))]
[JsonSerializable(typeof(List<PerGameProfileSnapshot>))]
public partial class AppJsonContext : JsonSerializerContext
{
}
