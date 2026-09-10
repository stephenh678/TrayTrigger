using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using TrayTrigger.Services;

namespace TrayTrigger.Views;

public enum DetectedLauncher
{
    Steam,
    Gog,
    Ea,
    Epic,
    Ubisoft
}

public class DetectedLauncherOption : INotifyPropertyChanged
{
    public DetectedLauncher Launcher { get; }
    public string DisplayName { get; }
    public int GameCount { get; }
    public string SubLabel => $"{GameCount} game{(GameCount == 1 ? "" : "s")} found";

    /// <summary>Pack URI to this platform's logo under Assets/LauncherLogos.</summary>
    public string LogoUri { get; }

    private bool _isSelected = true;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }

    public DetectedLauncherOption(DetectedLauncher launcher, string displayName, int gameCount)
    {
        Launcher = launcher;
        DisplayName = displayName;
        GameCount = gameCount;
        LogoUri = $"pack://application:,,,/Assets/LauncherLogos/{LogoFileFor(launcher)}";
    }

    private static string LogoFileFor(DetectedLauncher launcher) => launcher switch
    {
        DetectedLauncher.Steam => "steam.png",
        DetectedLauncher.Gog => "gog_galaxy.png",
        DetectedLauncher.Ea => "ea_app.png",
        DetectedLauncher.Epic => "epic_games.png",
        DetectedLauncher.Ubisoft => "ubisoft_connect.png",
        _ => "steam.png"
    };

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Shown the first time the user ever presses "Scan for Games" (see
/// ImportCoordinator.ScanForGamesAsync/DetectInstalledLaunchers), and only when at least one
/// launcher's own footprint turned up a game, so the user picks which ones TrayTrigger should
/// manage instead of every platform silently defaulting to "on" - or, at the other extreme,
/// having to discover and flip on each toggle manually from Settings. A launcher that isn't
/// installed, or is but has nothing installed under it, never appears here.
/// </summary>
public partial class LauncherDetectionDialog : Window
{
    private readonly List<DetectedLauncherOption> _options;

    public bool Confirmed { get; private set; }
    public IReadOnlyList<DetectedLauncher> EnabledLaunchers =>
        _options.Where(o => o.IsSelected).Select(o => o.Launcher).ToList();

    public LauncherDetectionDialog(List<DetectedLauncherOption> options)
    {
        InitializeComponent();
        WindowThemeService.PrepareForFirstShow(this);

        _options = options;
        OptionsItemsControl.ItemsSource = _options;

        Owner = WindowHelper.ActiveOwner();

        Loaded += (s, e) =>
        {
            WindowThemeService.CenterOverOwner(this);
            Activate();
        };
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var option in _options) option.IsSelected = true;
    }

    private void DeselectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var option in _options) option.IsSelected = false;
    }

    private void EnableButton_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        DialogResult = true;
        Close();
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
        Close();
    }
}
