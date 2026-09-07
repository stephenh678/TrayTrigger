using System;
using System.Windows;
using System.Windows.Media;
using TrayTrigger.Services;

namespace TrayTrigger.Views;

public enum DialogIconType
{
    Delete,
    Question,
    Warning,
    Info,
    Power
}

public enum DialogResultOption
{
    Cancel,
    Primary,
    Secondary
}

public partial class ModernDialog : Window
{
    public bool Result { get; private set; }
    public DialogResultOption ChoiceResult { get; private set; } = DialogResultOption.Cancel;

    public ModernDialog(
        string title,
        string message,
        string detail,
        string confirmText,
        string? cancelText,
        DialogIconType iconType)
    {
        InitializeComponent();

        Title = title;
        PrimaryMessageText.Text = message;

        if (string.IsNullOrWhiteSpace(detail))
        {
            DetailMessageText.Visibility = Visibility.Collapsed;
        }
        else
        {
            DetailMessageText.Text = detail;
            DetailMessageText.Visibility = Visibility.Visible;
        }

        ConfirmBtn.Content = confirmText;

        if (string.IsNullOrEmpty(cancelText))
        {
            CancelBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            CancelBtn.Content = cancelText;
            CancelBtn.Visibility = Visibility.Visible;
        }

        ConfigureIconAndStyle(iconType);

        Loaded += (s, e) =>
        {
            WindowThemeService.ApplyDarkTitleBar(this);
            WindowThemeService.CenterOverOwner(this);
            Activate();
            if (ConfirmBtn.Visibility == Visibility.Visible)
            {
                ConfirmBtn.Focus();
            }
        };
    }

    private void ConfigureIconAndStyle(DialogIconType iconType)
    {
        switch (iconType)
        {
            case DialogIconType.Delete:
                IconBadge.Background = new SolidColorBrush(Color.FromRgb(46, 24, 27));
                IconBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(120, 29, 34));
                IconGlyph.Text = "🗑";
                if (Application.Current?.TryFindResource("ModernDangerButton") is Style dangerStyle)
                {
                    ConfirmBtn.Style = dangerStyle;
                }
                break;

            case DialogIconType.Warning:
                IconBadge.Background = new SolidColorBrush(Color.FromRgb(40, 35, 21));
                IconBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(120, 93, 28));
                IconGlyph.Text = "⚠️";
                if (Application.Current?.TryFindResource("ModernAccentButton") is Style warnStyle)
                {
                    ConfirmBtn.Style = warnStyle;
                }
                break;

