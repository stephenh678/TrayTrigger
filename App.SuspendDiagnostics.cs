#if DEBUG
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger;

/// <summary>
/// --test-suspend &lt;out.txt&gt;: launches a copy of Character Map through the real launcher as a
/// throwaway game that isn't in the library, and checks CPU Cores and Suspend end to end: the CPU
/// Sets the game gets (at once, and after a delay), Suspend freezing it and stopping its playtime
/// clock, the hotkey resuming it, the anti-cheat refusal, Close Game on a suspended game, and Force
/// Close. Same throwaway folder under %TEMP% as --test-close-game, for the same reason.
/// </summary>
public partial class App
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessDefaultCpuSets(IntPtr process, uint[]? cpuSetIds, uint cpuSetIdCount, out uint requiredIdCount);

    [DllImport("user32.dll", EntryPoint = "IsIconic")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowMinimized(IntPtr hWnd);

    private bool TryHandleSuspendDevArgs(StartupEventArgs e, int i)
    {
        if (!e.Args[i].Equals("--test-suspend", StringComparison.OrdinalIgnoreCase) || i + 1 >= e.Args.Length) return false;
        string outPath = e.Args[i + 1];
        _skipSettingsSaveOnExit = true;

        Task.Run(() =>
        {
            var report = new StringBuilder();
            void Check(string what, bool ok, string detail = "") =>
                report.AppendLine($"{(ok ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? "  (" + detail + ")" : "")}");

            string folder = Path.Combine(Path.GetTempPath(), "TrayTrigger-suspend-test");
            try
            {
                string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
                Directory.CreateDirectory(Path.Combine(folder, "en-US"));
                string exe = Path.Combine(folder, "charmap.exe");
                File.Copy(Path.Combine(system32, "charmap.exe"), exe, overwrite: true);
                string mui = Path.Combine(system32, "en-US", "charmap.exe.mui");
                if (File.Exists(mui)) File.Copy(mui, Path.Combine(folder, "en-US", "charmap.exe.mui"), overwrite: true);

                var topology = CpuTopologyService.GetTopology();
                report.AppendLine($"CPU: {topology.Layout}. {topology.Summary}");
                var game = new GameEntry { Id = "suspend-test-" + Guid.NewGuid().ToString("N"), Name = "Suspend test", ExecutablePath = exe, CpuAffinity = CpuAffinityMode.Auto };

                Process? StartAndWait(out string detail)
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
                            return Process.GetProcessById(p.Id);
                        }
                        Thread.Sleep(250);
                    }
                    detail = $"launched: {launched}, error: {error ?? "none"}";
                    return null;
                }

                uint[] CpuSetsOf(Process p)
                {
                    GetProcessDefaultCpuSets(p.Handle, null, 0, out uint needed);
                    if (needed == 0) return [];
                    var ids = new uint[needed];
                    return GetProcessDefaultCpuSets(p.Handle, ids, needed, out _) ? ids : [];
                }

                bool IsFrozen(Process p)
                {
                    p.Refresh();
                    var threads = p.Threads.Cast<ProcessThread>().ToList();
                    return threads.Count > 0 && threads.All(t => t.ThreadState == System.Diagnostics.ThreadState.Wait && t.WaitReason == ThreadWaitReason.Suspended);
                }

                bool WaitFor(Func<bool> condition, int ms)
                {
                    var until = DateTime.UtcNow.AddMilliseconds(ms);
                    while (DateTime.UtcNow < until) { if (condition()) return true; Thread.Sleep(100); }
                    return condition();
                }

                ActiveGameSession? Session() => _launcherService.GetActiveSessions().FirstOrDefault(x => x.GameId == game.Id);

                // --- CPU Cores, at once
                var process = StartAndWait(out string d1);
                Check("the test game starts and shows a window", process != null, d1);
                if (process == null) throw new Exception("nothing to test");

                var expected = topology.CpusFor(CpuAffinityMode.Auto)?.Select(c => c.Id).OrderBy(x => x).ToArray() ?? [];
                bool cpuSetsOk = WaitFor(() => CpuSetsOf(process).OrderBy(x => x).SequenceEqual(expected), 3000);
                Check(expected.Length == 0 ? "Auto leaves a CPU with nothing to choose alone" : $"Auto keeps the game on {expected.Length} CPU Sets",
                    cpuSetsOk, $"has {string.Join(",", CpuSetsOf(process))}");

                // --- Suspend
                process.Refresh();
                IntPtr gameWindow = process.MainWindowHandle;
                var suspend = _launcherService.SuspendGame(game.Id);
                Check("Suspend reports the game suspended", suspend.Result == ProcessLauncherService.SuspendResult.Suspended, suspend.Message);
                Check("every thread of the game is frozen", IsFrozen(process));
                Check("the game's window is minimized", gameWindow != IntPtr.Zero && IsWindowMinimized(gameWindow));
                Check("the session says suspended", Session()?.IsSuspended == true);
                string suspendedFile = Path.Combine(_storageService.BaseDirectory, "suspended-games.json");
                Check("suspended-games.json lists it, for a start after a crash",
                    File.Exists(suspendedFile) && File.ReadAllText(suspendedFile).Contains(process.Id.ToString()));
                var playedBefore = Session()!.PlayedTime(DateTime.UtcNow);
                Thread.Sleep(1500);
                var playedAfter = Session()!.PlayedTime(DateTime.UtcNow);
                Check("the playtime clock is stopped", (playedAfter - playedBefore).TotalMilliseconds < 50,
                    $"{playedBefore.TotalSeconds:0.00}s then {playedAfter.TotalSeconds:0.00}s");
                Check("a second Suspend says it already is",
                    _launcherService.SuspendGame(game.Id).Result == ProcessLauncherService.SuspendResult.AlreadySuspended);

                // --- the hotkey resumes the suspended game
                var toggle = _launcherService.ToggleSuspendFromHotkey();
                Check("the hotkey resumes it", toggle.Result == ProcessLauncherService.SuspendResult.Resumed, toggle.Message);
                Check("its threads run again", !IsFrozen(process));
                Check("its window is restored", WaitFor(() => !IsWindowMinimized(gameWindow), 2000));
                Check("suspended-games.json is gone", !File.Exists(suspendedFile));
                var playedResumed = Session()!.PlayedTime(DateTime.UtcNow);
                Thread.Sleep(1000);
                Check("the playtime clock runs again", (Session()!.PlayedTime(DateTime.UtcNow) - playedResumed).TotalMilliseconds > 800);

                // --- the hotkey suspends the only game running
                toggle = _launcherService.ToggleSuspendFromHotkey();
                Check("the hotkey suspends the only game running", toggle.Result == ProcessLauncherService.SuspendResult.Suspended, toggle.Message);

                // --- Close Game on a suspended game resumes it, then closes it
                int pid = process.Id;
                var closed = _launcherService.CloseGameNow(game.Id);
                Check("Close Game closes a suspended game", closed == ProcessLauncherService.CloseGameResult.Closed, closed.ToString());
                Check("the game's process has exited", WaitFor(() => process.HasExited, 3000), $"PID {pid}");
                Check("the session has ended", !_launcherService.IsSessionActive(game.Id));

                // --- anti-cheat is refused
                Directory.CreateDirectory(Path.Combine(folder, "EasyAntiCheat"));
                process = StartAndWait(out string d2);
                Check("the test game starts again", process != null, d2);
                var refused = _launcherService.SuspendGame(game.Id);
                Check("a game with Easy Anti-Cheat in its folder is not suspended", refused.Result == ProcessLauncherService.SuspendResult.AntiCheat, refused.Message);
                Check("and isn't frozen", process != null && !IsFrozen(process));
                Directory.Delete(Path.Combine(folder, "EasyAntiCheat"));

                // --- Force Close on a suspended game
                Check("Suspend works once the anti-cheat is gone", _launcherService.SuspendGame(game.Id).Succeeded);
                pid = process?.Id ?? -1;
                bool ended = _launcherService.EndSessionNow(game.Id, forceCloseGame: true);
                Check("Force Close kills a suspended game and ends the session",
                    ended && WaitFor(() => process!.HasExited, 5000) && !_launcherService.IsSessionActive(game.Id), $"PID {pid}");
                Check("nothing is left in suspended-games.json", !File.Exists(suspendedFile));

                // --- CPU Cores after a delay
                if (topology.HasOptions)
                {
                    game.CpuAffinity = topology.Recommended;
                    game.CpuCoresDelaySeconds = 3;
                    process = StartAndWait(out string d3);
                    Check("the test game starts with a 3 s delay before its cores are set", process != null, d3);
                    var early = CpuSetsOf(process!);
                    Check("its cores aren't set straight away", early.Length == 0, $"has {string.Join(",", early)}");
                    bool late = WaitFor(() => CpuSetsOf(process!).OrderBy(x => x).SequenceEqual(expected), 6000);
                    Check("and are set once the delay is up", late, $"has {string.Join(",", CpuSetsOf(process!))}");
                    _launcherService.EndSessionNow(game.Id, forceCloseGame: true);
                    WaitFor(() => process!.HasExited, 5000);
                }
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
