#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;
using TrayTrigger.Views;

namespace TrayTrigger;

/// <summary>
/// --test-dialog-keys &lt;out.txt&gt;: opens each dialog modally, puts focus on a text box, a list or
/// a button, sends Esc or Enter through WPF's real input pipeline (so IsCancel / IsDefault access
/// keys and every Preview/KeyDown handler run as they do for a key press), and records whether
/// the dialog closed and with what result. Nothing is saved: the games are throwaway objects.
/// </summary>
public partial class App
{
    private sealed record DialogKeyCase(string Dialog, string FocusOn, string Keys, Func<Window> Create, Func<Window, IInputElement?> Focus, Key[] Send, Action<Window>? Prepare = null);

    private void RunDialogKeyTests(string outPath)
    {
        _skipSettingsSaveOnExit = true;
        static T Find<T>(Window w, Func<T, bool>? match = null) where T : DependencyObject =>
            FindVisualChild<T>(w, match ?? (_ => true)) ?? throw new Exception($"No {typeof(T).Name} in {w.GetType().Name}");
        static Button ButtonNamed(Window w, string content) => Find<Button>(w, b => (b.Content as string)?.Replace("_", "") == content);

        GameEntry SampleGame() => new() { Name = "Key Test Game", Category = "Action" };
        var categories = new[] { "Action", "RPG" };
        var tool = new ToolEntry { Name = "Key Test Tool", Category = "Mods", TargetPath = @"C:\Tools\missing.exe" };
        ModernDialog Confirm() => new("Remove from Library", "Remove it?", "Detail", "Remove", "Cancel", DialogIconType.Delete);

        var cases = new List<DialogKeyCase>
        {
            new("ModernDialog (confirm)", "button", "Esc", Confirm, w => ButtonNamed(w, "Remove"), new[] { Key.Escape }),
            new("ModernDialog (confirm)", "button", "Enter", Confirm, w => ButtonNamed(w, "Remove"), new[] { Key.Enter }),
            new("QuickInputDialog", "text box", "Esc", () => new QuickInputDialog("Rename", "Rename", "Name", "abc"), w => Find<TextBox>(w), new[] { Key.Escape }),
            new("QuickInputDialog", "text box", "Enter", () => new QuickInputDialog("Rename", "Rename", "Name", "abc"), w => Find<TextBox>(w), new[] { Key.Enter }),
            new("GameEditDialog, no changes", "text box", "Esc", () => new GameEditDialog(SampleGame(), categories, _iconExtractorService), w => Find<TextBox>(w), new[] { Key.Escape }),
            new("GameEditDialog, no changes", "button", "Esc", () => new GameEditDialog(SampleGame(), categories, _iconExtractorService), w => ButtonNamed(w, "Cancel"), new[] { Key.Escape }),
            new("GameEditDialog, name edited", "text box", "Esc, then Esc on the prompt", () => new GameEditDialog(SampleGame(), categories, _iconExtractorService), w => Find<TextBox>(w), new[] { Key.Escape },
                w => Find<TextBox>(w).Text = "Edited name"),
            new("GameEditDialog, name edited", "text box", "Enter", () => new GameEditDialog(SampleGame(), categories, _iconExtractorService), w => Find<TextBox>(w), new[] { Key.Enter },
                w => Find<TextBox>(w).Text = "Edited name"),
            new("GameEditDialog, category drop-down open", "combo box", "Enter", () => new GameEditDialog(SampleGame(), categories, _iconExtractorService),
                w => Find<ComboBox>(w, c => c.IsEditable), new[] { Key.Enter }, w => Find<ComboBox>(w, c => c.IsEditable).IsDropDownOpen = true),
            new("GameEditDialog, hotkey recording", "hotkey box", "Esc", () => new GameEditDialog(SampleGame(), categories, _iconExtractorService),
                w => Find<HotkeyRecorderBox>(w), new[] { Key.Escape }),
            new("GameEditDialog, hotkey recording", "hotkey box", "Enter", () => new GameEditDialog(SampleGame(), categories, _iconExtractorService),
                w => Find<HotkeyRecorderBox>(w), new[] { Key.Enter }),
            new("ToolEditDialog, no changes", "text box", "Esc", () => new ToolEditDialog(tool, categories, _iconExtractorService), w => Find<TextBox>(w), new[] { Key.Escape }),
            new("ToolEditDialog, name edited", "text box", "Esc, then Esc on the prompt", () => new ToolEditDialog(tool, categories, _iconExtractorService), w => Find<TextBox>(w), new[] { Key.Escape },
                w => Find<TextBox>(w).Text = "Edited tool"),
            new("ToolEditDialog, hotkey recording", "hotkey box", "Esc", () => new ToolEditDialog(tool, categories, _iconExtractorService),
                w => Find<HotkeyRecorderBox>(w), new[] { Key.Escape }),
            new("ToolEditDialog, hotkey recording", "hotkey box", "Enter", () => new ToolEditDialog(tool, categories, _iconExtractorService),
                w => Find<HotkeyRecorderBox>(w), new[] { Key.Enter }),
            new("ScanForGamesDialog (empty)", "list", "Esc", () => new ScanForGamesDialog(_mainViewModel, new(), new(), new(), new(), new(), new(), new(), new()), w => Find<ListBox>(w), new[] { Key.Escape }),
            new("ScanForGamesDialog (empty)", "text box", "Enter", () => new ScanForGamesDialog(_mainViewModel, new(), new(), new(), new(), new(), new(), new(), new()), w => Find<TextBox>(w), new[] { Key.Enter }),
            new("WelcomeDialog", "button", "Esc", () => new WelcomeDialog(), w => Find<Button>(w), new[] { Key.Escape }),
        };

        var report = new StringBuilder();
        report.AppendLine("| Dialog | Focus on | Keys | Outcome |");
        report.AppendLine("| :--- | :--- | :--- | :--- |");
        int index = 0;

        void Next()
        {
            if (index >= cases.Count)
            {
                File.WriteAllText(outPath, report.ToString());
                Console.WriteLine(report.ToString());
                ExitApplication();
                return;
            }
            var c = cases[index++];
            LoggingService.Info("DialogKeys", $"Case {index}: {c.Dialog} / {c.FocusOn} / {c.Keys}");
            string outcome;
            try
            {
                outcome = RunOneDialogKeyCase(c);
            }
            catch (Exception ex)
            {
                outcome = "ERROR: " + ex.Message;
            }
            LoggingService.Info("DialogKeys", $"  -> {outcome}");
            report.AppendLine($"| {c.Dialog} | {c.FocusOn} | {c.Keys} | {outcome} |");
            Dispatcher.BeginInvoke(Next, DispatcherPriority.Background);
        }
        Dispatcher.BeginInvoke(Next, DispatcherPriority.Background);
    }

