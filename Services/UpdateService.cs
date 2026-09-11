using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

public enum UpdateStatus
{
    UpToDate,
    UpdateAvailable,
    NoReleasesFound,
    Error
}

public class UpdateCheckResult
{
    public UpdateStatus Status { get; }
    public GitHubReleaseInfo? LatestRelease { get; }
    public string? ErrorMessage { get; }
    public Version CurrentVersion { get; }

    public bool IsUpdateAvailable => Status == UpdateStatus.UpdateAvailable;
    public bool IsUpToDate => Status == UpdateStatus.UpToDate;

    public UpdateCheckResult(UpdateStatus status, GitHubReleaseInfo? latestRelease, string? errorMessage, Version currentVersion)
    {
        Status = status;
        LatestRelease = latestRelease;
        ErrorMessage = errorMessage;
        CurrentVersion = currentVersion;
    }
}

public class UpdateService
{
    private static readonly Lazy<UpdateService> _instance = new(() => new UpdateService());
    public static UpdateService Instance => _instance.Value;

    private readonly HttpClient _httpClient;

    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v ?? new Version(1, 0, 0);
        }
    }

    /// <summary>
    /// Full semantic version of the running build, including any pre-release suffix baked in
    /// by the CI build (e.g. "1.3.0-beta.1" from /p:Version=1.3.0-beta.1 lands in
    /// AssemblyInformationalVersion). Falls back to the numeric assembly version.
    /// </summary>
    public static SemanticVersion CurrentSemVer
    {
        get
        {
            string? info = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return SemanticVersion.TryParse(info) ?? SemanticVersion.FromVersion(CurrentVersion);
        }
    }

    public static bool IsPrereleaseBuild => CurrentSemVer.IsPrerelease;

    public static string CurrentVersionDisplay => CurrentSemVer.ToDisplayString();

    public UpdateService()
    {
        // No client-wide Timeout: HttpClient.Timeout governs the whole request including reading
        // the response body (even with ResponseHeadersRead), which would abort a slow-but-still-
        // progressing installer download. Each call below applies its own appropriately-sized
        // per-request timeout instead.
        _httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("TrayTrigger", CurrentVersionDisplay.TrimStart('v')));
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>
    /// Picks the newest installable release from a list, honouring SemVer pre-release ordering.
    /// Drafts and releases with unparsable tags are ignored; pre-releases are ignored unless
    /// <paramref name="includePrerelease"/> is set. Returns null if nothing qualifies.
    /// </summary>
    public static GitHubReleaseInfo? SelectLatest(IEnumerable<GitHubReleaseInfo> releases, bool includePrerelease)
    {
        GitHubReleaseInfo? best = null;
        SemanticVersion? bestVer = null;

        foreach (var r in releases)
        {
            if (r.Draft) continue;
            var v = r.SemVer;
            if (v == null) continue;
            if (!includePrerelease && (r.Prerelease || v.IsPrerelease)) continue;

            if (bestVer == null || v > bestVer)
            {
                best = r;
                bestVer = v;
            }
        }

        return best;
    }

    /// <summary>
    /// Checks GitHub for the latest release in the specified repository (e.g. "stephenh678/TrayTrigger").
    /// When <paramref name="includePrerelease"/> is true, the full releases list is consulted and
    /// the newest version wins even if it is flagged as a pre-release; otherwise GitHub's
    /// /releases/latest endpoint is used, which already excludes pre-releases and drafts.
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdatesAsync(string? repository, bool includePrerelease = false)
    {
        string targetRepo = string.IsNullOrWhiteSpace(repository) ? "stephenh678/TrayTrigger" : repository.Trim();

        try
        {
            string url = includePrerelease
                ? $"https://api.github.com/repos/{targetRepo}/releases?per_page=30"
                : $"https://api.github.com/repos/{targetRepo}/releases/latest";
            LoggingService.Info("UpdateService", $"Checking for updates at {url} (prerelease={includePrerelease})");

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                LoggingService.Info("UpdateService", $"No releases found for repository {targetRepo} (HTTP 404).");
                return new UpdateCheckResult(
                    UpdateStatus.NoReleasesFound,
                    null,
                    $"No GitHub releases found for '{targetRepo}'. Once published, updates will appear here.",
                    CurrentVersion);
            }

            if (!response.IsSuccessStatusCode)
            {
                string statusMsg = $"GitHub API returned {(int)response.StatusCode} {response.ReasonPhrase}";
                LoggingService.Warn("UpdateService", statusMsg);
                return new UpdateCheckResult(UpdateStatus.Error, null, statusMsg, CurrentVersion);
            }

            string json = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

            GitHubReleaseInfo? release;
            if (includePrerelease)
            {
                var list = JsonSerializer.Deserialize(json, AppJsonContext.Default.ListGitHubReleaseInfo);
                if (list == null)
                {
                    return new UpdateCheckResult(UpdateStatus.Error, null, "Failed to parse release list from GitHub.", CurrentVersion);
                }
                if (list.Count == 0)
                {
                    LoggingService.Info("UpdateService", $"Release list for {targetRepo} is empty.");
                    return new UpdateCheckResult(
                        UpdateStatus.NoReleasesFound,
                        null,
                        $"No GitHub releases found for '{targetRepo}'. Once published, updates will appear here.",
                        CurrentVersion);
                }
                release = SelectLatest(list, includePrerelease: true);
            }
            else
            {
                release = JsonSerializer.Deserialize(json, AppJsonContext.Default.GitHubReleaseInfo);
            }

            if (release == null || string.IsNullOrWhiteSpace(release.TagName))
            {
                return new UpdateCheckResult(UpdateStatus.Error, null, "Failed to parse release information from GitHub.", CurrentVersion);
            }

            var remoteVersion = release.SemVer;
            var current = CurrentSemVer;
            if (remoteVersion != null && remoteVersion > current)
            {
                LoggingService.Info("UpdateService", $"New release detected: {release.TagName} (Current: {CurrentVersionDisplay}, prerelease={release.Prerelease})");
                return new UpdateCheckResult(UpdateStatus.UpdateAvailable, release, null, CurrentVersion);
            }

            LoggingService.Info("UpdateService", $"App is up to date. Latest release on GitHub is {release.TagName}.");
            return new UpdateCheckResult(UpdateStatus.UpToDate, release, null, CurrentVersion);
        }
        catch (HttpRequestException ex)
        {
            LoggingService.Warn("UpdateService", $"Network error checking for updates: {ex.Message}");
            return new UpdateCheckResult(UpdateStatus.Error, null, "Network connection issue: Unable to reach GitHub.", CurrentVersion);
        }
        catch (TaskCanceledException)
        {
            LoggingService.Warn("UpdateService", "Update check timed out.");
            return new UpdateCheckResult(UpdateStatus.Error, null, "Connection timed out while checking for updates.", CurrentVersion);
        }
        catch (Exception ex)
        {
            LoggingService.Error("UpdateService", "Unexpected error during update check", ex);
            return new UpdateCheckResult(UpdateStatus.Error, null, $"Update check failed: {ex.Message}", CurrentVersion);
        }
    }

    /// <summary>
    /// Downloads an asset (such as an installer executable) to a temporary file, reporting progress.
    /// </summary>
    public async Task<string> DownloadAssetAsync(
        GitHubReleaseAsset asset,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
        {
            throw new InvalidOperationException("Asset download URL is empty.");
        }

        string tempFolder = DownloadFolder;
        CleanupDownloadedInstallers();
        Directory.CreateDirectory(tempFolder);

        string safeName = Path.GetFileName(asset.Name);
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "TrayTrigger-Setup.exe";
        }

        string targetPath = Path.Combine(tempFolder, safeName);

        LoggingService.Info("UpdateService", $"Downloading update asset from {asset.BrowserDownloadUrl} to {targetPath}");

        // No extra internal timeout here: the caller (UpdateDialog) already provides a real,
        // user-controlled cancellationToken wired to a visible Cancel button and progress bar, so
        // there's no need for (and real risk of harm from) a silent ceiling that could abort a
        // slow-but-still-progressing ~60MB download on a throttled connection.
        using var response = await _httpClient.GetAsync(
            asset.BrowserDownloadUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        long? totalBytes = response.Content.Headers.ContentLength ?? (asset.Size > 0 ? asset.Size : null);

        using (var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        using (var destStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            var buffer = new byte[81920];
            long totalRead = 0;
            int read;

            while ((read = await sourceStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destStream.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                totalRead += read;

                if (totalBytes.HasValue && totalBytes.Value > 0)
                {
                    double pct = (double)totalRead / totalBytes.Value;
                    progress?.Report(pct);
                }
            }
        }

        LoggingService.Info("UpdateService", $"Download completed: {targetPath}");
        return targetPath;
    }

    /// <summary>
    /// Where <see cref="DownloadAssetAsync"/> puts installers. The installer's uninstaller
    /// deletes the same folder (see setup.iss RemoveDownloadedInstallers).
    /// </summary>
    public static string DownloadFolder => Path.Combine(Path.GetTempPath(), "TrayTriggerUpdates");

    /// <summary>
    /// Best-effort removal of previously downloaded installers. Called at startup and before
    /// each new download so the folder never accumulates one ~50 MB setup per release. An
    /// installer that is still running (the one that just relaunched us) is locked and simply
    /// survives until the next pass.
    /// </summary>
    public static void CleanupDownloadedInstallers()
    {
        try
        {
            if (!Directory.Exists(DownloadFolder)) return;

            foreach (string file in Directory.EnumerateFiles(DownloadFolder))
            {
                try
                {
                    File.Delete(file);
                    LoggingService.Verbose("UpdateService", $"Removed old installer: {file}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LoggingService.Verbose("UpdateService", $"Old installer still in use, skipping: {file} ({ex.Message})");
                }
            }
        }
        catch (Exception ex)
        {
            LoggingService.Warn("UpdateService", $"Installer cleanup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Inno Setup switches for an in-app update: silent with a progress window, no reboot,
    /// no prompts, and our own /UPDATE marker that tells setup.iss to leave startup
    /// registration and settings.json alone and to relaunch the app when it finishes.
    /// </summary>
    internal const string InstallerUpdateArguments = "/SILENT /NORESTART /SP- /SUPPRESSMSGBOXES /UPDATE";

    /// <summary>
    /// Launches the downloaded installer and gracefully exits TrayTrigger so files can be updated.
    /// </summary>
    public static void LaunchInstallerAndExit(string installerPath)
    {
        if (!File.Exists(installerPath))
        {
            throw new FileNotFoundException("Installer executable was not found.", installerPath);
        }

        LoggingService.Info("UpdateService", $"Launching installer: {installerPath} {InstallerUpdateArguments}");

        var psi = new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = InstallerUpdateArguments,
            UseShellExecute = true
        };

        Process.Start(psi);

        Application.Current?.Dispatcher.Invoke(() =>
        {
            LoggingService.Info("UpdateService", "Shutting down application for update installation...");
            if (Application.Current is App app)
            {
                app.ExitApplication();
            }
            else
            {
                Application.Current.Shutdown();
            }
        });
    }
}
