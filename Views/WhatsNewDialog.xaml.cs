using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using TrayTrigger.Services;

namespace TrayTrigger.Views;

/// <summary>
/// "What's new in x.y.z", shown once after an update (see MainWindow.MaybeShowWhatsNew) and on
/// demand from the About page. The text is the running version's WhatsNew.md section.
/// </summary>
public partial class WhatsNewDialog : Window
{
    private const string ReleasesUrl = "https://github.com/stephenh678/TrayTrigger/releases";

    private static readonly Regex Bullet = new(@"^\s*[-*]\s+\*\*(.+?)\*\*\s*-?\s*(.*)$", RegexOptions.Compiled);

    private WhatsNewDialog(string key, string notes, bool afterUpdate)
    {
        InitializeComponent();
        WindowThemeService.PrepareForFirstShow(this);

        TitleText.Text = $"What's new in {key}";
        SubtitleText.Text = $"You're running {UpdateService.CurrentVersionDisplay}";
        KickerText.Visibility = afterUpdate ? Visibility.Visible : Visibility.Collapsed;
        FillNotes(notes);

        Loaded += (s, e) =>
        {
            WindowThemeService.CenterOverOwner(this);
            Activate();
        };
    }

    /// <summary>Each "- **Title** - text" bullet as a bold title with its sentence under it.</summary>
    private void FillNotes(string notes)
    {
        var titleBrush = (Brush)FindResource("BrushTextPrimary");
        bool first = true;
        foreach (string line in notes.Split('\n'))
        {
            var m = Bullet.Match(line.TrimEnd('\r'));
            if (!m.Success) continue;
            if (!first) NotesText.Inlines.Add(new LineBreak());
            first = false;
            NotesText.Inlines.Add(new Run(m.Groups[1].Value) { FontWeight = FontWeights.SemiBold, Foreground = titleBrush });
            NotesText.Inlines.Add(new LineBreak());
            NotesText.Inlines.Add(new Run(m.Groups[2].Value));
            NotesText.Inlines.Add(new LineBreak());
        }
    }

    private void OnGitHubClick(object sender, RoutedEventArgs e) => HelpCommands.OpenUrl.Execute(ReleasesUrl);

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>Shows the running version's notes. False when the notes file has no section for it.</summary>
    public static bool ShowFor(Window? owner, bool afterUpdate)
    {
        string key = WhatsNewService.StableKey(UpdateService.CurrentSemVer);
        if (WhatsNewService.LoadSection(key) is not { } notes) return false;

        var dialog = new WhatsNewDialog(key, notes, afterUpdate);
        if ((owner ?? WindowHelper.ActiveOwner()) is { } activeOwner) dialog.Owner = activeOwner;
        dialog.ShowDialog();
        return true;
    }
}
