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
public partial class AppJsonContext : JsonSerializerContext
{
}
