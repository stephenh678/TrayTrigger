using System.Windows;
using System.Windows.Input;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Views;

/// <summary>
/// One-time welcome shown the first time the window is actually displayed on a fresh install -
/// see MainWindow.MaybeShowWelcomePrompt. Styled after the in-app Help window (icon badge,
/// breadcrumb, title) since both are "here's what TrayTrigger is" framing.
///
/// <para>Two steps. Step 1 asks for the optional SteamGridDB and RAWG keys, first because posters
/// and game info are fetched as each game is imported: a key added after the first scan used to
/// leave that scan's games without them. It binds to <see cref="SettingsViewModel"/>, so a key
/// typed here saves, switches its source on and is checked exactly as in Settings. Step 2 is the
/// short "what TrayTrigger does" and Scan for Games.</para>
/// </summary>
public partial class WelcomeDialog : Window
{
    /// <param name="settings">Null only for the dev captures that build the dialog with no app
    /// behind it; the key boxes are then inert.</param>
    public WelcomeDialog(SettingsViewModel? settings = null)
    {
        InitializeComponent();
        WindowThemeService.PrepareForFirstShow(this);
        DataContext = settings;

        Loaded += (s, e) =>
        {
            WindowThemeService.CenterOverOwner(this);
            Activate();
            SteamGridDbKeyBox.Focus();
        };
    }

    /// <summary>True when the user chose "Scan for Games" - the caller runs the scan after
    /// this dialog closes (see MainWindow.MaybeShowWelcomePrompt).</summary>
    public bool ScanRequested { get; private set; }

    /// <summary>Opens straight on step 2, for the dev capture of that step.</summary>
    internal void ShowGamesStep() => ShowStep(keys: false);

    private void ShowStep(bool keys)
    {
        KeysStep.Visibility = keys ? Visibility.Visible : Visibility.Collapsed;
        GamesStep.Visibility = keys ? Visibility.Collapsed : Visibility.Visible;
        StepText.Text = keys ? "GETTING STARTED · STEP 1 OF 2" : "GETTING STARTED · STEP 2 OF 2";
        // Enter means the step's main action; a default button left on the hidden step would
        // still answer it.
        NextButton.IsDefault = keys;
        ScanButton.IsDefault = !keys;
        (keys ? NextButton : ScanButton).Focus();
    }

    // Esc closes from either step, as the old single-step dialog's Skip did. Handled here rather
    // than with IsCancel because only one step's buttons are on screen at a time.
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel settings)
        {
            LoggingService.Info("Welcome", $"Keys step done (SteamGridDB key: {settings.SteamGridDbApiKeyOrNull != null}, RAWG key: {settings.RawgApiKeyOrNull != null}).");
        }
        ShowStep(keys: false);
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => ShowStep(keys: true);

    private void OnSkipClick(object sender, RoutedEventArgs e) => Close();

    private void OnScanClick(object sender, RoutedEventArgs e)
    {
        ScanRequested = true;
        Close();
    }
}
