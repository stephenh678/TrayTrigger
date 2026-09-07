using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Views;

public partial class UpdateDialog : Window
{
    private readonly GitHubReleaseInfo _release;
    private readonly Version _currentVersion;
    private CancellationTokenSource? _downloadCts;

    public UpdateDialog(GitHubReleaseInfo release, Version currentVersion)
    {
        InitializeComponent();

        _release = release;
        _currentVersion = currentVersion;

        CurrentVersionText.Text = $"v{currentVersion.Major}.{currentVersion.Minor}.{currentVersion.Build}";
        NewVersionText.Text = release.TagName;
        ReleaseTitleText.Text = string.IsNullOrWhiteSpace(release.Name) ? release.TagName : release.Name;

        string body = release.Body;
        if (string.IsNullOrWhiteSpace(body))
        {
            ReleaseNotesText.Text = "No detailed release notes provided for this version. Visit GitHub for commit details.";
        }
        else
        {
            ReleaseNotesText.Text = body.Trim();
        }

        var installer = release.InstallerAsset;
        var zip = release.ZipAsset;

        if (installer != null)
        {
            string sizeStr = !string.IsNullOrEmpty(installer.FormattedSize) ? $" ({installer.FormattedSize})" : string.Empty;
            InstallBtn.Content = $"Download & Install{sizeStr}";
        }
        else if (zip != null)
        {
            string sizeStr = !string.IsNullOrEmpty(zip.FormattedSize) ? $" ({zip.FormattedSize})" : string.Empty;
            InstallBtn.Content = $"Download ZIP{sizeStr}";
        }
        else
        {
            InstallBtn.Content = "Open Release on GitHub";
        }

        Loaded += (s, e) =>
        {
            WindowThemeService.ApplyDarkTitleBar(this);
            WindowThemeService.CenterOverOwner(this);
            Activate();
            InstallBtn.Focus();
        };

        Closed += (s, e) =>
        {
            _downloadCts?.Cancel();
            _downloadCts?.Dispose();
            _downloadCts = null;
        };
    }

    private void OnViewOnGitHubClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string url = !string.IsNullOrWhiteSpace(_release.HtmlUrl) ? _release.HtmlUrl : "https://github.com";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LoggingService.Error("UpdateDialog", "Failed to open GitHub release URL", ex);
        }
    }

    private void OnRemindLaterClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        var installer = _release.InstallerAsset;
        if (installer == null)
        {
            var zip = _release.ZipAsset;
            if (zip != null && !string.IsNullOrWhiteSpace(zip.BrowserDownloadUrl))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(zip.BrowserDownloadUrl) { UseShellExecute = true });
                    Close();
                    return;
                }
                catch (Exception ex)
                {
                    LoggingService.Error("UpdateDialog", "Failed to open browser download for zip asset", ex);
                }
            }

            OnViewOnGitHubClick(sender, e);
            Close();
            return;
        }

        InstallBtn.IsEnabled = false;
        RemindLaterBtn.IsEnabled = false;
        ProgressContainer.Visibility = Visibility.Visible;
        ProgressStatusText.Text = $"Downloading {installer.Name}...";
        DownloadProgressBar.Value = 0;
        ProgressPercentText.Text = "0%";

        _downloadCts = new CancellationTokenSource();

        var progress = new Progress<double>(pct =>
        {
            int percentInt = (int)(pct * 100);
            DownloadProgressBar.Value = percentInt;
            ProgressPercentText.Text = $"{percentInt}%";
        });

        try
        {
            string downloadedPath = await UpdateService.Instance.DownloadAssetAsync(
                installer,
                progress,
                _downloadCts.Token);

            ProgressStatusText.Text = "Launching installer...";
            ProgressPercentText.Text = "100%";
            DownloadProgressBar.Value = 100;

            await Task.Delay(400);

            UpdateService.LaunchInstallerAndExit(downloadedPath);
        }
        catch (OperationCanceledException)
        {
            ProgressStatusText.Text = "Download cancelled.";
            InstallBtn.IsEnabled = true;
            RemindLaterBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            LoggingService.Error("UpdateDialog", "Failed to download update installer", ex);
            ProgressStatusText.Text = "Download failed. Check your internet connection.";
            InstallBtn.Content = "Retry Download";
            InstallBtn.IsEnabled = true;
            RemindLaterBtn.IsEnabled = true;
        }
    }

    public static void ShowUpdateDialog(Window? owner, GitHubReleaseInfo release, Version currentVersion)
    {
        var activeOwner = owner ?? (Application.Current?.MainWindow is { IsVisible: true } w ? w : null);
        var dialog = new UpdateDialog(release, currentVersion);

        if (activeOwner != null)
        {
            dialog.Owner = activeOwner;
        }

        dialog.ShowDialog();
    }
}
