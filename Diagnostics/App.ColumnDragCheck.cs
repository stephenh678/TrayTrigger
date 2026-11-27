#if DEBUG
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TrayTrigger.ViewModels;
using TrayTrigger.Views;

namespace TrayTrigger;

/// <summary>
/// --test-column-drag &lt;out.txt&gt;: drags the Details List's column handles the way Windows feeds a mouse drag to a
/// Thumb (each step's change is the mouse's distance from where the handle now is), and lists, per step, where the
/// mouse is and where the column's right edge ended up. In an Explorer-style resize the edge follows the mouse until
/// the column hits a limit. Then narrows the window and checks the Play buttons are still inside it. Settings are
/// put back as they were.
/// </summary>
public partial class App
{
    private void RunColumnDragCheck(string outPath)
    {
        _skipSettingsSaveOnExit = true;
        var report = new StringBuilder();
        string settingsFile = Path.Combine(_storageService.BaseDirectory, "settings.json");
        var settingsBefore = new[] { settingsFile, settingsFile + ".bak" }.ToDictionary(f => f, f => File.Exists(f) ? File.ReadAllBytes(f) : null);
        var window = _mainWindow!;
        var main = _mainViewModel!;
        try
        {
            window.WindowState = WindowState.Normal;
            window.Show();
            window.Width = 1400;
            window.Height = 860;
            main.CurrentSection = NavSection.Library;
            main.SettingsVM.LibraryViewMode = SettingsViewModel.ViewModeDetailsList;
            DetailsListColumns.Instance.Reset();
            Settle(window);

            var page = (LibraryView)window.FindName("LibraryPage");
            var header = (Grid)page.FindName("DetailsHeaderGrid");
            Thumb Grip(string column) => Descendants(header).OfType<Thumb>().First(t => (t.Tag as string) == column);
            double EdgeOf(Thumb grip) => grip.TranslatePoint(new Point(grip.ActualWidth / 2, 0), window).X;

            void Drag(string column, double step, int steps)
            {
                var grip = Grip(column);
                double start = EdgeOf(grip);
                report.AppendLine($"## {column}: drag {(step > 0 ? "right" : "left")} {Math.Abs(step) * steps:0} from edge {start:0}");
                grip.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                for (int i = 1; i <= steps; i++)
                {
                    double mouse = start + step * i;
                    double change = mouse - EdgeOf(grip);
                    grip.RaiseEvent(new DragDeltaEventArgs(change, 0) { RoutedEvent = Thumb.DragDeltaEvent });
                    Settle(window);
                    double edge = EdgeOf(grip);
                    report.AppendLine($"  mouse {mouse,6:0}  edge {edge,6:0}  off by {edge - mouse,5:0}  width {DetailsListColumns.Instance.Width(column),5:0}" +
                                      $"  title {header.ColumnDefinitions[1].ActualWidth,5:0}  spare {header.ColumnDefinitions[7].ActualWidth,5:0}");
                }
                grip.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            }

            Drag(DetailsListColumns.Category, 20, 6);
            Drag(DetailsListColumns.Category, -20, 4);
            Drag(DetailsListColumns.Title, -25, 6);
            Drag(DetailsListColumns.Size, 30, 5);
            Drag(DetailsListColumns.Title, 40, 12);
            Drag(DetailsListColumns.Size, -30, 3);

            foreach (double width in new[] { 1100.0, 900.0, 760.0 })
            {
                window.Width = width;
                Settle(window);
                double actionsRight = header.TranslatePoint(new Point(header.ActualWidth, 0), window).X;
                report.AppendLine($"## window {width:0}: Library page {page.ActualWidth:0} wide; Actions column ends at {actionsRight:0} of {window.ActualWidth:0}" +
                                  $" ({(actionsRight <= window.ActualWidth ? "inside" : "OUTSIDE")}), title {header.ColumnDefinitions[1].ActualWidth:0}");
            }
            DetailsListColumns.Instance.Reset();
            File.WriteAllText(outPath, report.ToString());
        }
        catch (Exception ex)
        {
            File.WriteAllText(outPath, report + Environment.NewLine + "FAILED: " + ex);
        }
        finally
        {
            foreach (var (file, bytes) in settingsBefore)
            {
                try
                {
                    if (bytes != null) File.WriteAllBytes(file, bytes);
                    else File.Delete(file);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Could not put {file} back as it was: {ex.Message}");
                }
            }
        }
        ExitApplication();
    }
}
#endif
