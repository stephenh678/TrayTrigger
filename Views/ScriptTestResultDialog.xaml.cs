using System.Collections.Generic;
using System.Windows;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;

namespace TrayTrigger.Views;

/// <summary>
/// Shows what a "Test Run" of a game script produced: exit code, timing, captured stdout/stderr,
/// and a warning when the real run will differ from the test (elevated, or visible so nothing is
/// captured). Read-only; the user closes it or copies the output.
/// </summary>
public partial class ScriptTestResultDialog : Window
{
    public ScriptTestResultDialog(ScriptTestReport report)
    {
        InitializeComponent();
        WindowThemeService.PrepareForFirstShow(this);
        Owner = WindowHelper.ActiveOwner();

        var r = report.Result;
        string which = report.IsPreLaunch ? "Pre-launch" : "Post-exit";

        HeadingText.Text = !r.Started ? $"{which} script could not be run"
            : r.TimedOut ? $"{which} script timed out and was stopped"
            : r.ExitCode == 0 ? $"{which} script finished with exit code 0"
            : $"{which} script exited with code {r.ExitCode}";

        var summary = new List<string> { report.ScriptPath };
        if (!r.Started && r.Error != null)
        {
            summary.Add(r.Error);
        }
        else
        {
            string phase = report.IsPreLaunch ? "prelaunch" : "postexit (playtime 0)";
            summary.Add(r.TimedOut
                ? $"Ran as {phase}; still running after {GameScriptService.TestRunTimeout.TotalSeconds:0} s, so it was killed. A real run is never killed - it keeps going while the game launches."
                : $"Ran as {phase} in {r.Elapsed.TotalSeconds:0.0} s, hidden and not elevated, with {report.ProbeDescription ?? "the name and exe currently in Edit Game"}.");
        }
        SummaryText.Text = string.Join("\n", summary);

        var notes = new List<string>();
        if (report.WillRunElevated)
        {
            notes.Add("This game runs its scripts as Administrator. The test ran without elevation so the output could be captured. The real run will show a UAC prompt, run elevated, and receive no TRAYTRIGGER_* environment variables - read the arguments instead.");
        }
        if (!report.WillRunHidden)
        {
            notes.Add("This game runs its scripts visibly. The test ran hidden so the output could be captured. The real run keeps its output in its own console window and writes nothing to the log.");
        }
        if (notes.Count > 0)
        {
            NoteText.Text = string.Join("\n\n", notes);
            NoteBorder.Visibility = Visibility.Visible;
        }

        OutputBox.Text = string.IsNullOrEmpty(r.Output) ? "(no output)" : r.Output;

        Loaded += (s, e) =>
        {
            WindowThemeService.CenterOverOwner(this);
            Activate();
        };
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(OutputBox.Text); } catch { }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
