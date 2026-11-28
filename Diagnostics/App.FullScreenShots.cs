#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using TrayTrigger.ViewModels;
using TrayTrigger.Views;

namespace TrayTrigger;

/// <summary>
/// --screenshot-fullscreen &lt;out-dir&gt; ["game name"] [WxH]: the full-screen Library as a TV shows it, for the site and the
/// walkthrough. The window is the full-screen one (no frame, the bigger posters and text) at WxH screen pixels (default
/// 1920x1080, a 1080p TV) in the top-left corner, with the demonstration collections the Library hero image uses (never
/// saved). Saves fullscreen.png, then opens Game Details for the named game (default the first in view) and saves
/// fullscreen-details.png once its details and banner have loaded. Run it on a copy of a library (TRAYTRIGGER_CAPTURE_DATA):
/// opening Game Details can refresh the game's details.
/// </summary>
public partial class App
{
    private void RunFullScreenShots(string outDir, string? gameName, string? size)
    {
        _skipSettingsSaveOnExit = true;
        Directory.CreateDirectory(outDir);
        var window = _mainWindow!;
        var main = _mainViewModel!;
        int pxW = 1920, pxH = 1080;
        if (size?.Split('x') is [var w, var h] && int.TryParse(w, out int pw) && int.TryParse(h, out int ph)) (pxW, pxH) = (pw, ph);
        try
        {
            main.CurrentSection = NavSection.Library;
            var cards = main.Library.Games.OrderBy(c => c.Name).ToList();
            for (int n = 0; n < cards.Count; n++)
            {
                var demo = new List<string>();
                if (n % 3 == 0) demo.Add("Co-op");
                if (n % 4 == 1) demo.Add("With Discord");
                if (n % 5 == 2) demo.Add("Family");
                cards[n].Game.Collections = demo;
            }
            main.Library.RebuildCategories();
            window.WindowState = WindowState.Normal;
            window.Show();
            window.Activate();
            PumpDispatcher(TimeSpan.FromMilliseconds(600));
            main.IsFullScreenLibrary = true;
            PumpDispatcher(TimeSpan.FromMilliseconds(800));

            // From maximized on this monitor to a TV's size, still without a frame.
            var dpi = VisualTreeHelper.GetDpi(window);
            window.WindowState = WindowState.Normal;
            window.Left = 0;
            window.Top = 0;
            window.Width = pxW / dpi.DpiScaleX;
            window.Height = pxH / dpi.DpiScaleY;
            window.Activate();
            window.LibraryPage.FocusGames();
            PumpDispatcher(TimeSpan.FromMilliseconds(1500));
            SaveWindowContentPng(window, Path.Combine(outDir, "fullscreen.png"));

            var card = (gameName == null ? null : main.Library.Games.FirstOrDefault(c => c.Name.StartsWith(gameName, StringComparison.OrdinalIgnoreCase)))
                ?? main.FilteredGames.Cast<GameCardViewModel>().First();
            var deadline = DateTime.UtcNow.AddSeconds(45);
            DateTime? readySince = null;
            var shot = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            shot.Tick += (_, _) =>
            {
                var details = Current.Windows.OfType<GameDetailsDialog>().FirstOrDefault();
                if (details == null) return;
                // Sized for this monitor's screen, not the TV-sized window: fitted to the window as a TV's screen would fit it.
                if (details.ActualHeight > window.ActualHeight - 60 || details.ActualWidth > window.ActualWidth - 60)
                {
                    details.Height = Math.Min(details.ActualHeight, window.ActualHeight - 60);
                    details.Width = Math.Min(details.ActualWidth, window.ActualWidth - 60);
                    details.Left = window.Left + (window.ActualWidth - details.Width) / 2;
                    details.Top = window.Top + (window.ActualHeight - details.Height) / 2;
                }
                bool ready = details.DataContext is GameDetailsViewModel vm && vm.HasDetails && !vm.IsLoading && details.HasHeroImage;
                if (ready) readySince ??= DateTime.UtcNow;
                // A second after it is ready, for the banner's fade and the news to settle.
                if ((readySince is { } since && DateTime.UtcNow - since > TimeSpan.FromSeconds(1.5)) || DateTime.UtcNow > deadline)
                {
                    shot.Stop();
                    // A picture for the site: the game's own folder and file are the player's business, not the page's.
                    foreach (var path in FindVisualChildren<System.Windows.Controls.TextBlock>(details).Where(t => t.Text == card.Game.ExecutablePath))
                        path.Visibility = Visibility.Hidden;
                    PumpDispatcher(TimeSpan.FromMilliseconds(300));
                    CaptureScreen(window, Path.Combine(outDir, "fullscreen-details.png"));
                    details.Close();
                }
            };
            shot.Start();
            main.OpenGameDetails(card);
            shot.Stop();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(outDir, "fullscreen-error.txt"), ex.ToString());
        }
        finally
        {
            main.IsFullScreenLibrary = false;
        }
        ExitApplication();
    }
}
#endif