            case DialogIconType.Info:
                IconBadge.Background = new SolidColorBrush(Color.FromRgb(22, 34, 46));
                IconBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(28, 75, 120));
                IconGlyph.Text = "ℹ";
                IconGlyph.Foreground = (Brush)Application.Current.FindResource("BrushAccentHover");
                if (Application.Current?.TryFindResource("ModernAccentButton") is Style infoStyle)
                {
                    ConfirmBtn.Style = infoStyle;
                }
                break;

            case DialogIconType.Power:
                IconBadge.Background = new SolidColorBrush(Color.FromRgb(46, 24, 27));
                IconBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(120, 29, 34));
                IconGlyph.Text = "\xE7E8";
                IconGlyph.FontFamily = new FontFamily("Segoe MDL2 Assets, Segoe Fluent Icons");
                IconGlyph.Foreground = new SolidColorBrush(Color.FromRgb(224, 108, 117));
                if (Application.Current?.TryFindResource("ModernDangerButton") is Style powerDangerStyle)
                {
                    ConfirmBtn.Style = powerDangerStyle;
                }
                break;

            case DialogIconType.Question:
            default:
                IconBadge.Background = new SolidColorBrush(Color.FromRgb(22, 34, 46));
                IconBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(28, 75, 120));
                IconGlyph.Text = "?";
                IconGlyph.Foreground = (Brush)Application.Current.FindResource("BrushAccentHover");
                if (Application.Current?.TryFindResource("ModernAccentButton") is Style questStyle)
                {
                    ConfirmBtn.Style = questStyle;
                }
                break;
        }
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        Result = true;
        ChoiceResult = DialogResultOption.Primary;
        DialogResult = true;
        Close();
    }

    private void OnSecondaryClick(object sender, RoutedEventArgs e)
    {
        Result = false;
        ChoiceResult = DialogResultOption.Secondary;
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Result = false;
        ChoiceResult = DialogResultOption.Cancel;
        DialogResult = false;
        Close();
    }

    public static DialogResultOption PromptExitAction(Window? owner)
    {
        var activeOwner = owner ?? (Application.Current?.MainWindow is { IsVisible: true } w ? w : null);
        var dialog = new ModernDialog(
            "Exit TrayTrigger",
            "Exit or Minimize to Tray?",
            "Would you like to minimize TrayTrigger to the system tray (keeping your hotkeys and tray menu active), or completely exit the application?",
            "Exit App",
            "Cancel",
            DialogIconType.Power);

        dialog.SecondaryBtn.Content = "Minimize to Tray";
        dialog.SecondaryBtn.Visibility = Visibility.Visible;
        if (Application.Current?.TryFindResource("ModernAccentButton") is Style accentStyle)
        {
            dialog.SecondaryBtn.Style = accentStyle;
        }

        if (activeOwner != null)
        {
            dialog.Owner = activeOwner;
        }

        dialog.ShowDialog();
        return dialog.ChoiceResult;
    }

    public static bool ConfirmDelete(
        Window? owner,
        string title,
        string message,
        string detail = "",
        string confirmText = "Remove",
        string cancelText = "Cancel")
    {
        return ShowModal(owner, title, message, detail, confirmText, cancelText, DialogIconType.Delete);
    }

    public static bool Confirm(
        Window? owner,
        string title,
        string message,
        string detail = "",
        string confirmText = "Yes",
        string cancelText = "No")
    {
        return ShowModal(owner, title, message, detail, confirmText, cancelText, DialogIconType.Question);
    }

    public static void ShowInfo(
        Window? owner,
        string title,
        string message,
        string detail = "")
    {
        ShowModal(owner, title, message, detail, "OK", null, DialogIconType.Info);
    }

    public static bool PromptRestart(Window? owner, string detail)
    {
        return Confirm(
            owner,
            "Restart Required",
            "This change needs a restart to take effect.",
            detail,
            confirmText: "Restart Now",
            cancelText: "Restart Later");
    }

    public static bool PromptSteamGridDbSetup(Window? owner)
    {
        return ShowModal(
            owner,
            "Improve Your Poster Art",
            "Steam doesn't have official box art for every game yet - new, indie, and upcoming titles often fall back to a lower-quality poster.",
            "TrayTrigger can optionally pull higher-quality vertical art from SteamGridDB.com for those games. It's free and takes a minute to set up with your own API key. You can always do this later from Settings > Library.",
            confirmText: "Set Up Now",
            cancelText: "Maybe Later",
            DialogIconType.Info);
    }

    public static void ShowWarning(
        Window? owner,
        string title,
        string message,
        string detail = "")
    {
        ShowModal(owner, title, message, detail, "OK", null, DialogIconType.Warning);
    }

    private static bool ShowModal(
        Window? owner,
        string title,
        string message,
        string detail,
        string confirmText,
        string? cancelText,
        DialogIconType iconType)
    {
        var activeOwner = owner ?? (Application.Current?.MainWindow is { IsVisible: true } w ? w : null);
        var dialog = new ModernDialog(title, message, detail, confirmText, cancelText, iconType);

        if (activeOwner != null)
        {
            dialog.Owner = activeOwner;
        }

        bool? res = dialog.ShowDialog();
        return res == true && dialog.Result;
    }
}
