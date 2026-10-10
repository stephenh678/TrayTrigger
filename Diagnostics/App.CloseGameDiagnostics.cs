#if DEBUG
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger;

/// <summary>
/// --test-close-game &lt;out.txt&gt;: launches a copy of Character Map (a plain Win32 window that quits
/// on WM_CLOSE) through the real launcher as a throwaway game that isn't in the library, then checks
/// Close Game: it asks the window to close, the game exits, and the session ends through the
/// normal exit path. Then launches it again and checks Force Close still kills it.
///
/// The copy sits in a folder of its own under %TEMP%: the launcher treats a game's folder as the
/// place its processes live, and Force Close can kill everything there. Run from System32 this
/// test once had it kill Windows' own processes (a CRITICAL_PROCESS_DIED blue screen); the folder
/// guard now refuses System32, and the test checks that too.
/// </summary>
public partial class App
{
    private bool TryHandleCloseGameDevArgs(StartupEventArgs e, int i)
    {
        if (!e.Args[i].Equals("--test-close-game", StringComparison.OrdinalIgnoreCase) || i + 1 >= e.Args.Length) return false;
        string outPath = e.Args[i + 1];
        _skipSettingsSaveOnExit = true;

        Task.Run(() =>
        {
            var report = new StringBuilder();
            void Check(string what, bool ok, string detail = "") =>
                report.AppendLine($"{(ok ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? "  (" + detail + ")" : "")}");

            try
            {
                string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
                Check("System32 is refused as a game folder",
                    ProcessPathResolver.FindProcessesUnderDirectory(ProcessPathResolver.NormalizeDirectory(system32), includeHelpers: true).Count == 0);

                string folder = Path.Combine(Path.GetTempPath(), "TrayTrigger-close-game-test");
                Directory.CreateDirectory(Path.Combine(folder, "en-US"));
                string exe = Path.Combine(folder, "charmap.exe");
                File.Copy(Path.Combine(system32, "charmap.exe"), exe, overwrite: true);
                string mui = Path.Combine(system32, "en-US", "charmap.exe.mui");
                if (File.Exists(mui)) File.Copy(mui, Path.Combine(folder, "en-US", "charmap.exe.mui"), overwrite: true);
                if (ProcessPathResolver.IsUnsafeProcessFolder(folder, out string why)) throw new Exception($"test folder refused: {why}");

                var game = new GameEntry { Id = "close-game-test-" + Guid.NewGuid().ToString("N"), Name = "Close Game test", ExecutablePath = exe };

                bool StartAndWait(out string detail)
                {
                    bool launched = _launcherService.LaunchGame(game, out string? error);
                    var until = DateTime.UtcNow.AddSeconds(15);
                    while (DateTime.UtcNow < until)
                    {
                        var s = _launcherService.GetActiveSessions().FirstOrDefault(x => x.GameId == game.Id);
                        var p = s is { GameStarted: true } ? s.Process : null;
                        p?.Refresh();
                        if (p != null && p.MainWindowHandle != IntPtr.Zero)
                        {
                            detail = $"PID {p.Id}";
                            return true;
                        }
                        Thread.Sleep(250);
                    }
                    detail = $"launched: {launched}, error: {error ?? "none"}";
                    return false;
                }

                Check("the test game starts and shows a window", StartAndWait(out string d1), d1);
                int pid = _launcherService.GetActiveSessions().FirstOrDefault(x => x.GameId == game.Id)?.Process?.Id ?? -1;

                var result = _launcherService.CloseGameNow(game.Id);
                Check("Close Game reports the game closed", result == ProcessLauncherService.CloseGameResult.Closed, result.ToString());
                bool exited;
                try { exited = Process.GetProcessById(pid).WaitForExit(2000); } catch (ArgumentException) { /* the process has already exited */ exited = true; }
                Check("the game's process has exited", exited, $"PID {pid}");
                Check("the session has ended", !_launcherService.IsSessionActive(game.Id));

                Check("the test game starts again", StartAndWait(out string d2), d2);
                pid = _launcherService.GetActiveSessions().FirstOrDefault(x => x.GameId == game.Id)?.Process?.Id ?? -1;
                bool ended = _launcherService.EndSessionNow(game.Id, forceCloseGame: true);
                try { exited = Process.GetProcessById(pid).WaitForExit(5000); } catch (ArgumentException) { /* the process has already exited */ exited = true; }
                Check("Force Close kills the game and ends the session", ended && exited && !_launcherService.IsSessionActive(game.Id), $"PID {pid}");

                Check("Close Game with nothing tracked says so",
                    _launcherService.CloseGameNow(game.Id) == ProcessLauncherService.CloseGameResult.NoSession);
            }
            catch (Exception ex)
            {
                report.AppendLine("ERROR " + ex);
            }

            File.WriteAllText(outPath, report.ToString());
            Console.WriteLine(report.ToString());
            Dispatcher.BeginInvoke(ExitApplication);
        });
        return true;
    }
}
#endif
