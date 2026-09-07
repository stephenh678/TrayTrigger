using System;
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

    public static string CurrentVersionDisplay =>
        $"v{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";

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
    /// Checks GitHub for the latest release in the specified repository (e.g. "stephenh678/TrayTrigger").
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdatesAsync(string? repository)
    {
        string targetRepo = string.IsNullOrWhiteSpace(repository) ? "stephenh678/TrayTrigger" : repository.Trim();

        try
        {
            string url = $"https://api.github.com/repos/{targetRepo}/releases/latest";
            LoggingService.Info("UpdateService", $"Checking for updates at {url}");

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
            var release = JsonSerializer.Deserialize(json, AppJsonContext.Default.GitHubReleaseInfo);

            if (release == null || string.IsNullOrWhiteSpace(release.TagName))
            {
                return new UpdateCheckResult(UpdateStatus.Error, null, "Failed to parse release information from GitHub.", CurrentVersion);
            }

            var remoteVersion = release.ParsedVersion;
            if (remoteVersion != null && remoteVersion > CurrentVersion)
            {
                LoggingService.Info("UpdateService", $"New release detected: {release.TagName} (Current: {CurrentVersionDisplay})");
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

        string tempFolder = Path.Combine(Path.GetTempPath(), "TrayTriggerUpdates");
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
    /// Launches the downloaded installer and gracefully exits TrayTrigger so files can be updated.
    /// </summary>
    public static void LaunchInstallerAndExit(string installerPath)
    {
        if (!File.Exists(installerPath))
        {
            throw new FileNotFoundException("Installer executable was not found.", installerPath);
        }

        LoggingService.Info("UpdateService", $"Launching installer: {installerPath}");

        var psi = new ProcessStartInfo
        {
            FileName = installerPath,
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
