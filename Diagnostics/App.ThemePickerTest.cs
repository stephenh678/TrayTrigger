#if DEBUG
using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TrayTrigger.Services.Theming;
using TrayTrigger.ViewModels;
using TrayTrigger.Views;

namespace TrayTrigger;

/// <summary>
/// --test-theme-picker &lt;out.txt&gt;: drives the color picker the way a person does. Opens a color row's popup, moves the
/// square with the arrow keys, types a color into the box, and presses Esc, then writes what each step did to the
/// colors. The unit tests cannot do this: the picker needs the app's styles, which only a running app has. Nothing is saved.
/// </summary>
public partial class App
{
    private void RunThemePickerTest(string outPath)
    {
        _skipSettingsSaveOnExit = true;
        var report = new StringBuilder();
        bool allGood = true;
        void Check(string what, bool ok)
        {
            report.AppendLine($"{(ok ? "ok  " : "FAIL")} {what}");
            allGood &= ok;
        }
        static Color Hex(string hex) { ThemeColor.TryParse(hex, out var color); return color; }
        void Settle() => Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

        var themes = ThemeService.Instance;
        var editor = new ThemeEditorViewModel(themes.Active, themes);
        var row = new ThemeColorRowControl { DataContext = editor.Accent };
        var host = new Window { Width = 520, Height = 120, Content = row, ShowInTaskbar = false };
        host.Show();
        host.UpdateLayout();

        var original = editor.Accent.Color;
        var originalHover = editor.Palette["AccentHover"];

        row.SwatchButton.IsChecked = true;
        row.UpdateLayout();
        Settle();
        Check("the picker opens under the swatch", row.PickerPopup.IsOpen);
        Check("the keyboard goes into the square as it opens", row.Picker.Square.IsKeyboardFocused);

        void Press(Key key)
        {
            var source = PresentationSource.FromVisual(row.Picker.Square);
            row.Picker.Square.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
        }

        for (int i = 0; i < 10; i++) Press(Key.Left);
        Settle();
        Check("the arrow keys change the color", editor.Accent.Color != original);
        Check("the box shows the new color", editor.Accent.Hex == ThemeColor.Format(editor.Accent.Color));
        Check("the colors that follow it moved", editor.Palette["AccentHover"] != originalHover);
        Check("the picker's own color is the row's", row.Picker.Color == editor.Accent.Color);
        Check("the editor knows it changed", editor.HasUnsavedChanges);

        editor.Accent.Hex = "#238636";
        Settle();
        Check("typing a color moves the picker", row.Picker.Color == Hex("#238636"));

        // A real key goes to the focused square and tunnels down to it through the popup's border.
        var press = PresentationSource.FromVisual(row.Picker.Square);
        row.Picker.Square.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, press, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        Settle();
        Check("Esc closes the picker", !row.PickerPopup.IsOpen);
        Check("the color Esc left is kept", editor.Accent.Color == Hex("#238636"));

        editor.Accent.ResetCommand.Execute(null);
        Settle();
        Check("the original color can be put back", editor.Accent.Color == original && !editor.HasUnsavedChanges);

        host.Close();
        report.AppendLine(allGood ? "All steps worked." : "Something did not work.");
        File.WriteAllText(outPath, report.ToString());
        Console.WriteLine(report.ToString());
        ExitApplication();
    }
}
#endif
