using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TrayTrigger.Models;

/// <summary>
/// Model representing a GitHub release asset (e.g. installer or portable zip).
/// </summary>
public class GitHubReleaseAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("content_type")]
    public string ContentType { get; set; } = string.Empty;

    [JsonIgnore]
    public string FormattedSize
    {
        get
        {
            if (Size <= 0) return string.Empty;
            double mb = Size / (1024.0 * 1024.0);
            return $"{mb:F1} MB";
        }
    }
}

/// <summary>
/// Model representing GitHub Release information retrieved from api.github.com/repos/{owner}/{repo}/releases/latest.
/// </summary>
public class GitHubReleaseInfo
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;

    [JsonPropertyName("published_at")]
    public DateTime? PublishedAt { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubReleaseAsset> Assets { get; set; } = new();

    /// <summary>
    /// Full semantic version parsed from TagName, including any pre-release suffix
    /// (e.g. "v1.3.0-beta.1"). Use this for ordering releases; see <see cref="SemanticVersion"/>.
    /// </summary>
    [JsonIgnore]
    public SemanticVersion? SemVer => SemanticVersion.TryParse(TagName);

    /// <summary>
    /// Parses the numeric version from TagName (stripping any leading 'v' or 'V' and any
    /// pre-release suffix). Returns null if unable to parse. Prefer <see cref="SemVer"/> for
    /// comparisons, since this deliberately treats "1.3.0-beta.1" as equal to "1.3.0".
    /// </summary>
    [JsonIgnore]
    public Version? ParsedVersion
    {
        get
        {
            if (string.IsNullOrWhiteSpace(TagName)) return null;

            string clean = TagName.Trim().TrimStart('v', 'V');
            // Remove any commit or prerelease suffix (e.g. "1.0.1-beta" -> "1.0.1")
            int hyphen = clean.IndexOf('-');
            if (hyphen > 0)
            {
                clean = clean.Substring(0, hyphen);
            }

            // Normalise semver: e.g. "1.0" -> "1.0.0"
            string[] parts = clean.Split('.');
            if (parts.Length == 1 && int.TryParse(parts[0], out int major))
            {
                return new Version(major, 0, 0);
            }
            if (parts.Length == 2 && int.TryParse(parts[0], out major) && int.TryParse(parts[1], out int minor))
            {
                return new Version(major, minor, 0);
            }

            if (Version.TryParse(clean, out var version))
            {
                return version;
            }

            return null;
        }
    }

    /// <summary>
    /// Finds the setup executable asset if available.
    /// Prioritizes files ending with "setup.exe", then any ".exe".
    /// </summary>
    [JsonIgnore]
    public GitHubReleaseAsset? InstallerAsset
    {
        get
        {
            if (Assets == null || Assets.Count == 0) return null;

            // First search for *setup*.exe
            foreach (var a in Assets)
            {
                if (a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                    a.Name.Contains("setup", StringComparison.OrdinalIgnoreCase))
                {
                    return a;
                }
            }

            // Fallback to any .exe asset
            foreach (var a in Assets)
            {
                if (a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    return a;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Finds the portable zip asset if available.
    /// </summary>
    /// <summary>Name of the checksum manifest the release workflow publishes next to the installer.</summary>
    public const string ChecksumsAssetName = "SHA256SUMS.txt";

    /// <summary>
    /// The sha256sum-style manifest ("&lt;hash&gt;  &lt;file&gt;" per line) that
    /// <c>release.yml</c> generates for every asset. The updater refuses to launch an installer
    /// it can't verify against this.
    /// </summary>
    [JsonIgnore]
    public GitHubReleaseAsset? ChecksumsAsset
    {
        get
        {
            if (Assets == null || Assets.Count == 0) return null;

            foreach (var a in Assets)
            {
                if (a.Name.Equals(ChecksumsAssetName, StringComparison.OrdinalIgnoreCase))
                {
                    return a;
                }
            }

            return null;
        }
    }

    [JsonIgnore]
    public GitHubReleaseAsset? ZipAsset
    {
        get
        {
            if (Assets == null || Assets.Count == 0) return null;

            foreach (var a in Assets)
            {
                if (a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    return a;
                }
            }

            return null;
        }
    }
}