    private string RunOneDialogKeyCase(DialogKeyCase c)
    {
        var dialog = c.Create();
        dialog.Owner = null;
        dialog.ShowInTaskbar = true;
        bool forced = false;
        string notes = string.Empty;
        int step = 0;
        int keyIndex = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (s, e) =>
        {
            step++;
            if (step == 1)
            {
                dialog.Activate();
                c.Prepare?.Invoke(dialog);
                var element = c.Focus(dialog);
                if (element != null) Keyboard.Focus(element);
                if (element is HotkeyRecorderBox && !((UIElement)element).IsKeyboardFocusWithin) notes += "(recorder did not take focus) ";
                if (c.Prepare != null && element is ComboBox combo) combo.IsDropDownOpen = true;
                return;
            }
            var nested = Current.Windows.OfType<ModernDialog>().FirstOrDefault(w => !ReferenceEquals(w, dialog) && w.IsVisible);
            if (nested != null)
            {
                notes += "asked \"Discard your changes?\"; Esc there kept the dialog open. ";
                nested.Activate();
                Keyboard.Focus(nested);
                Current.Dispatcher.BeginInvoke(() => SendTestKey(Key.Escape));
                return;
            }
            if (keyIndex < c.Send.Length)
            {
                // Posted, not sent from inside the tick: a key that opens a nested modal prompt
                // would otherwise hold this tick open and the timer would never fire again.
                var key = c.Send[keyIndex++];
                Current.Dispatcher.BeginInvoke(() => SendTestKey(key));
                return;
            }
            timer.Stop();
            if (dialog.IsVisible)
            {
                forced = true;
                dialog.Close();
            }
        };
        timer.Start();
        bool? result = dialog.ShowDialog();
        timer.Stop();
        return forced
            ? notes + "stayed open"
            : notes + $"closed, DialogResult={(result?.ToString() ?? "null")}";
    }

    /// <summary>Sends a key press through the input manager, as the keyboard driver would.</summary>
    private static void SendTestKey(Key key)
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        var source = (focused != null ? PresentationSource.FromDependencyObject(focused) : null)
                     ?? PresentationSource.FromVisual(Current.Windows.OfType<Window>().First(w => w.IsActive));
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        InputManager.Current.ProcessInput(args);
    }
}
#endif
