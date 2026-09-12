#if DEBUG
#pragma warning disable CS8602, CS8604
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TrayTrigger.Models;
using TrayTrigger.Services;
using TrayTrigger.ViewModels;
using TrayTrigger.Views;

namespace TrayTrigger;

public partial class App
{
#if DEBUG
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);
    private const int ATTACH_PARENT_PROCESS = -1;

    // Used by --test-dialog-cloak to dismiss a modal shell dialog from a helper thread.
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private const uint WM_CLOSE = 0x0010;

    // Used by --test-poster-zoom: WPF's IsMouseOver only reacts to the real pointer, so the
    // hover trigger cannot be exercised without actually moving it. POINT is declared further
    // down, alongside the screenshot helpers.
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);

    /// <summary>The card's current ScaleTransform, read fresh - see the note at its call sites.</summary>
    private static System.Windows.Media.ScaleTransform ScaleOf(Border card) =>
        card.RenderTransform as System.Windows.Media.ScaleTransform
        ?? throw new Exception($"CardBorder.RenderTransform is {card.RenderTransform?.GetType().Name ?? "null"}, expected ScaleTransform.");
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);

    /// <summary>

    private async System.Threading.Tasks.Task RunXboxDiagnosticAsync(bool launch, string? target)
    {
        static void Log(string m) => LoggingService.Info("XboxTest", m);
        try
        {
            var vm = _mainViewModel!;
            var existing = vm.Games.Where(g => g.Game.IsXboxGame && g.Game.XboxAumid != null).Select(g => g.Game.XboxAumid!).ToList();
            var found = _xboxScannerService.ScanInstalledGames(existing);
            Log($"Scan found {found.Count}: {string.Join("; ", found.Select(g => $"{g.Name} [{g.Aumid}] alreadyImported={g.IsAlreadyImported} exe={g.ExePath}"))}");

            var toImport = found.Where(g => !g.IsAlreadyImported).ToList();
            int added = await vm.Import.ImportXboxGamesAsync(toImport);
            Log($"ImportXboxGamesAsync added {added} (candidates {toImport.Count}).");

            foreach (var card in vm.Games.Where(g => g.Game.IsXboxGame))
            {
                var route = LaunchRouter.Resolve(card.Game, default);
                Log($"Library entry '{card.Name}': aumid={card.Game.XboxAumid} exe={card.Game.ExecutablePath} workDir={card.Game.WorkingDirectory} category={card.Game.Category} importedFrom={card.Game.ImportedFrom} route={route} isLocal={card.IsLocalGame} showCategoryBadge={card.ShowCategoryBadge} isMissing={card.IsMissing} icon={(card.IconImage != null ? "yes" : "no")}");
            }

            if (!launch)
            {
                Log("Import-only run complete; exiting.");
                ExitApplication();
                return;
            }

            var targetCard = vm.Games.FirstOrDefault(g => g.Game.IsXboxGame && (target == null || g.Name.Contains(target, StringComparison.OrdinalIgnoreCase)));
            if (targetCard == null)
            {
                Log($"No Xbox library entry matches '{target}'; exiting.");
                ExitApplication();
                return;
            }

            targetCard.Game.PerformanceProfile = PerformanceProfileMode.Optimized;
            Log($"Launching '{targetCard.Name}' with PerformanceProfile=Optimized. Staying resident to observe the session.");
            vm.LaunchGame(targetCard);
        }
        catch (Exception ex)
        {
            Log($"FAILED: {ex}");
            ExitApplication();
        }
    }

    private void ProcessDevArguments(StartupEventArgs e)
    {
        AttachConsole(ATTACH_PARENT_PROCESS);
        for (int i = 0; i < e.Args.Length; i++)
        {
            string arg = e.Args[i];
            bool isScan = arg.Equals("--test-scan", StringComparison.OrdinalIgnoreCase) ||
                          arg.Equals("-test-scan", StringComparison.OrdinalIgnoreCase) ||
                          arg.Equals("--test-identify", StringComparison.OrdinalIgnoreCase) ||
                          arg.Equals("-test-identify", StringComparison.OrdinalIgnoreCase);
            bool requiresVm = !isScan && (
                              arg.StartsWith("--screenshot", StringComparison.OrdinalIgnoreCase) ||
                              arg.StartsWith("-screenshot", StringComparison.OrdinalIgnoreCase) ||
                              arg.StartsWith("--test-", StringComparison.OrdinalIgnoreCase) ||
                              arg.StartsWith("-test-", StringComparison.OrdinalIgnoreCase));
            if (requiresVm && (_mainViewModel == null || _mainWindow == null)) continue;

            if (e.Args[i].Equals("--test-identify", StringComparison.OrdinalIgnoreCase) ||
                e.Args[i].Equals("-test-identify", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("=== TESTING ENHANCED GAME IDENTIFICATION ===");
                var steam = new SteamSearchService();

                // Test 1: Gothic 1 reamek with Unreal Engine subpath
                string gothicExe = @"C:\Games\Gothic 1 reamek\Gothic\Binaries\Win64\Gothic-Win64-Shipping.exe";
                string gothicFolder = "Gothic 1 reamek";
                var resGothic = GameNameExtractor.ResolveGameMatchAsync(gothicExe, gothicFolder, preferExe: true, searchOnline: true, steamSearch: steam).GetAwaiter().GetResult();
                Console.WriteLine($"[TEST 1 - Gothic 1 reamek] Title: \"{resGothic.ResolvedTitle}\" | SteamAppId: {resGothic.SteamAppId ?? "NULL"}");

                // Test 2: Release group clutter AnkerGames
                string dawnExe = @"C:\Games\The-Blood-of-Dawnwalker-AnkerGames\The Blood of Dawnwalker\Dawnwalker\Binaries\Win64\Dawnwalker.exe";
                string dawnFolder = "The-Blood-of-Dawnwalker-AnkerGames";
                var resDawn = GameNameExtractor.ResolveGameMatchAsync(dawnExe, dawnFolder, preferExe: true, searchOnline: true, steamSearch: steam).GetAwaiter().GetResult();
                Console.WriteLine($"[TEST 2 - AnkerGames] Title: \"{resDawn.ResolvedTitle}\" | SteamAppId: {resDawn.SteamAppId ?? "NULL"}");

                // Test 3: Clutter DODI + version number
                string hkExe = @"C:\Games\Hollow.Knight.v1.5.78.11833.GoG-DODI\hollow_knight.exe";
                string hkFolder = "Hollow.Knight.v1.5.78.11833.GoG-DODI";
                var resHk = GameNameExtractor.ResolveGameMatchAsync(hkExe, hkFolder, preferExe: true, searchOnline: true, steamSearch: steam).GetAwaiter().GetResult();
                Console.WriteLine($"[TEST 3 - DODI / Version] Title: \"{resHk.ResolvedTitle}\" | SteamAppId: {resHk.SteamAppId ?? "NULL"}");

                // Test 4: Unknown / non-Steam game should reject low-confidence matches and preserve clean local name
                string fakeExe = @"C:\Games\CustomUnreleasedGame2026\CustomUnreleasedGame2026.exe";
                string fakeFolder = "CustomUnreleasedGame2026";
                var resFake = GameNameExtractor.ResolveGameMatchAsync(fakeExe, fakeFolder, preferExe: true, searchOnline: true, steamSearch: steam).GetAwaiter().GetResult();
                Console.WriteLine($"[TEST 4 - Unknown Game] Title: \"{resFake.ResolvedTitle}\" | SteamAppId: {resFake.SteamAppId ?? "NULL"} (Expected: NULL AppId, preserved name)");

                // Test 5: String similarity checks
                double simGeneric = SteamSearchService.CalculateSimilarity("Remake", "Heroes of Might and Magic III Remake");
                double simGothicTypo = SteamSearchService.CalculateSimilarity("Gothic 1 reamek", "Gothic 1 Remake");
                double simRoman = SteamSearchService.CalculateSimilarity("Civilization VI", "Civilization 6");
                Console.WriteLine($"[TEST 5 - Similarity] 'Remake' vs 'Heroes...': {simGeneric:F2} (Must be < 0.60)");
                Console.WriteLine($"[TEST 5 - Similarity] 'Gothic 1 reamek' vs 'Gothic 1 Remake': {simGothicTypo:F2} (Must be >= 0.60)");
                Console.WriteLine($"[TEST 5 - Similarity] 'Civilization VI' vs 'Civilization 6': {simRoman:F2} (Must be >= 0.85)");

                Console.WriteLine("=== ALL TESTS FINISHED ===");
                ExitApplication();
                return;
            }

            // --test-xbox: scan Gaming Services for GDK titles, import them into the real library,
            // log what the library now holds and how each would route, then exit.
            // --test-xbox-launch <name>: same, then launch the named entry with an Optimized
            // profile and stay running so the session (profile apply/revert, tracking, exit) can be
            // followed in debug.log.
            if (e.Args[i].Equals("--test-xbox", StringComparison.OrdinalIgnoreCase) ||
                e.Args[i].Equals("--test-xbox-launch", StringComparison.OrdinalIgnoreCase))
            {
                bool launch = e.Args[i].EndsWith("-launch", StringComparison.OrdinalIgnoreCase);
                string? target = launch && i + 1 < e.Args.Length ? e.Args[i + 1] : null;
                _ = RunXboxDiagnosticAsync(launch, target);
                return;
            }
            if (e.Args[i].Equals("--test-scan", StringComparison.OrdinalIgnoreCase) && i + 1 < e.Args.Length)
            {
                string targetFolder = e.Args[i + 1];
                var scanner = new FolderScannerService();
                var list = scanner.ScanFolder(targetFolder);
                Console.WriteLine($"[TEST-SCAN-RESULT] Count={list.Count}");
                foreach (var c in list)
                {
                    Console.WriteLine($"[CANDIDATE] Name={c.Name} | Score={c.ConfidenceScore} | RelPath={c.RelativePath} | Size={c.DisplaySize}");
                }
                ExitApplication();
                return;
            }

            if (e.Args[i].Equals("--test-scan-library", StringComparison.OrdinalIgnoreCase) && i + 1 < e.Args.Length)
            {
                string targetFolder = e.Args[i + 1];
                var scanner = new FolderScannerService();
                var res = scanner.ScanFolderOrLibrary(targetFolder);
                Console.WriteLine($"[TEST-LIBRARY-RESULT] IsMultiGameLibrary={res.IsMultiGameLibrary} | DiscoveredGamesCount={res.DiscoveredGames.Count} | SingleCandidatesCount={res.SingleGameCandidates.Count}");
                foreach (var g in res.DiscoveredGames)
                {
                    Console.WriteLine($"[DETECTED-GAME] Name={g.Name} | Exe={g.ExePath} | Score={g.ConfidenceScore} | RelPath={g.RelativePath}");
                }
                ExitApplication();
                return;
            }


            if (e.Args[i].Equals("--test-gamename", StringComparison.OrdinalIgnoreCase) ||
                e.Args[i].Equals("-test-gamename", StringComparison.OrdinalIgnoreCase))
            {
                string testExe = @"C:\Games\The-Blood-of-Dawnwalker-AnkerGames\The Blood of Dawnwalker\Dawnwalker\Binaries\Win64\Dawnwalker.exe";
                string folder = "The-Blood-of-Dawnwalker-AnkerGames";
                string localName = GameNameExtractor.ExtractGameName(testExe, folder, preferExe: true);
                var steamSearch = new SteamSearchService();
                var match = steamSearch.FindBestMatchAsync(localName).GetAwaiter().GetResult();
                Console.WriteLine($"[TEST-LOCAL-NAME]  '{localName}'");
                Console.WriteLine($"[TEST-ONLINE-NAME] '{match?.Name}' (AppId: {match?.AppId})");
                if (match?.AppId != null)
                {
                    using var http = new System.Net.Http.HttpClient();
                    string json = http.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={match.AppId}&l=english").GetAwaiter().GetResult();
                    Console.WriteLine($"[APPDETAILS-LENGTH] {json.Length} chars");
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty(match.AppId, out var appElem) && appElem.GetProperty("success").GetBoolean())
                    {
                        var data = appElem.GetProperty("data");
                        Console.WriteLine($"[NAME] {data.GetProperty("name").GetString()}");
                        if (data.TryGetProperty("genres", out var genres))
                        {
                            foreach (var g in genres.EnumerateArray())
                            {
                                Console.WriteLine($"[GENRE] {g.GetProperty("description").GetString()}");
                            }
                        }
                        if (data.TryGetProperty("developers", out var devs))
                        {
                            foreach (var d in devs.EnumerateArray())
                            {
                                Console.WriteLine($"[DEVELOPER] {d.GetString()}");
                            }
                        }
                        if (data.TryGetProperty("publishers", out var pubs))
                        {
                            foreach (var p in pubs.EnumerateArray())
                            {
                                Console.WriteLine($"[PUBLISHER] {p.GetString()}");
                            }
                        }
                        if (data.TryGetProperty("release_date", out var rd))
                        {
                            Console.WriteLine($"[RELEASE DATE] {rd.GetProperty("date").GetString()}");
                        }
                        if (data.TryGetProperty("capsule_imagev5", out var v5))
                        {
                            Console.WriteLine($"[CAPSULE_V5] {v5.GetString()}");
                        }
                        if (data.TryGetProperty("short_description", out var sd))
                        {
                            Console.WriteLine($"[SHORT_DESC] {sd.GetString()}");
                        }
                        if (data.TryGetProperty("categories", out var cats))
                        {
                            foreach (var c in cats.EnumerateArray())
                            {
                                Console.WriteLine($"[CATEGORY/PLAYMODE] {c.GetProperty("description").GetString()}");
                            }
                        }
                        if (data.TryGetProperty("pc_requirements", out var pcr))
                        {
                            if (pcr.TryGetProperty("minimum", out var min))
                                Console.WriteLine($"[PC MIN LENGTH] {min.GetString()?.Length}");
                            if (pcr.TryGetProperty("recommended", out var rec))
                                Console.WriteLine($"[PC REC LENGTH] {rec.GetString()?.Length}");
                        }

                        // Test Cyberpunk 1091500 poster
                        var cpPoster = http.GetAsync("https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/1091500/library_600x900_2x.jpg").GetAwaiter().GetResult();
                        Console.WriteLine($"[CYBERPUNK POSTER STATUS] {cpPoster.StatusCode}");
                    }
                }
                ExitApplication();
                return;
            }

            if (e.Args[i].Equals("--test-view-modes", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[TEST-VIEW-MODES] Initial mode: {_mainViewModel.SettingsVM.LibraryViewMode} (IsGrid={_mainViewModel.SettingsVM.IsGridView}, IsIcons={_mainViewModel.SettingsVM.IsIconsView}, IsList={_mainViewModel.SettingsVM.IsListView})");
                if (!_mainViewModel.SettingsVM.IsGridView || _mainViewModel.SettingsVM.LibraryViewMode != SettingsViewModel.ViewModePosterGrid)
                {
                    Console.WriteLine("[TEST-VIEW-MODES-ERROR] Expected initial mode to default to Poster Grid");
                    ExitApplication();
                    return;
                }

                _mainViewModel.SettingsVM.SetViewModeCommand.Execute(SettingsViewModel.ViewModeCompactIcons);
                Console.WriteLine($"[TEST-VIEW-MODES] After Compact Icons: {_mainViewModel.SettingsVM.LibraryViewMode} (IsGrid={_mainViewModel.SettingsVM.IsGridView}, IsIcons={_mainViewModel.SettingsVM.IsIconsView}, IsList={_mainViewModel.SettingsVM.IsListView})");
                if (!_mainViewModel.SettingsVM.IsIconsView || _mainViewModel.SettingsVM.LibraryViewMode != SettingsViewModel.ViewModeCompactIcons)
                {
                    Console.WriteLine("[TEST-VIEW-MODES-ERROR] Failed switching to Compact Icons");
                    ExitApplication();
                    return;
                }

                _mainViewModel.SettingsVM.SetViewModeCommand.Execute(SettingsViewModel.ViewModeDetailsList);
                Console.WriteLine($"[TEST-VIEW-MODES] After Details List: {_mainViewModel.SettingsVM.LibraryViewMode} (IsGrid={_mainViewModel.SettingsVM.IsGridView}, IsIcons={_mainViewModel.SettingsVM.IsIconsView}, IsList={_mainViewModel.SettingsVM.IsListView})");
                if (!_mainViewModel.SettingsVM.IsListView || _mainViewModel.SettingsVM.LibraryViewMode != SettingsViewModel.ViewModeDetailsList)
                {
                    Console.WriteLine("[TEST-VIEW-MODES-ERROR] Failed switching to Details List");
                    ExitApplication();
                    return;
                }

                _mainViewModel.SettingsVM.SetViewModeCommand.Execute(SettingsViewModel.ViewModePosterGrid);
                Console.WriteLine($"[TEST-VIEW-MODES] After Poster Grid: {_mainViewModel.SettingsVM.LibraryViewMode} (IsGrid={_mainViewModel.SettingsVM.IsGridView}, IsIcons={_mainViewModel.SettingsVM.IsIconsView}, IsList={_mainViewModel.SettingsVM.IsListView})");
                if (!_mainViewModel.SettingsVM.IsGridView || _mainViewModel.SettingsVM.LibraryViewMode != SettingsViewModel.ViewModePosterGrid)
                {
                    Console.WriteLine("[TEST-VIEW-MODES-ERROR] Failed switching to Poster Grid");
                    ExitApplication();
                    return;
                }

                // Backward compatibility normalization test
                _mainViewModel.SettingsVM.SetViewModeCommand.Execute("Icons");
                if (!_mainViewModel.SettingsVM.IsIconsView) { Console.WriteLine("[TEST-VIEW-MODES-ERROR] 'Icons' alias failed"); ExitApplication(); return; }
                _mainViewModel.SettingsVM.SetViewModeCommand.Execute("List");
                if (!_mainViewModel.SettingsVM.IsListView) { Console.WriteLine("[TEST-VIEW-MODES-ERROR] 'List' alias failed"); ExitApplication(); return; }
                _mainViewModel.SettingsVM.SetViewModeCommand.Execute("Grid");
                if (!_mainViewModel.SettingsVM.IsGridView) { Console.WriteLine("[TEST-VIEW-MODES-ERROR] 'Grid' alias failed"); ExitApplication(); return; }
                _mainViewModel.SettingsVM.SetViewModeCommand.Execute("invalid_xyz");
                if (!_mainViewModel.SettingsVM.IsGridView || _mainViewModel.SettingsVM.LibraryViewMode != SettingsViewModel.ViewModePosterGrid)
                {
                    Console.WriteLine("[TEST-VIEW-MODES-ERROR] Invalid mode fallback failed");
                    ExitApplication();
                    return;
                }

                Console.WriteLine("[TEST-VIEW-MODES-SUCCESS] All view mode transitions and Poster Grid defaults verified successfully!");
                ExitApplication();
                return;
            }

            if (e.Args[i].Equals("--test-updater", StringComparison.OrdinalIgnoreCase) ||
                e.Args[i].Equals("-test-updater", StringComparison.OrdinalIgnoreCase))
            {
                void LogOut(string msg)
                {
                    Console.WriteLine(msg);
                    LoggingService.Info("DevTest", msg);
                }

                LogOut($"[TEST-UPDATER] Current Version: {UpdateService.CurrentVersionDisplay} ({UpdateService.CurrentVersion})");
                
                // 1. Verify semantic version parsing
                var release1 = new GitHubReleaseInfo { TagName = "v1.0.1" };
                if (release1.ParsedVersion != new Version(1, 0, 1))
                {
                    LogOut($"[TEST-UPDATER-ERROR] Expected 1.0.1 but got {release1.ParsedVersion}");
                    ExitApplication();
                    return;
                }

                var release2 = new GitHubReleaseInfo { TagName = "v2.0.0-beta.1" };
                if (release2.ParsedVersion != new Version(2, 0, 0))
                {
                    LogOut($"[TEST-UPDATER-ERROR] Expected 2.0.0 but got {release2.ParsedVersion}");
                    ExitApplication();
                    return;
                }

                // 2. Verify asset detection
                var releaseWithAssets = new GitHubReleaseInfo
                {
                    TagName = "v1.1.0",
                    Assets = new List<GitHubReleaseAsset>
                    {
                        new() { Name = "TrayTrigger-v1.1.0-portable.zip", BrowserDownloadUrl = "http://example.com/zip", Size = 25000000 },
                        new() { Name = "TrayTrigger-v1.1.0-Setup.exe", BrowserDownloadUrl = "http://example.com/setup", Size = 28000000 }
                    }
                };
                if (releaseWithAssets.InstallerAsset?.Name != "TrayTrigger-v1.1.0-Setup.exe")
                {
                    LogOut("[TEST-UPDATER-ERROR] Failed finding InstallerAsset");
                    ExitApplication();
                    return;
                }
                if (releaseWithAssets.ZipAsset?.Name != "TrayTrigger-v1.1.0-portable.zip")
                {
                    LogOut("[TEST-UPDATER-ERROR] Failed finding ZipAsset");
                    ExitApplication();
                    return;
                }

                // 3. Test GitHub query (graceful 404 handling if repo not yet published)
                var result = Task.Run(() => UpdateService.Instance.CheckForUpdatesAsync("Steph/TrayTrigger")).GetAwaiter().GetResult();
                LogOut($"[TEST-UPDATER] CheckForUpdates result: Status={result.Status}, ErrorMessage={result.ErrorMessage ?? "(none)"}");

                // 4. Test Settings bindings
                if (!_mainViewModel.SettingsVM.AutoCheckForUpdates)
                {
                    LogOut("[TEST-UPDATER-ERROR] AutoCheckForUpdates should default to true");
                    ExitApplication();
                    return;
                }

                LogOut("[TEST-UPDATER-SUCCESS] All update service tests passed!");
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-updater-dialog", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-updater-dialog", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                var mockRelease = new GitHubReleaseInfo
                {
                    TagName = "v1.1.0",
                    Name = "TrayTrigger v1.1.0 - Windows Setup & Auto-Update Engine",
                    Body = "### Highlights:\n• Automated GitHub release detection and self-upgrade workflow\n• Modern Windows Installer with custom directory and startup options\n• Seamless tray minimize and performance improvements",
                    HtmlUrl = "https://github.com/Steph/TrayTrigger/releases/tag/v1.1.0",
                    Assets = new List<GitHubReleaseAsset>
                    {
                        new() { Name = "TrayTrigger-v1.1.0-Setup.exe", Size = 26588200 },
                        new() { Name = "TrayTrigger-v1.1.0-portable.zip", Size = 31200000 }
                    }
                };

                var dlg = new UpdateDialog(mockRelease, UpdateService.CurrentVersion);
                dlg.Show();
                dlg.UpdateLayout();
                CaptureVisual(dlg, 540, 360, targetPng);
                dlg.Close();
                ExitApplication();
                return;
            }

            // --screenshot-batch-menu <out.png>: a real screen grab (popups don't render into a
            // RenderTargetBitmap) of the poster grid with two cards selected and the batch
            // context menu open over the second one.
            if ((e.Args[i].Equals("--screenshot-batch-menu", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-batch-menu", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                _mainViewModel.CurrentSection = NavSection.Library;
                _mainViewModel.SettingsVM.LibraryViewMode = SettingsViewModel.ViewModePosterGrid;
                _mainWindow.WindowStartupLocation = WindowStartupLocation.Manual;
                _mainWindow.Left = 0;
                _mainWindow.Top = 0;
                _mainWindow.Width = 1020;
                _mainWindow.Height = 700;
                _mainWindow.Show();
                var selected = _mainViewModel.FilteredGames.Cast<GameCardViewModel>().Take(2).ToList();
                foreach (var card in selected)
                {
                    _mainViewModel.SetCardSelected(card, true);
                }
                WindowThemeService.WhenContentRendered(_mainWindow, () =>
                {
                    var open = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                    open.Tick += (s, args) =>
                    {
                        open.Stop();
                        var target = FindVisualChild<Border>(_mainWindow, b => b.Name == "CardBorder" && ReferenceEquals(b.DataContext, selected.LastOrDefault()));
                        var menu = (ContextMenu)_mainWindow.FindResource("GameBatchContextMenu");
                        menu.DataContext = _mainViewModel;
                        menu.PlacementTarget = target ?? (UIElement)_mainWindow;
                        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Center;
                        menu.IsOpen = true;
                        var grab = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                        grab.Tick += (s2, args2) =>
                        {
                            grab.Stop();
                            CaptureScreen(_mainWindow, targetPng);
                            menu.IsOpen = false;
                            ExitApplication();
                        };
                        grab.Start();
                    };
                    open.Start();
                });
                return;
            }

            // --screenshot-select <view mode> <out.png>: the library in Select mode with the first
            // two visible cards checked, in the given view ("Poster Grid", "Extra Large",
            // "Compact Icons", "Details List").
            if ((e.Args[i].Equals("--screenshot-select", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-select", StringComparison.OrdinalIgnoreCase)) &&
                i + 2 < e.Args.Length)
            {
                string viewMode = e.Args[i + 1];
                string targetPng = e.Args[i + 2];
                string previousMode = _mainViewModel.SettingsVM.LibraryViewMode;
                _mainViewModel.CurrentSection = NavSection.Library;
                _mainViewModel.SettingsVM.LibraryViewMode = viewMode;
                foreach (var card in _mainViewModel.FilteredGames.Cast<GameCardViewModel>().Take(2))
                {
                    _mainViewModel.SetCardSelected(card, true);
                }
                _mainWindow.Show();
                _mainWindow.UpdateLayout();
                CaptureVisual(_mainWindow, 960, 700, targetPng);
                _mainViewModel.SettingsVM.LibraryViewMode = previousMode;
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-icons", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-icons", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                string previousMode = _mainViewModel.SettingsVM.LibraryViewMode;
                _mainViewModel.CurrentSection = NavSection.Library;
                _mainViewModel.SettingsVM.LibraryViewMode = SettingsViewModel.ViewModeCompactIcons;
                _mainWindow.Show();
                _mainWindow.UpdateLayout();
                CaptureVisual(_mainWindow, 960, 700, targetPng);
                _mainViewModel.SettingsVM.LibraryViewMode = previousMode;
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-list", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-list", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                string previousMode = _mainViewModel.SettingsVM.LibraryViewMode;
                _mainViewModel.CurrentSection = NavSection.Library;
                _mainViewModel.SettingsVM.LibraryViewMode = SettingsViewModel.ViewModeDetailsList;
                _mainWindow.Show();
                _mainWindow.UpdateLayout();
                CaptureVisual(_mainWindow, 960, 700, targetPng);
                _mainViewModel.SettingsVM.LibraryViewMode = previousMode;
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                _mainWindow.Show();
                _mainWindow.UpdateLayout();
                CaptureVisual(_mainWindow, 960, 700, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-settings", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-settings", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                _mainViewModel.CurrentSection = NavSection.Settings;
                _mainWindow.Show();
                _mainWindow.UpdateLayout();
                var sv = FindVisualChild<ScrollViewer>(_mainWindow, s => s.ScrollableHeight > 0);
                sv?.ScrollToBottom();
                _mainWindow.UpdateLayout();
                CaptureVisual(_mainWindow, 960, 750, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-settings-top", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-settings-top", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                _mainViewModel.SettingsVM.SelectedTab = SettingsCategoryTab.All;
                _mainViewModel.CurrentSection = NavSection.Settings;
                _mainWindow.Show();
                _mainWindow.UpdateLayout();
                CaptureVisual(_mainWindow, 960, 750, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-settings-diagnostics", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-settings-diagnostics", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                _mainViewModel.SettingsVM.SelectedTab = SettingsCategoryTab.Diagnostics;
                _mainViewModel.CurrentSection = NavSection.Settings;
                _mainWindow.Show();
                _mainWindow.UpdateLayout();
                CaptureVisual(_mainWindow, 960, 750, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-settings-library", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-settings-library", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                _mainViewModel.SettingsVM.SelectedTab = SettingsCategoryTab.Library;
                _mainViewModel.CurrentSection = NavSection.Settings;
                _mainWindow.Show();
                _mainWindow.UpdateLayout();
                CaptureVisual(_mainWindow, 960, 750, targetPng);
                ExitApplication();
                return;
            }

            // --screenshot-settings-performance <out.png> [offset]: the Launch & Performance tab,
            // scrolled to its end (where the default scripts block sits) or to a given offset.
            if ((e.Args[i].Equals("--screenshot-settings-performance", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-settings-performance", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                double? settingsScrollOffset = i + 2 < e.Args.Length && double.TryParse(e.Args[i + 2], out double parsedSettingsOffset) ? parsedSettingsOffset : null;
                _mainViewModel.SettingsVM.SelectedTab = SettingsCategoryTab.PerformanceTweaks;
                _mainViewModel.CurrentSection = NavSection.Settings;
                _mainWindow.Show();
                _mainWindow.UpdateLayout();
                var sv = FindVisualChild<ScrollViewer>(_mainWindow, s => s.ScrollableHeight > 0);
                if (settingsScrollOffset is double settingsOffset) sv?.ScrollToVerticalOffset(settingsOffset); else sv?.ScrollToBottom();
                _mainWindow.UpdateLayout();
                CaptureVisual(_mainWindow, 960, 750, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-settings-general", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-settings-general", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                _mainViewModel.SettingsVM.SelectedTab = SettingsCategoryTab.General;
                _mainViewModel.CurrentSection = NavSection.Settings;
                _mainWindow.Show();
                _mainWindow.UpdateLayout();
                CaptureVisual(_mainWindow, 960, 750, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-settings-tray", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-settings-tray", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                _mainViewModel.SettingsVM.SelectedTab = SettingsCategoryTab.TrayMenu;
                _mainViewModel.CurrentSection = NavSection.Settings;
                _mainWindow.Show();
                _mainWindow.UpdateLayout();
                CaptureVisual(_mainWindow, 960, 750, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-about", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-about", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                _mainViewModel.CurrentSection = NavSection.About;
                _mainViewModel.CurrentAboutSection = AboutSubSection.All;
                _mainWindow.Show();
                _mainWindow.UpdateLayout();
                CaptureVisual(_mainWindow, 960, 750, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-about-overview", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-about-overview", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                _mainViewModel.CurrentSection = NavSection.About;
                _mainViewModel.CurrentAboutSection = AboutSubSection.Overview;
                _mainWindow.Show();
                _mainWindow.UpdateLayout();
                CaptureVisual(_mainWindow, 960, 750, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-about-all-scroll", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-about-all-scroll", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                _mainViewModel.CurrentSection = NavSection.About;
                _mainViewModel.CurrentAboutSection = AboutSubSection.All;
                _mainWindow.Show();
                _mainWindow.UpdateLayout();
                var sv = FindVisualChild<ScrollViewer>(_mainWindow, s => s.ScrollableHeight > 0);
                sv?.ScrollToVerticalOffset(500);
                _mainWindow.UpdateLayout();
                CaptureVisual(_mainWindow, 960, 750, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-about-features", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-about-features", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                _mainViewModel.CurrentSection = NavSection.About;
                _mainViewModel.CurrentAboutSection = AboutSubSection.Features;
                _mainWindow.Show();
                _mainWindow.UpdateLayout();
                CaptureVisual(_mainWindow, 960, 750, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-about-shortcuts", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-about-shortcuts", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                _mainViewModel.CurrentSection = NavSection.About;
                _mainViewModel.CurrentAboutSection = AboutSubSection.Shortcuts;
                _mainWindow.Show();
                _mainWindow.UpdateLayout();
                CaptureVisual(_mainWindow, 960, 750, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-about-support", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-about-support", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("--screenshot-about-diagnostics", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-about-diagnostics", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                _mainViewModel.CurrentSection = NavSection.About;
                _mainViewModel.CurrentAboutSection = AboutSubSection.Support;
                _mainWindow.Show();
                _mainWindow.UpdateLayout();
                CaptureVisual(_mainWindow, 960, 750, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-system-all", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-system-all", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                _mainViewModel.CurrentSection = NavSection.System;
                _mainViewModel.SystemVM.CurrentSubSection = SystemSubSection.All;
                _mainWindow.Show();
                _mainWindow.UpdateLayout();
                CaptureVisual(_mainWindow, 960, 750, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-system-all-scroll", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-system-all-scroll", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                _mainViewModel.CurrentSection = NavSection.System;
                _mainViewModel.SystemVM.CurrentSubSection = SystemSubSection.All;
                _mainWindow.Show();
                _mainWindow.UpdateLayout();
                var sv = FindVisualChild<ScrollViewer>(_mainWindow, s => s.ScrollableHeight > 0);
                sv?.ScrollToVerticalOffset(650);
                _mainWindow.UpdateLayout();
                CaptureVisual(_mainWindow, 960, 750, targetPng);
                ExitApplication();
                return;
            }

            // --screenshot-system-ready <out.png>: the System "All" tab captured only once hardware
            // detection has finished (up to 15 s), so the README shot shows real specs rather than
            // "Detecting hardware...".
            if ((e.Args[i].Equals("--screenshot-system-ready", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-system-ready", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                _mainViewModel.CurrentSection = NavSection.System;
                _mainViewModel.SystemVM.CurrentSubSection = SystemSubSection.All;
                _mainWindow.Show();
                _mainWindow.UpdateLayout();
                var started = DateTime.Now;
                var poll = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                poll.Tick += (s, args) =>
                {
                    if (!_mainViewModel.SystemVM.IsSpecsLoaded && DateTime.Now - started < TimeSpan.FromSeconds(15)) return;
                    poll.Stop();
                    // One more beat so the bound cards have laid out with the detected values.
                    var settle = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                    settle.Tick += (s2, args2) =>
                    {
                        settle.Stop();
                        _mainWindow.UpdateLayout();
                        CaptureVisual(_mainWindow, 960, 750, targetPng);
                        ExitApplication();
                    };
                    settle.Start();
                };
                poll.Start();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-system-specs", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-system-specs", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                _mainViewModel.CurrentSection = NavSection.System;
                _mainViewModel.SystemVM.CurrentSubSection = SystemSubSection.HardwareSpecs;
                _mainWindow.Show();
                _mainWindow.UpdateLayout();
                CaptureVisual(_mainWindow, 960, 750, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-system-tweaks", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-system-tweaks", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                _mainViewModel.CurrentSection = NavSection.System;
                _mainViewModel.SystemVM.CurrentSubSection = SystemSubSection.PerformanceTweaks;
                _mainWindow.Show();
                _mainWindow.UpdateLayout();
                CaptureVisual(_mainWindow, 960, 750, targetPng);
                ExitApplication();
                return;
            }

            // --screenshot-nomatch <out.png>: the library's "No games match" state (a search
            // that hits nothing).
            if ((e.Args[i].Equals("--screenshot-nomatch", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-nomatch", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                _mainViewModel.CurrentSection = NavSection.Library;
                _mainViewModel.SearchText = "zzzz-no-such-game";
                _mainWindow.Show();
                _mainWindow.UpdateLayout();
                CaptureVisual(_mainWindow, 960, 700, targetPng);
                _mainViewModel.SearchText = string.Empty;
                ExitApplication();
                return;
            }

            // --screenshot-welcome <out.png>: the first-run Welcome dialog.
            if ((e.Args[i].Equals("--screenshot-welcome", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-welcome", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                var dlg = new WelcomeDialog();
                CaptureVisual(dlg, 560, 620, targetPng);
                ExitApplication();
                return;
            }

            // --screenshot-sources-reminder <out.png> [steamgriddb|rawg]: the one-time SteamGridDB /
            // RAWG reminder, worded for both sources missing unless one is named as the only gap.
            if ((e.Args[i].Equals("--screenshot-sources-reminder", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-sources-reminder", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                string only = i + 2 < e.Args.Length ? e.Args[i + 2] : string.Empty;
                bool needsSteamGridDb = !only.Equals("rawg", StringComparison.OrdinalIgnoreCase);
                bool needsRawg = !only.Equals("steamgriddb", StringComparison.OrdinalIgnoreCase);
                var dlg = ModernDialog.CreateMetadataSourcesReminder(needsSteamGridDb, needsRawg);
                CaptureVisual(dlg, 470, 240, targetPng);
                ExitApplication();
                return;
            }

            // --screenshot-system-profiles <out.png>: the Performance Profiles sub-tab.
            if ((e.Args[i].Equals("--screenshot-system-profiles", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-system-profiles", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                _mainViewModel.CurrentSection = NavSection.System;
                _mainViewModel.SystemVM.CurrentSubSection = SystemSubSection.GameProfiles;
                _mainWindow.Show();
                _mainWindow.UpdateLayout();
                CaptureVisual(_mainWindow, 960, 820, targetPng);
                ExitApplication();
                return;
            }

            // --screenshot-edit-bottom <out.png> [offset]: the Edit Game dialog scrolled to its end
            // (scripts card and privileges card), or to a given vertical offset when one is passed.
            if ((e.Args[i].Equals("--screenshot-edit-bottom", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-edit-bottom", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                double? scrollOffset = i + 2 < e.Args.Length && double.TryParse(e.Args[i + 2], out double parsedOffset) ? parsedOffset : null;
                var sampleGame = _mainViewModel.Games.FirstOrDefault()?.Game ?? new GameEntry
                {
                    Name = "DOOM Eternal",
                    Category = "Action",
                    ExecutablePath = @"C:\Games\DOOM Eternal\DOOMEternalx64tk.exe",
                    WorkingDirectory = @"C:\Games\DOOM Eternal"
                };
                var dlg = new GameEditDialog(sampleGame, _mainViewModel.Categories, _iconExtractorService, scriptsEnabled: true);
                dlg.Show();
                dlg.UpdateLayout();
                var sv = FindVisualChild<ScrollViewer>(dlg, s => s.ScrollableHeight > 0);
                if (scrollOffset is double offset) sv?.ScrollToVerticalOffset(offset); else sv?.ScrollToBottom();
                dlg.UpdateLayout();
                CaptureVisual(dlg, 820, 660, targetPng);
                ExitApplication();
                return;
            }

            // --screenshot-help <topicId> <out.png>: renders the HelpDialog for one topic.
            if ((e.Args[i].Equals("--screenshot-help", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-help", StringComparison.OrdinalIgnoreCase)) &&
                i + 2 < e.Args.Length)
            {
                string topicId = e.Args[i + 1];
                string targetPng = e.Args[i + 2];
                _mainViewModel.CurrentSection = NavSection.System;
                _mainViewModel.SystemVM.CurrentSubSection = SystemSubSection.PerformanceTweaks;
                _mainWindow.Show();
                _mainWindow.UpdateLayout();
                Views.HelpDialog.ShowTopic(_mainWindow, topicId);
                var helpWindow = Current.Windows.OfType<Views.HelpDialog>().First();
                helpWindow.UpdateLayout();
                CaptureVisual(helpWindow, (int)helpWindow.Width, (int)helpWindow.Height, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-collapsed", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-collapsed", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                _mainViewModel.IsSidebarExpanded = false;
                CaptureVisual(_mainWindow, 960, 700, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-expanded", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-expanded", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                _mainViewModel.IsSidebarExpanded = true;
                CaptureVisual(_mainWindow, 960, 700, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-steam", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-steam", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                var dlg = new ScanForGamesDialog(_mainViewModel, new List<DiscoveredSteamGame>(), new List<DiscoveredGogGame>(), new List<DiscoveredEaGame>(), new List<DiscoveredEpicGame>(), new List<DiscoveredUbisoftGame>(), new List<DiscoveredXboxGame>(), new List<GameCandidate>());
                CaptureVisual(dlg, 680, 580, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-exit-dialog", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-exit-dialog", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                var dialog = new ModernDialog(
                    "Exit TrayTrigger",
                    "Close or minimize TrayTrigger?",
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
                CaptureVisual(dialog, 490, 220, targetPng);
                ExitApplication();
                return;
            }

            if (e.Args[i].Equals("--test-steam-exit-behavior", StringComparison.OrdinalIgnoreCase))
            {
                var steamGameDefaultCat = new GameEntry { Name = "Portal 2", Category = "Steam", IsSteamGame = true };
                var card1 = new GameCardViewModel(steamGameDefaultCat, _ => {}, _ => {}, _ => {});
                if (!card1.IsSteamGame || card1.ShowCategoryBadge)
                    throw new Exception("Test 1 failed: steamGameDefaultCat should have IsSteamGame=true and ShowCategoryBadge=false");

                var steamGameCustomCat = new GameEntry { Name = "Cyberpunk 2077", Category = "Favorites", IsSteamGame = true };
                var card2 = new GameCardViewModel(steamGameCustomCat, _ => {}, _ => {}, _ => {});
                if (!card2.IsSteamGame || !card2.ShowCategoryBadge)
                    throw new Exception("Test 2 failed: steamGameCustomCat should have IsSteamGame=true and ShowCategoryBadge=true");

                var nonSteamGame = new GameEntry { Name = "Notepad", Category = "Utilities", IsSteamGame = false };
                var card3 = new GameCardViewModel(nonSteamGame, _ => {}, _ => {}, _ => {});
                if (card3.IsSteamGame || !card3.ShowCategoryBadge)
                    throw new Exception("Test 3 failed: nonSteamGame should have IsSteamGame=false and ShowCategoryBadge=true");

                if (!_mainViewModel.SettingsVM.MinimizeOnGameLaunch)
                    throw new Exception("Test 4 failed: MinimizeOnGameLaunch should default to true");

                Console.WriteLine($"[TEST_STEAM_EXIT_PASSED] card1 ShowCategoryBadge={card1.ShowCategoryBadge}, card2 ShowCategoryBadge={card2.ShowCategoryBadge}, MinimizeOnGameLaunch={_mainViewModel.SettingsVM.MinimizeOnGameLaunch}");
                ExitApplication();
                return;
            }

            if (e.Args[i].Equals("--test-force-steam-overlay", StringComparison.OrdinalIgnoreCase))
            {
                var nonSteamGame = new GameEntry { Name = "GOG Witcher 3", Category = "RPG", IsSteamGame = false, ForceSteamOverlayTag = false };
                var card1 = new GameCardViewModel(nonSteamGame, _ => {}, _ => {}, _ => {});
                if (card1.HasSteamOverlay)
                    throw new Exception("Test failed: nonSteamGame should have HasSteamOverlay=false");

                var forcedGame = new GameEntry { Name = "GOG Witcher 3", Category = "RPG", IsSteamGame = false, ForceSteamOverlayTag = true };
                var card2 = new GameCardViewModel(forcedGame, _ => {}, _ => {}, _ => {});
                if (!card2.HasSteamOverlay || !card2.ShowCategoryBadge)
                    throw new Exception("Test failed: forcedGame should have HasSteamOverlay=true and ShowCategoryBadge=true");

                var forcedSteamCatGame = new GameEntry { Name = "GOG Witcher 3", Category = "Steam", IsSteamGame = false, ForceSteamOverlayTag = true };
                var card3 = new GameCardViewModel(forcedSteamCatGame, _ => {}, _ => {}, _ => {});
                if (!card3.HasSteamOverlay || card3.ShowCategoryBadge)
                    throw new Exception("Test failed: forcedSteamCatGame with Category=Steam should have HasSteamOverlay=true and ShowCategoryBadge=false");

                // Test GameEditViewModel mapping and saving
                var editVm = new GameEditViewModel(nonSteamGame, _mainViewModel.Categories, _iconExtractorService);
                if (editVm.ForceSteamOverlayTag)
                    throw new Exception("Test failed: editVm.ForceSteamOverlayTag should initially be false");

                editVm.ForceSteamOverlayTag = true;
                var saveMethod = typeof(GameEditViewModel).GetMethod("Save", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                saveMethod?.Invoke(editVm, null);

                if (!nonSteamGame.ForceSteamOverlayTag || !nonSteamGame.HasSteamOverlay)
                    throw new Exception("Test failed: saving editVm should set SourceGame.ForceSteamOverlayTag to true");

                // Test card RefreshProperties reflects saved state
                card1.RefreshProperties();
                if (!card1.HasSteamOverlay)
                    throw new Exception("Test failed: card1 should have HasSteamOverlay=true after RefreshProperties");

                Console.WriteLine("[TEST_FORCE_STEAM_OVERLAY_PASSED] All tests passed for ForceSteamOverlayTag!");
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-edit", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-edit", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                string sysDir = Environment.SystemDirectory;
                var sampleGame = _mainViewModel.Games.FirstOrDefault()?.Game ?? new GameEntry
                {
                    Name = "DOOM Eternal",
                    Category = "Action",
                    Hotkey = "Ctrl+Alt+D",
                    RunAsAdmin = true,
                    ExecutablePath = @"C:\Games\DOOM Eternal\DOOMEternalx64tk.exe",
                    WorkingDirectory = @"C:\Games\DOOM Eternal"
                };
                if (string.IsNullOrEmpty(sampleGame.IconPath) || !File.Exists(sampleGame.IconPath))
                {
                    string fallbackExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                    sampleGame.IconPath = _iconExtractorService.ExtractAndCacheIcon(sampleGame.Id, fallbackExe, sampleGame.Name);
                }

                var dlg = new GameEditDialog(sampleGame, _mainViewModel.Categories, _iconExtractorService);
                CaptureVisual(dlg, 550, 640, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-edit-overlap", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-edit-overlap", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                if (_mainViewModel.Games.Count == 0)
                {
                    string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    var sample = new GameEntry
                    {
                        Name = "Cyberpunk 2077",
                        ExecutablePath = Path.Combine(winDir, "notepad.exe"),
                        Category = "RPG"
                    };
                    sample.IconPath = _iconExtractorService.ExtractAndCacheIcon(sample.Id, sample.ExecutablePath, sample.Name);
                    _mainViewModel.Games.Add(new GameCardViewModel(sample, _mainViewModel.LaunchGame, _ => { }, _mainViewModel.DeleteGame));
                    _mainViewModel.RebuildCategories();
                }

                using var mainBmp = CaptureVisualBitmap(_mainWindow, 960, 700);
                var sampleGame = _mainViewModel.Games.First().Game;
                var dlg = new GameEditDialog(sampleGame, _mainViewModel.Categories, _iconExtractorService);
                using var dlgBmp = CaptureVisualBitmap(dlg, 550, 640);

                using var composite = new System.Drawing.Bitmap(mainBmp.Width, mainBmp.Height);
                using (var g = System.Drawing.Graphics.FromImage(composite))
                {
                    g.DrawImage(mainBmp, 0, 0);
                    int dx = (mainBmp.Width - dlgBmp.Width) / 2;
                    int dy = (mainBmp.Height - dlgBmp.Height) / 2;
                    using var shadowBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(160, 0, 0, 0));
                    g.FillRectangle(shadowBrush, dx - 12, dy - 8, dlgBmp.Width + 24, dlgBmp.Height + 20);
                    g.DrawImage(dlgBmp, dx, dy);
                }

                string? pDir = Path.GetDirectoryName(targetPng);
                if (!string.IsNullOrEmpty(pDir) && !Directory.Exists(pDir)) Directory.CreateDirectory(pDir);
                composite.Save(targetPng, System.Drawing.Imaging.ImageFormat.Png);
                ExitApplication();
                return;
            }

            if (e.Args[i].Equals("--screenshot-forced-card", StringComparison.OrdinalIgnoreCase) && i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                _mainViewModel.Games.Clear();
                var forcedSample = new GameEntry
                {
                    Name = "GOG Baldur's Gate 3",
                    ExecutablePath = @"C:\Games\BG3\bin\bg3.exe",
                    Category = "RPG",
                    IsSteamGame = false,
                    ForceSteamOverlayTag = true
                };
                string fallbackExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                forcedSample.IconPath = _iconExtractorService.ExtractAndCacheIcon(forcedSample.Id, fallbackExe, forcedSample.Name);
                _mainViewModel.Games.Add(new GameCardViewModel(forcedSample, _mainViewModel.LaunchGame, _ => { }, _mainViewModel.DeleteGame));
                _mainViewModel.RebuildCategories();

                CaptureVisual(_mainWindow, 960, 600, targetPng);
                ExitApplication();
                return;
            }

            if (e.Args[i].Equals("--test-details-vm", StringComparison.OrdinalIgnoreCase))
            {
                var testGame = new GameEntry { Name = "Cyberpunk 2077", Category = "RPG", CumulativePlaytimeMinutes = 90 };
                var meta = new SteamMetadataService();
                var search = new SteamSearchService();

                bool launched = false;
                bool edited = false;
                bool deleted = false;
                int closeCount = 0;

                var testVm = new GameDetailsViewModel(
                    testGame,
                    meta,
                    search,
                    launchAction: g => launched = true,
                    editAction: g => edited = true,
                    deleteAction: g => deleted = true
                );

                testVm.RequestClose += () => closeCount++;

                // Test Launch
                testVm.LaunchGameCommand.Execute(null);
                if (!launched || closeCount != 1) throw new Exception("Launch failed or didn't request close!");

                // Test Edit
                testVm.EditGameCommand.Execute(null);
                if (!edited || closeCount != 2) throw new Exception("Edit failed or didn't request close!");

                // Test Delete
                testVm.DeleteGameCommand.Execute(null);
                if (!deleted || closeCount != 3) throw new Exception("Delete failed or didn't request close!");

                // Test Properties
                if (testVm.PlaytimeDisplay != "1.5h played") throw new Exception($"Playtime mismatch: {testVm.PlaytimeDisplay}");
                if (testVm.Category != "RPG") throw new Exception($"Category mismatch: {testVm.Category}");

                Console.WriteLine("TEST_DETAILS_VM_PASSED");
                ExitApplication();
                return;
            }

            if (e.Args[i].Equals("--test-tray-tooltip", StringComparison.OrdinalIgnoreCase))
            {
                if (_trayIcon == null) InitializeTrayIcon();

                // Regression guard for the 1.4.0 bug: SessionStarted/SessionEnded are raised on
                // the launcher's monitor thread, and touching TaskbarIcon.ToolTipText from there
                // threw a cross-thread exception that UpdateTrayToolTip swallowed into a Verbose
                // log - so the tooltip silently never left its default. Drive it from a
                // background thread, the way the launcher does, and check the text really moved.
                _trayIcon.ToolTipText = "sentinel-value-that-must-be-replaced";

                Exception? escaped = null;
                var worker = new System.Threading.Thread(() =>
                {
                    try { UpdateTrayToolTip(); } catch (Exception ex) { escaped = ex; }
                });
                worker.Start();

                // The update is marshalled to this thread, so blocking on Join alone would
                // deadlock: pump the dispatcher while waiting for the callback to land.
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < deadline && _trayIcon.ToolTipText != DefaultTrayToolTip)
                {
                    Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                    System.Threading.Thread.Sleep(25);
                }
                worker.Join(TimeSpan.FromSeconds(5));

                if (escaped != null)
                    throw new Exception($"UpdateTrayToolTip threw on a background thread: {escaped.Message}");

                string actual = _trayIcon.ToolTipText;
                if (actual != DefaultTrayToolTip)
                    throw new Exception($"Tooltip was not updated from a background thread. Expected '{DefaultTrayToolTip}', got '{actual}'.");

                // Also to the log: this app is a WinExe, so Console output is unreliable when the
                // harness is driven from a script rather than a console.
                LoggingService.Info("TrayTooltipTest", $"[TEST_TRAY_TOOLTIP_PASSED] background-thread update produced '{actual}'");
                Console.WriteLine($"[TEST_TRAY_TOOLTIP_PASSED] background-thread update produced '{actual}'");
                ExitApplication();
                return;
            }

            if (e.Args[i].Equals("--test-tray-menu", StringComparison.OrdinalIgnoreCase))
            {
                if (_trayIcon == null) InitializeTrayIcon();
                // Test 1: ShowRecentInTray = true, with 0 played games
                _mainViewModel.Settings.ShowRecentInTray = true;
                _mainViewModel.Settings.GroupTrayMenuByCategory = true;
                foreach (var g in _mainViewModel.Games) g.Game.LastPlayed = null;
                UpdateTrayContextMenu();

                var menu = _trayIcon?.ContextMenu;
                if (menu == null) throw new Exception("ContextMenu was null!");

                // With 0 played games, placeholder MenuItem should be in the root menu items
                var placeholder = menu.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Header as string)?.Contains("No recently played games") == true);
                if (placeholder == null || placeholder.IsEnabled)
                    throw new Exception("Expected disabled placeholder item for Recent when 0 games played!");

                // Test 2: ShowRecentInTray = true, with 1 played game
                var firstGame = _mainViewModel.Games.First();
                firstGame.Game.LastPlayed = DateTime.Now;
                UpdateTrayContextMenu();
                menu = _trayIcon?.ContextMenu;

                // Recent game should now appear directly as a MenuItem in the root menu!
                var recentGameItem = menu?.Items.OfType<MenuItem>().FirstOrDefault(m => (string)m.Header == firstGame.Name);
                if (recentGameItem == null) throw new Exception($"Expected root game item '{firstGame.Name}', but none found!");

                // Test 3: ShowRecentInTray = false
                _mainViewModel.Settings.ShowRecentInTray = false;
                UpdateTrayContextMenu();
                menu = _trayIcon?.ContextMenu;
                placeholder = menu?.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Header as string)?.Contains("No recently played games") == true);
                if (placeholder != null) throw new Exception("Recent placeholder should not exist when ShowRecentInTray is false!");

                var recentHeader = menu?.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Header as string) == "RECENT");
                if (recentHeader != null) throw new Exception("Recent header should not exist when ShowRecentInTray is false!");

                // Reset game LastPlayed back to null so we don't alter user data
                firstGame.Game.LastPlayed = null;
                _mainViewModel.Settings.ShowRecentInTray = false;
                UpdateTrayContextMenu();

                Console.WriteLine("TEST_TRAY_MENU_PASSED");
                ExitApplication();
                return;
            }

            if (e.Args[i].Equals("--test-startup-reconcile", StringComparison.OrdinalIgnoreCase))
            {
                // The Run key is the truth for "Start with Windows"; settings.json only mirrors
                // it. Reconciling used to happen solely when the Settings page opened, so a fresh
                // install that ticked the installer's startup task carried StartWithWindows=false
                // in settings.json until the user wandered into Settings. Drive this with the two
                // deliberately out of step (registry on, stored value off) and the assert only
                // passes if startup reconciled them.
                bool registry = _startupManager.IsStartupEnabled();
                bool setting = _mainViewModel.SettingsVM.StartWithWindows;
                if (registry != setting)
                    throw new Exception($"Startup state not reconciled: Run key says {registry}, setting says {setting}.");

                LoggingService.Info("StartupReconcileTest", $"[TEST_STARTUP_RECONCILE_PASSED] Run key and setting agree ({registry})");
                Console.WriteLine($"[TEST_STARTUP_RECONCILE_PASSED] Run key and setting agree ({registry})");
                ExitApplication();
                return;
            }

            if (e.Args[i].Equals("--test-poster-zoom", StringComparison.OrdinalIgnoreCase))
            {
                // The hover zoom is driven by a Storyboard declared in Window.Resources but
                // targeting "CardBorder", a name that only exists inside the poster DataTemplate.
                // That resolves at trigger time, not compile time, so a build succeeding proves
                // nothing - a bad target name throws the moment the pointer touches a card. This
                // moves the real cursor onto a real card and reads the transform back.
                if (_mainViewModel.Games.Count == 0)
                {
                    string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    var sample = new GameEntry
                    {
                        Name = "Zoom Test",
                        Category = "Action",
                        ExecutablePath = Path.Combine(winDir, "explorer.exe"),
                    };
                    sample.IconPath = _iconExtractorService.ExtractAndCacheIcon(sample.Id, sample.ExecutablePath, sample.Name);
                    _mainViewModel.Games.Add(_mainViewModel.CreateCardViewModel(sample));
                    _mainViewModel.RebuildCategories();
                }

                _mainViewModel.CurrentSection = NavSection.Library;
                _mainViewModel.SettingsVM.LibraryViewMode = "Poster Grid";
                _mainWindow.Show();
                _mainWindow.Activate();
                // A process started in the background may be denied foreground activation, and
                // hover goes to whatever window is actually under the pointer - so pin it on top
                // for the duration or the test measures some other window.
                _mainWindow.Topmost = true;

                // The assertions run on a worker so the UI thread can return to its own message
                // loop: hover state and animation ticks both need it running, and nothing
                // driveable from inside OnStartup gives us that.
                var zoomWorker = new System.Threading.Thread(() =>
                {
                    string outcome;
                    GetCursorPos(out POINT restore);
                    try
                    {
                        System.Threading.Thread.Sleep(700); // let the grid lay out

                        var card = Dispatcher.Invoke(() =>
                            FindVisualChild<Border>(_mainWindow, b => b.Name == "CardBorder" && b.IsVisible));
                        if (card == null) throw new Exception("No visible poster CardBorder found to hover.");

                        // Re-read on every poll via ScaleOf: animating a sub-property path like
                        // RenderTransform.ScaleX can leave a different (unfrozen) transform instance
                        // on the element, so a cached reference goes on reporting the resting value.
                        var scale = Dispatcher.Invoke(() => card.RenderTransform as System.Windows.Media.ScaleTransform);
                        if (scale == null) throw new Exception("CardBorder.RenderTransform is not a ScaleTransform.");

                        // The part that can actually fail at runtime: the storyboards live in
                        // Window.Resources but target "CardBorder", a name that only exists inside
                        // the poster DataTemplate. That lookup happens when the animation starts,
                        // so the compiler proves nothing - a wrong name throws on first hover.
                        // Begin() resolves names against the element's own namescope, exactly as
                        // the trigger's BeginStoryboard does.
                        var zoomIn = Dispatcher.Invoke(() => (System.Windows.Media.Animation.Storyboard)_mainWindow.FindResource("PosterCardZoomIn"));
                        var zoomOut = Dispatcher.Invoke(() => (System.Windows.Media.Animation.Storyboard)_mainWindow.FindResource("PosterCardZoomOut"));

                        Dispatcher.Invoke(() => zoomIn.Begin(card, isControllable: true));
                        double peak = 1.0;
                        for (int t = 0; t < 80 && peak < 1.04; t++)
                        {
                            System.Threading.Thread.Sleep(25);
                            peak = Math.Max(peak, Dispatcher.Invoke(() => ScaleOf(card).ScaleX));
                        }
                        if (peak < 1.04) throw new Exception($"Zoom-in did not scale the card: ScaleX peaked at {peak:F3}, expected ~1.05.");

                        double sy = Dispatcher.Invoke(() => ScaleOf(card).ScaleY);
                        double sx = Dispatcher.Invoke(() => ScaleOf(card).ScaleX);
                        if (Math.Abs(sy - sx) > 0.001) throw new Exception($"Non-uniform zoom: ScaleX={sx:F3} ScaleY={sy:F3}.");

                        // Proof for the eye, captured while the card is still enlarged.
                        string shot = Path.Combine(Path.GetTempPath(), "traytrigger-poster-zoom.png");
                        Dispatcher.Invoke(() => CaptureVisual(_mainWindow, 1020, 760, shot));
                        LoggingService.Info("PosterZoomTest", $"captured zoomed card to {shot}");

                        // And it must come back down, or cards stay permanently enlarged.
                        Dispatcher.Invoke(() => zoomOut.Begin(card, isControllable: true));
                        double settled = 2.0;
                        for (int t = 0; t < 80 && settled > 1.001; t++)
                        {
                            System.Threading.Thread.Sleep(25);
                            settled = Dispatcher.Invoke(() => ScaleOf(card).ScaleX);
                        }
                        if (settled > 1.001) throw new Exception($"Card did not return to its resting size: ScaleX={settled:F3}.");

                        // Best effort on top of that: drive the real pointer and see whether the
                        // trigger fires by itself. Synthetic input does not reliably reach a
                        // background-launched window, so this reports but never fails the run.
                        var mid = Dispatcher.Invoke(() => card.PointToScreen(new Point(card.ActualWidth / 2, card.ActualHeight / 2)));
                        SetCursorPos((int)mid.X, (int)mid.Y);
                        double hoverPeak = 1.0;
                        for (int t = 0; t < 40 && hoverPeak < 1.04; t++)
                        {
                            System.Threading.Thread.Sleep(25);
                            hoverPeak = Math.Max(hoverPeak, Dispatcher.Invoke(() => ScaleOf(card).ScaleX));
                        }
                        bool hovered = Dispatcher.Invoke(() => card.IsMouseOver);
                        SetCursorPos(restore.X, restore.Y);
                        string hoverNote = hovered
                            ? $"live hover fired the trigger, ScaleX {hoverPeak:F3}"
                            : "live hover not reproducible here (synthetic input never reached the window)";

                        outcome = $"[TEST_POSTER_ZOOM_PASSED] storyboard zoomed to {peak:F3} and released to {settled:F3}; {hoverNote}";
                    }
                    catch (Exception ex)
                    {
                        outcome = "[TEST_POSTER_ZOOM_FAILED] " + ex.Message;
                    }
                    finally
                    {
                        SetCursorPos(restore.X, restore.Y);
                    }

                    LoggingService.Info("PosterZoomTest", outcome);
                    Console.WriteLine(outcome);
                    Dispatcher.Invoke(ExitApplication);
                }) { IsBackground = true };
                zoomWorker.Start();
                return;
            }

            if (e.Args[i].Equals("--test-context-menus", StringComparison.OrdinalIgnoreCase))
            {
                // Both menus live in Window.Resources and are parsed the first time one is shown,
                // not at build time - so a wrong x:Static enum member, a missing converter, or a
                // binding to a property that does not exist all survive a green build and throw
                // (or silently render an empty submenu) on the first right-click. This opens each
                // menu for real and reads back what it rendered.
                string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                _mainViewModel.Games.Clear();
                _mainViewModel.Games.Add(_mainViewModel.CreateCardViewModel(new GameEntry
                {
                    Name = "Menu Test Steam", Category = "Action", ExecutablePath = "steam://rungameid/220",
                    IsSteamGame = true, ImportedFrom = LauncherPlatform.Steam, SteamAppId = "220"
                }));
                _mainViewModel.Games.Add(_mainViewModel.CreateCardViewModel(new GameEntry
                {
                    Name = "Menu Test Local", Category = "Action",
                    ExecutablePath = Path.Combine(winDir, "explorer.exe")
                }));
                _mainViewModel.RebuildCategories();

                _mainViewModel.CurrentSection = NavSection.Library;
                _mainWindow.Show();
                _mainWindow.Activate();

                var menuWorker = new System.Threading.Thread(() =>
                {
                    string outcome;
                    try
                    {
                        System.Threading.Thread.Sleep(600);

                        var singleHeaders = Dispatcher.Invoke(() =>
                            OpenMenuAndReadHeaders("GameItemContextMenu", _mainViewModel.Games[0]));
                        System.Threading.Thread.Sleep(200);

                        foreach (string required in new[] { "Play", "Performance Profile", "CPU Cores", "Launch Options", "Change", "Steam", "Remove from Library" })
                        {
                            if (!singleHeaders.Contains(required))
                                throw new Exception($"Single-game menu is missing '{required}'. Rendered: {string.Join(" | ", singleHeaders)}");
                        }
                        // The submenus are the new part; an empty one means its items failed to build.
                        foreach (string child in new[] { "Optimized", "Aggressive", "Run as Administrator", "Close Launcher After Game Exits", "Match...", "Poster Artwork...", "Verify Game Files" })
                        {
                            if (!singleHeaders.Contains(child))
                                throw new Exception($"Single-game submenu item '{child}' did not render. Rendered: {string.Join(" | ", singleHeaders)}");
                        }

                        // Two selected: the batch menu is shown instead, and must offer the same
                        // three quick-setting submenus under the same names.
                        var batchHeaders = Dispatcher.Invoke(() =>
                        {
                            foreach (var card in _mainViewModel.Games) card.IsSelected = true;
                            _mainViewModel.Library.SelectAllCommand.Execute(null);
                            return OpenMenuAndReadHeaders("GameBatchContextMenu", _mainViewModel);
                        });
                        System.Threading.Thread.Sleep(200);

                        foreach (string required in new[] { "Performance Profile", "CPU Cores", "Launch Options", "Change Category...", "Remove from Library" })
                        {
                            if (!batchHeaders.Contains(required))
                                throw new Exception($"Batch menu is missing '{required}'. Rendered: {string.Join(" | ", batchHeaders)}");
                        }
                        if (!batchHeaders.Any(h => h.Contains("games selected", StringComparison.Ordinal)))
                            throw new Exception($"Batch menu lost its selection-count header. Rendered: {string.Join(" | ", batchHeaders)}");

                        outcome = $"[TEST_CONTEXT_MENUS_PASSED] single-game menu rendered {singleHeaders.Count} items, batch menu {batchHeaders.Count}";
                    }
                    catch (Exception ex)
                    {
                        outcome = "[TEST_CONTEXT_MENUS_FAILED] " + ex.Message;
                    }

                    LoggingService.Info("ContextMenuTest", outcome);
                    Console.WriteLine(outcome);
                    Dispatcher.Invoke(ExitApplication);
                }) { IsBackground = true };
                menuWorker.Start();
                return;
            }

            if (e.Args[i].Equals("--test-library-filter", StringComparison.OrdinalIgnoreCase))
            {
                // LibraryFilterTests covers the filtering rules. What it cannot cover is the
                // flyout: a Popup whose ItemsControl nests a second ItemsControl over two view
                // model types, bound through a converter to a property on a nested view model.
                // All of that resolves at render time, so a green build and a green unit suite
                // both prove nothing about whether a tick box ever appears on screen.
                string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string exe = Path.Combine(winDir, "explorer.exe");
                _mainViewModel.Games.Clear();
                _mainViewModel.Games.Add(_mainViewModel.CreateCardViewModel(new GameEntry
                {
                    Name = "Filter Test Steam", Category = "Action", ExecutablePath = "steam://rungameid/220",
                    IsSteamGame = true, ImportedFrom = LauncherPlatform.Steam
                }));
                _mainViewModel.Games.Add(_mainViewModel.CreateCardViewModel(new GameEntry
                {
                    Name = "Filter Test Local", Category = "Action", ExecutablePath = exe
                }));
                _mainViewModel.RebuildCategories();

                _mainViewModel.CurrentSection = NavSection.Library;
                _mainWindow.Show();
                _mainWindow.Activate();

                var filterWorker = new System.Threading.Thread(() =>
                {
                    string outcome;
                    try
                    {
                        System.Threading.Thread.Sleep(600); // let the toolbar lay out

                        var filter = _mainViewModel.Library.Filter;

                        // Filters persist to settings, so a run that threw before reaching Clear
                        // leaves a tick behind and the next run starts with games already hidden -
                        // which shows up as a baffling "expected 2 games, saw 1" that has nothing
                        // to do with the change under test. Start from a known-clear state.
                        Dispatcher.Invoke(filter.ClearAll);
                        System.Threading.Thread.Sleep(150);

                        // The popup is closed at rest, so its contents are not in the tree yet -
                        // opening it is what forces the templates to expand.
                        Dispatcher.Invoke(() => filter.IsOpen = true);
                        System.Threading.Thread.Sleep(500);

                        // Every touch of a DependencyObject stays inside a Dispatcher.Invoke: this
                        // thread does not own them, and popup.Child read from out here throws.
                        const string steamKey = "launcher:steam";
                        var (boxCount, renderedKeys, steamBox) = Dispatcher.Invoke(() =>
                        {
                            var popup = FindVisualChild<System.Windows.Controls.Primitives.Popup>(_mainWindow, p => p.IsOpen);
                            if (popup?.Child == null) return (0, new List<string>(), (CheckBox?)null);
                            var found = FindVisualChildren<CheckBox>(popup.Child).ToList();
                            var keys = found.Select(b => (b.DataContext as LibraryFilterOption)?.Key ?? "?").ToList();
                            return (found.Count, keys, found.FirstOrDefault(b => (b.DataContext as LibraryFilterOption)?.Key == steamKey));
                        });

                        if (boxCount == 0) throw new Exception("The filter popup did not open, or opened and rendered no tick boxes - a template or converter failed to resolve.");
                        if (steamBox == null) throw new Exception($"No '{steamKey}' tick box rendered. Rendered: {string.Join(", ", renderedKeys)}");

                        int before = Dispatcher.Invoke(() => _mainViewModel.Library.FilteredGames.Cast<object>().Count());

                        // Tick it the way a user does, through the CheckBox, not the view model -
                        // that is what exercises the two-way binding.
                        Dispatcher.Invoke(() => steamBox.IsChecked = true);
                        System.Threading.Thread.Sleep(250);

                        int after = Dispatcher.Invoke(() => _mainViewModel.Library.FilteredGames.Cast<object>().Count());
                        if (before != 2) throw new Exception($"Expected 2 games visible before filtering, saw {before}.");
                        if (after != 1) throw new Exception($"Ticking '{steamKey}' should leave 1 game visible, saw {after}.");
                        if (!Dispatcher.Invoke(() => filter.HasActiveFilters)) throw new Exception("HasActiveFilters stayed false - the count badge would never appear.");

                        // The badge is a Border+TextBlock bound to ActiveFilterCount inside the
                        // button's content Grid. Nothing above proves it rendered, or that the
                        // number in it is the count rather than, say, an unresolved binding.
                        string badge = Dispatcher.Invoke(() =>
                            !_mainWindow.LibraryFilterBadge.IsVisible
                                ? "<hidden>"
                                : (_mainWindow.LibraryFilterBadge.Child as TextBlock)?.Text ?? "<no text>");
                        if (badge != "1") throw new Exception($"The count badge did not render '1' for one active filter. Saw: '{badge}'.");

                        // The badge overlays the button's top-right corner, so it has to be square
                        // in that corner and it must not eat the clicks underneath it.
                        var badgeGeometry = Dispatcher.Invoke(() =>
                        {
                            var b = _mainWindow.LibraryFilterBadge;
                            var btn = _mainWindow.LibraryFilterButton;
                            var badgeTopRight = b.PointToScreen(new Point(b.ActualWidth, 0));
                            var buttonTopRight = btn.PointToScreen(new Point(btn.ActualWidth, 0));
                            var glyph = FindVisualChildren<TextBlock>(btn).FirstOrDefault();
                            var glyphMid = glyph == null ? new Point(double.NaN, double.NaN)
                                : glyph.PointToScreen(new Point(glyph.ActualWidth / 2, glyph.ActualHeight / 2));
                            return (Dx: badgeTopRight.X - buttonTopRight.X, Dy: badgeTopRight.Y - buttonTopRight.Y,
                                    CoversGlyphCentre: !double.IsNaN(glyphMid.X)
                                        && glyphMid.X >= badgeTopRight.X - b.ActualWidth && glyphMid.X <= badgeTopRight.X
                                        && glyphMid.Y >= badgeTopRight.Y && glyphMid.Y <= badgeTopRight.Y + b.ActualHeight,
                                    HitTestable: b.IsHitTestVisible);
                        });
                        if (Math.Abs(badgeGeometry.Dx) > 6 || Math.Abs(badgeGeometry.Dy) > 6)
                            throw new Exception($"The badge is not in the button's top-right corner (off by {badgeGeometry.Dx:0.#},{badgeGeometry.Dy:0.#}px) - the button template centres its content, which parks the badge on the glyph.");
                        if (badgeGeometry.CoversGlyphCentre)
                            throw new Exception("The badge is sitting on top of the filter glyph.");
                        if (badgeGeometry.HitTestable)
                            throw new Exception("The badge is hit-testable and will swallow clicks meant for the button.");

                        // Right edges should line up. A fixed HorizontalOffset only matched at one
                        // popup width; the placement callback has to hold at whatever width the
                        // content lands on.
                        double drift = Dispatcher.Invoke(() =>
                        {
                            var popup = FindVisualChild<System.Windows.Controls.Primitives.Popup>(_mainWindow, p => p.IsOpen);
                            if (popup?.Child is not FrameworkElement panel) return double.NaN;
                            double panelRight = panel.PointToScreen(new Point(panel.ActualWidth, 0)).X;
                            double buttonRight = _mainWindow.LibraryFilterButton.PointToScreen(new Point(_mainWindow.LibraryFilterButton.ActualWidth, 0)).X;
                            return Math.Abs(panelRight - buttonRight);
                        });
                        if (double.IsNaN(drift)) throw new Exception("Could not measure the flyout - it was not open when placement was checked.");
                        if (drift > 2) throw new Exception($"The flyout is not right-aligned with its button: {drift:0.#}px of drift.");

                        // Captured with the flyout shut, because that is the only state where the
                        // blue dot is the sole indication that games are being hidden. (A Popup is
                        // its own HWND and never appears in a PrintWindow capture anyway.)
                        Dispatcher.Invoke(() => filter.IsOpen = false);
                        System.Threading.Thread.Sleep(400);
                        string shot = Path.Combine(Path.GetTempPath(), "traytrigger-library-filter.png");
                        Dispatcher.Invoke(() => CaptureVisual(_mainWindow, 1020, 760, shot));

                        // Clear from inside the flyout, which is the only place it exists: it must
                        // empty the filter, untick the box on screen, and shut the flyout behind it.
                        Dispatcher.Invoke(() => filter.IsOpen = true);
                        System.Threading.Thread.Sleep(300);
                        Dispatcher.Invoke(() => filter.ClearCommand.Execute(null));
                        System.Threading.Thread.Sleep(250);
                        int cleared = Dispatcher.Invoke(() => _mainViewModel.Library.FilteredGames.Cast<object>().Count());
                        if (cleared != 2) throw new Exception($"Clear should restore all 2 games, saw {cleared}.");
                        if (Dispatcher.Invoke(() => steamBox.IsChecked == true)) throw new Exception("Clear emptied the filter but left the tick box checked on screen.");
                        if (Dispatcher.Invoke(() => filter.IsOpen)) throw new Exception("Clear left the flyout open - the Clear button hides itself at zero, so it vanishes under the pointer.");

                        if (Dispatcher.Invoke(() => _mainWindow.LibraryFilterBadge.IsVisible))
                            throw new Exception("The badge survived Clear - it should disappear at zero, not show a 0.");

                        outcome = $"[TEST_LIBRARY_FILTER_PASSED] {boxCount} tick boxes rendered; ticking Steam went {before} -> {after} games, badge read 1, flyout right-aligned within {drift:0.#}px, Clear restored {cleared} and closed the flyout; screenshot {shot}";
                    }
                    catch (Exception ex)
                    {
                        outcome = "[TEST_LIBRARY_FILTER_FAILED] " + ex.Message;
                    }

                    LoggingService.Info("LibraryFilterTest", outcome);
                    Console.WriteLine(outcome);
                    Dispatcher.Invoke(ExitApplication);
                }) { IsBackground = true };
                filterWorker.Start();
                return;
            }

            if (e.Args[i].Equals("--test-live-settings", StringComparison.OrdinalIgnoreCase))
            {
                // PerformanceProfileService used to be built with its convenience constructor,
                // whose provider is () => storageService.LoadSettings(): a disk read plus key
                // decryption on every profile apply and restore, returning a detached copy of
                // settings rather than the object the UI is editing. Reference equality is the
                // whole point of the fix, so assert exactly that.
                var live = _mainViewModel.Settings;
                var seen = _performanceProfileService.CurrentSettingsForTests;
                if (!ReferenceEquals(live, seen))
                    throw new Exception("PerformanceProfileService is not reading the live AppSettings instance - it is re-loading a detached copy from disk.");

                // And prove it follows unsaved edits, which a disk-reading provider cannot.
                bool original = live.CreateRestorePointBeforeTweaks;
                try
                {
                    live.CreateRestorePointBeforeTweaks = !original;
                    if (_performanceProfileService.CurrentSettingsForTests.CreateRestorePointBeforeTweaks != !original)
                        throw new Exception("PerformanceProfileService did not observe an unsaved in-memory settings change.");
                }
                finally
                {
                    live.CreateRestorePointBeforeTweaks = original;
                }

                LoggingService.Info("LiveSettingsTest", "[TEST_LIVE_SETTINGS_PASSED] profile service reads the live AppSettings instance");
                Console.WriteLine("[TEST_LIVE_SETTINGS_PASSED] profile service reads the live AppSettings instance");
                ExitApplication();
                return;
            }

            if (e.Args[i].Equals("--test-dialog-cloak", StringComparison.OrdinalIgnoreCase))
            {
                // Regression guard for the 1.4.1-beta.2 bug: FileDialogCloak polled for a
                // *direct child* named DirectUIHWND to decide the shell view had painted. The
                // folder picker nests its view deeper, so that never matched and every "Add
                // Folder" dialog stayed cloaked until the 450 ms fail-safe - visible in the log
                // only as "fail-safe at 739 ms" where a working detection says "painted".
                // Both dialog types are driven here because only the folder one regressed, and
                // nothing would have caught it: the fail-safe makes a broken detector look fine.
                var cloakFailures = new List<string>();
                foreach (var (label, dialog) in new (string, Microsoft.Win32.CommonDialog)[]
                {
                    ("OpenFolderDialog", new Microsoft.Win32.OpenFolderDialog { Title = "Cloak test (closes itself)" }),
                    ("OpenFileDialog", new Microsoft.Win32.OpenFileDialog { Title = "Cloak test (closes itself)", Filter = "All files (*.*)|*.*" }),
                })
                {
                    // ShowDialog blocks this thread, so the auto-close has to come from another
                    // one. It waits out the full fail-safe window before closing, otherwise the
                    // dialog could vanish before the detector had a chance either way.
                    var closer = new System.Threading.Thread(() =>
                    {
                        var deadline = DateTime.UtcNow.AddSeconds(10);
                        while (DateTime.UtcNow < deadline)
                        {
                            System.Threading.Thread.Sleep(50);
                            IntPtr h = FileDialogCloak.LastDialogHandle;
                            if (h == IntPtr.Zero || FileDialogCloak.LastRevealReason == null) continue;
                            System.Threading.Thread.Sleep(150);
                            PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                            return;
                        }
                    }) { IsBackground = true };
                    closer.Start();

                    FileDialogCloak.Show(dialog);
                    closer.Join(TimeSpan.FromSeconds(12));

                    string reason = FileDialogCloak.LastRevealReason ?? "<never revealed>";
                    LoggingService.Info("DialogCloakTest", $"{label}: revealed via '{reason}'");
                    if (!reason.Contains("painted", StringComparison.Ordinal))
                    {
                        cloakFailures.Add($"{label} revealed via '{reason}' - shell view was never detected.");
                    }
                }

                if (cloakFailures.Count > 0)
                    throw new Exception("Dialog cloak detection failed: " + string.Join(" | ", cloakFailures));

                LoggingService.Info("DialogCloakTest", "[TEST_DIALOG_CLOAK_PASSED] both dialog types revealed on shell-view detection, not the fail-safe");
                Console.WriteLine("[TEST_DIALOG_CLOAK_PASSED] both dialog types revealed on shell-view detection, not the fail-safe");
                ExitApplication();
                return;
            }

            if (e.Args[i].Equals("--test-reset-settings", StringComparison.OrdinalIgnoreCase))
            {
                // Mutate some settings
                _mainViewModel.SettingsVM.ShowRecentInTray = false;
                _mainViewModel.SettingsVM.MaxRecentInTray = 15;
                _mainViewModel.SettingsVM.GroupTrayMenuByCategory = false;
                _mainViewModel.SettingsVM.TrayMenuSortOption = "Cumulative Playtime";
                _mainViewModel.SettingsVM.VerboseLoggingEnabled = true;

                // Reset to defaults without prompt
                _mainViewModel.SettingsVM.ResetSettingsToDefaults(promptConfirm: false);

                // Assert expected defaults
                if (!_mainViewModel.SettingsVM.ShowRecentInTray) throw new Exception("Expected ShowRecentInTray=true after reset");
                if (_mainViewModel.SettingsVM.MaxRecentInTray != 5) throw new Exception($"Expected MaxRecentInTray=5 after reset, got {_mainViewModel.SettingsVM.MaxRecentInTray}");
                if (!_mainViewModel.SettingsVM.GroupTrayMenuByCategory) throw new Exception("Expected GroupTrayMenuByCategory=true after reset");
                if (_mainViewModel.SettingsVM.TrayMenuSortOption != "Alphabetical (A - Z)") throw new Exception($"Expected TrayMenuSortOption='Alphabetical (A - Z)' after reset, got {_mainViewModel.SettingsVM.TrayMenuSortOption}");
                if (_mainViewModel.SettingsVM.VerboseLoggingEnabled) throw new Exception("Expected VerboseLoggingEnabled=false after reset");
                if (!_mainViewModel.SettingsVM.AlwaysShowTrayIcon) throw new Exception("Expected AlwaysShowTrayIcon=true after reset");
                if (!_mainViewModel.SettingsVM.AutoCategorizeFromSteam) throw new Exception("Expected AutoCategorizeFromSteam=true after reset");
                if (!_mainViewModel.SettingsVM.SearchOfficialTitleOnline) throw new Exception("Expected SearchOfficialTitleOnline=true after reset");
                if (!_mainViewModel.SettingsVM.UseVerticalPosterArt) throw new Exception("Expected UseVerticalPosterArt=true after reset");
                if (_mainViewModel.SettingsVM.UseRawgMetadata) throw new Exception("Expected UseRawgMetadata=false after reset");
                if (_mainViewModel.SettingsVM.UseSteamGridDbArt) throw new Exception("Expected UseSteamGridDbArt=false after reset");
                if (_mainViewModel.SettingsVM.StartWithWindows) throw new Exception("Expected StartWithWindows=false after reset");
                if (!_mainViewModel.SettingsVM.StartMinimizedToTray) throw new Exception("Expected StartMinimizedToTray=true after reset");

                Console.WriteLine("TEST_RESET_SETTINGS_PASSED");
                ExitApplication();
                return;
            }

            if (e.Args[i].Equals("--test-settings-navigation", StringComparison.OrdinalIgnoreCase))
            {
                _mainViewModel.CurrentSection = NavSection.About;
                if (_mainViewModel.CurrentSection != NavSection.About) throw new Exception("Expected About section");

                _mainViewModel.OpenDiagnosticsSettingsCommand.Execute(null);
                if (_mainViewModel.CurrentSection != NavSection.Settings) throw new Exception("Expected Settings section after OpenDiagnosticsSettings");
                if (_mainViewModel.SettingsVM.SelectedTab != SettingsCategoryTab.Diagnostics) throw new Exception("Expected Diagnostics tab");
                if (!_mainViewModel.SettingsVM.ShowDiagnosticsSection) throw new Exception("Expected ShowDiagnosticsSection to be true");
                if (_mainViewModel.SettingsVM.ShowGeneralSection) throw new Exception("Expected ShowGeneralSection to be false on Diagnostics tab");
                if (_mainViewModel.SettingsVM.ShowLibrarySection) throw new Exception("Expected ShowLibrarySection to be false on Diagnostics tab");
                if (_mainViewModel.SettingsVM.ShowTrayMenuSection) throw new Exception("Expected ShowTrayMenuSection to be false on Diagnostics tab");

                _mainViewModel.SettingsVM.SelectGeneralTabCommand.Execute(null);
                if (!_mainViewModel.SettingsVM.ShowGeneralSection) throw new Exception("Expected ShowGeneralSection to be true on General tab");
                if (_mainViewModel.SettingsVM.ShowDiagnosticsSection) throw new Exception("Expected ShowDiagnosticsSection to be false on General tab");

                _mainViewModel.SettingsVM.SelectAllTabCommand.Execute(null);
                if (!_mainViewModel.SettingsVM.ShowGeneralSection ||
                    !_mainViewModel.SettingsVM.ShowLibrarySection ||
                    !_mainViewModel.SettingsVM.ShowTrayMenuSection ||
                    !_mainViewModel.SettingsVM.ShowDiagnosticsSection)
                    throw new Exception("Expected all sections visible on All tab");

                Console.WriteLine("TEST_SETTINGS_NAVIGATION_PASSED");
                ExitApplication();
                return;
            }

            if (e.Args[i].Equals("--test-system-navigation", StringComparison.OrdinalIgnoreCase))
            {
                _mainViewModel.CurrentSection = NavSection.System;
                if (_mainViewModel.CurrentSection != NavSection.System) throw new Exception("Expected System section");

                _mainViewModel.SystemVM.SelectSpecsTabCommand.Execute(null);
                if (_mainViewModel.SystemVM.CurrentSubSection != SystemSubSection.HardwareSpecs) throw new Exception("Expected HardwareSpecs sub-section");
                if (!_mainViewModel.SystemVM.IsSpecsTab) throw new Exception("Expected IsSpecsTab=true");
                if (_mainViewModel.SystemVM.IsAllTab) throw new Exception("Expected IsAllTab=false");
                if (_mainViewModel.SystemVM.IsTweaksTab) throw new Exception("Expected IsTweaksTab=false");
                if (!_mainViewModel.SystemVM.ShowSpecsSection) throw new Exception("Expected ShowSpecsSection=true on Specs tab");
                if (_mainViewModel.SystemVM.ShowTweaksSection) throw new Exception("Expected ShowTweaksSection=false on Specs tab");

                _mainViewModel.SystemVM.SelectTweaksTabCommand.Execute(null);
                if (_mainViewModel.SystemVM.CurrentSubSection != SystemSubSection.PerformanceTweaks) throw new Exception("Expected PerformanceTweaks sub-section");
                if (!_mainViewModel.SystemVM.IsTweaksTab) throw new Exception("Expected IsTweaksTab=true");
                if (_mainViewModel.SystemVM.IsSpecsTab) throw new Exception("Expected IsSpecsTab=false");
                if (_mainViewModel.SystemVM.IsAllTab) throw new Exception("Expected IsAllTab=false");
                if (!_mainViewModel.SystemVM.ShowTweaksSection) throw new Exception("Expected ShowTweaksSection=true on Tweaks tab");
                if (_mainViewModel.SystemVM.ShowSpecsSection) throw new Exception("Expected ShowSpecsSection=false on Tweaks tab");

                _mainViewModel.SystemVM.SelectAllTabCommand.Execute(null);
                if (_mainViewModel.SystemVM.CurrentSubSection != SystemSubSection.All) throw new Exception("Expected All sub-section");
                if (!_mainViewModel.SystemVM.IsAllTab) throw new Exception("Expected IsAllTab=true");
                if (_mainViewModel.SystemVM.IsSpecsTab) throw new Exception("Expected IsSpecsTab=false");
                if (_mainViewModel.SystemVM.IsTweaksTab) throw new Exception("Expected IsTweaksTab=false");
                if (!_mainViewModel.SystemVM.ShowSpecsSection) throw new Exception("Expected ShowSpecsSection=true on All tab");
                if (!_mainViewModel.SystemVM.ShowTweaksSection) throw new Exception("Expected ShowTweaksSection=true on All tab");

                Console.WriteLine("TEST_SYSTEM_NAVIGATION_PASSED");
                ExitApplication();
                return;
            }

            if (e.Args[i].Equals("--test-about-navigation", StringComparison.OrdinalIgnoreCase))
            {
                _mainViewModel.CurrentSection = NavSection.About;
                if (_mainViewModel.CurrentSection != NavSection.About) throw new Exception("Expected About section");
                if (!_mainViewModel.IsAboutView) throw new Exception("Expected IsAboutView=true");

                _mainViewModel.SelectAboutOverviewTabCommand.Execute(null);
                if (_mainViewModel.CurrentAboutSection != AboutSubSection.Overview) throw new Exception("Expected Overview sub-section");
                if (!_mainViewModel.IsAboutOverviewTab) throw new Exception("Expected IsAboutOverviewTab=true");
                if (_mainViewModel.IsAboutAllTab) throw new Exception("Expected IsAboutAllTab=false");
                if (!_mainViewModel.ShowAboutOverviewSection) throw new Exception("Expected ShowAboutOverviewSection=true on Overview tab");
                if (_mainViewModel.ShowAboutFeaturesSection) throw new Exception("Expected ShowAboutFeaturesSection=false on Overview tab");

                _mainViewModel.SelectAboutFeaturesTabCommand.Execute(null);
                if (_mainViewModel.CurrentAboutSection != AboutSubSection.Features) throw new Exception("Expected Features sub-section");
                if (!_mainViewModel.IsAboutFeaturesTab) throw new Exception("Expected IsAboutFeaturesTab=true");
                if (!_mainViewModel.ShowAboutFeaturesSection) throw new Exception("Expected ShowAboutFeaturesSection=true on Features tab");
                if (_mainViewModel.ShowAboutOverviewSection) throw new Exception("Expected ShowAboutOverviewSection=false on Features tab");

                _mainViewModel.SelectAboutShortcutsTabCommand.Execute(null);
                if (_mainViewModel.CurrentAboutSection != AboutSubSection.Shortcuts) throw new Exception("Expected Shortcuts sub-section");
                if (!_mainViewModel.IsAboutShortcutsTab) throw new Exception("Expected IsAboutShortcutsTab=true");
                if (!_mainViewModel.ShowAboutShortcutsSection) throw new Exception("Expected ShowAboutShortcutsSection=true on Shortcuts tab");

                _mainViewModel.SelectAboutSupportTabCommand.Execute(null);
                if (_mainViewModel.CurrentAboutSection != AboutSubSection.Support) throw new Exception("Expected Support sub-section");
                if (!_mainViewModel.IsAboutSupportTab) throw new Exception("Expected IsAboutSupportTab=true");
                if (!_mainViewModel.ShowAboutSupportSection) throw new Exception("Expected ShowAboutSupportSection=true on Support tab");

                _mainViewModel.SelectAboutAllTabCommand.Execute(null);
                if (_mainViewModel.CurrentAboutSection != AboutSubSection.All) throw new Exception("Expected All sub-section");
                if (!_mainViewModel.IsAboutAllTab) throw new Exception("Expected IsAboutAllTab=true");
                if (!_mainViewModel.ShowAboutOverviewSection) throw new Exception("Expected ShowAboutOverviewSection=true on All tab");
                if (!_mainViewModel.ShowAboutFeaturesSection) throw new Exception("Expected ShowAboutFeaturesSection=true on All tab");
                if (!_mainViewModel.ShowAboutShortcutsSection) throw new Exception("Expected ShowAboutShortcutsSection=true on All tab");
                if (!_mainViewModel.ShowAboutSupportSection) throw new Exception("Expected ShowAboutSupportSection=true on All tab");

                Console.WriteLine("TEST_ABOUT_NAVIGATION_PASSED");
                ExitApplication();
                return;
            }

            if (e.Args[i].Equals("--test-library-tabs", StringComparison.OrdinalIgnoreCase))
            {
                _mainViewModel.CurrentSection = NavSection.Library;
                _mainViewModel.Games.Clear();

                var g1 = new GameEntry { Name = "Cyberpunk 2077", Category = "RPG" };
                var g2 = new GameEntry { Name = "Elden Ring", Category = "Action" };
                var g3 = new GameEntry { Name = "Portal 2", Category = "Steam", IsSteamGame = true };
                _mainViewModel.Games.Add(_mainViewModel.CreateCardViewModel(g1));
                _mainViewModel.Games.Add(_mainViewModel.CreateCardViewModel(g2));
                _mainViewModel.Games.Add(_mainViewModel.CreateCardViewModel(g3));
                _mainViewModel.RebuildCategories();

                if (_mainViewModel.CategoryTabs.Count != 4)
                    throw new Exception($"Expected 4 category tabs, got {_mainViewModel.CategoryTabs.Count}");

                var allTab = _mainViewModel.CategoryTabs.FirstOrDefault(t => t.Name == "All");
                var rpgTab = _mainViewModel.CategoryTabs.FirstOrDefault(t => t.Name == "RPG");
                var actTab = _mainViewModel.CategoryTabs.FirstOrDefault(t => t.Name == "Action");
                var steamTab = _mainViewModel.CategoryTabs.FirstOrDefault(t => t.Name == "Steam");

                if (allTab == null || rpgTab == null || actTab == null || steamTab == null)
                    throw new Exception("Missing expected category tabs");

                if (allTab.DisplayName != "All Games")
                    throw new Exception($"Expected allTab.DisplayName='All Games', got '{allTab.DisplayName}'");

                // Switch to RPG
                rpgTab.SelectCommand.Execute(null);
                if (_mainViewModel.SelectedCategory != "RPG")
                    throw new Exception($"Expected SelectedCategory='RPG', got '{_mainViewModel.SelectedCategory}'");
                if (!rpgTab.IsSelected || allTab.IsSelected || actTab.IsSelected || steamTab.IsSelected)
                    throw new Exception("Tab IsSelected flags incorrect after selecting RPG");

                // Switch back to All
                allTab.SelectCommand.Execute(null);
                if (_mainViewModel.SelectedCategory != "All")
                    throw new Exception($"Expected SelectedCategory='All', got '{_mainViewModel.SelectedCategory}'");
                if (!allTab.IsSelected || rpgTab.IsSelected)
                    throw new Exception("Tab IsSelected flags incorrect after selecting All");

                Console.WriteLine("TEST_LIBRARY_TABS_PASSED");
                ExitApplication();
                return;
            }



            if ((e.Args[i].Equals("--screenshot-confirm", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-confirm", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                var dlg = new ModernDialog(
                    "Remove from Library",
                    "Are you sure you want to remove \"The Blood of Dawnwalker\" from your library?",
                    "This will only remove the shortcut from TrayTrigger. Your installed game files will not be deleted.",
                    "Remove",
                    "Cancel",
                    DialogIconType.Delete);
                CaptureVisual(dlg, 470, 200, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-details", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-details", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                var game = new GameEntry
                {
                    Name = "The Blood of Dawnwalker",
                    Category = "Action RPG",
                    SteamAppId = "3751260",
                    IsSteamGame = false,
                    CumulativePlaytimeMinutes = 145,
                    LastPlayed = DateTime.Now.AddHours(-3)
                };

                var metaService = new SteamMetadataService();
                var searchService = new SteamSearchService();
                var vm = new GameDetailsViewModel(game, metaService, searchService);

                var sampleDetails = new SteamAppDetails
                {
                    AppId = "3751260",
                    Name = "The Blood of Dawnwalker",
                    CoverImagePath = Path.Combine(SteamMetadataService.CoversDirectory, "3751260.jpg"),
                    Developers = "Rebel Wolves",
                    Publishers = "Bandai Namco Entertainment",
                    ReleaseDate = "Coming Soon (2025)",
                    ShortDescription = "Dawnwalker is a dark fantasy narrative-action RPG set in medieval Europe. Unravel ancient curses and wage tactical war against monstrous adversaries across a dynamic, blood-drenched open world.",
                    MetacriticScore = 88,
                    ReviewScorePercent = 91,
                    ReviewSummary = "Very Positive (91% of 14,280 reviews)",
                    PcRequirementsMin = "• OS: Windows 10/11 64-bit\n• Processor: Intel Core i5-8400 or AMD Ryzen 5 2600X\n• Memory: 16 GB RAM\n• Graphics: NVIDIA GeForce RTX 2060 (6GB) or AMD Radeon RX 5600 XT\n• DirectX: Version 12 (DirectX 12 Ultimate)\n• Storage: 85 GB available space (SSD required)",
                    PcRequirementsRec = "• OS: Windows 11 64-bit\n• Processor: Intel Core i7-12700K or AMD Ryzen 7 7800X3D\n• Memory: 32 GB RAM\n• Graphics: NVIDIA GeForce RTX 4070 (12GB) or AMD Radeon RX 7800 XT\n• DirectX: Version 12\n• Storage: 85 GB NVMe SSD"
                };
                sampleDetails.Genres.Add("Action");
                sampleDetails.Genres.Add("RPG");
                sampleDetails.Genres.Add("Dark Fantasy");
                sampleDetails.Genres.Add("Open World");
                sampleDetails.PlayModes.Add("Full controller support");
                sampleDetails.PlayModes.Add("Single-player");
                sampleDetails.PlayModes.Add("Steam Achievements");
                sampleDetails.PlayModes.Add("Steam Cloud");
                sampleDetails.NewsItems.Add(new SteamNewsItem
                {
                    Title = "Developer Update #4: World Building & Combat Systems",
                    Date = DateTime.Now.AddDays(-6),
                    Author = "Rebel Wolves Team",
                    Snippet = "Take a deep dive into the combat philosophy behind Dawnwalker, showcasing dynamic swordplay, visceral animations, and dark magic combinations in our latest dev diary."
                });
                sampleDetails.NewsItems.Add(new SteamNewsItem
                {
                    Title = "Teaser Trailer Revealed at Summer Game Fest",
                    Date = DateTime.Now.AddDays(-28),
                    Author = "Bandai Namco",
                    Snippet = "Watch the premiere teaser trailer for The Blood of Dawnwalker and get your first glimpse of the cursed European frontier."
                });
                vm.Details = sampleDetails;
                vm.IsLoading = false;

                var dlg = new GameDetailsDialog(vm);
                CaptureVisual(dlg, 820, 680, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-hover", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-hover", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];

                if (_mainViewModel.Games.Count == 0)
                {
                    string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    var sample = new GameEntry
                    {
                        Name = "Cyberpunk 2077",
                        Category = "Action",
                        ExecutablePath = Path.Combine(winDir, "explorer.exe"),
                        CumulativePlaytimeMinutes = 120,
                        LastPlayed = DateTime.Now.AddHours(-1)
                    };
                    sample.IconPath = _iconExtractorService.ExtractAndCacheIcon(sample.Id, sample.ExecutablePath, sample.Name);
                    _mainViewModel.Games.Add(_mainViewModel.CreateCardViewModel(sample));
                    _mainViewModel.RebuildCategories();
                }

                _mainWindow.Show();
                _mainWindow.UpdateLayout();

                var cardBorder = FindVisualChild<Border>(_mainWindow, b => b.Name == "CardBorder");
                if (cardBorder != null)
                {
                    cardBorder.BorderBrush = (System.Windows.Media.Brush)_mainWindow.FindResource("BrushAccent");
                }
                CaptureVisual(_mainWindow, 960, 700, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-nav-settings", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-nav-settings", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                _mainViewModel.CurrentSection = NavSection.Settings;
                _mainWindow.Show();
                _mainWindow.UpdateLayout();
                CaptureVisual(_mainWindow, 960, 700, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-card-menu", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-card-menu", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];

                var testWin = new Window
                {
                    Title = "Card Context Menu Preview",
                    Width = 320,
                    Height = 360,
                    Background = new SolidColorBrush(Color.FromRgb(18, 18, 20)),
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };

                var container = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(23, 23, 28)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(44, 44, 56)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(5),
                    Margin = new Thickness(20),
                    VerticalAlignment = VerticalAlignment.Center
                };

                // Render the real GameItemContextMenu (not a hand-built mock) against the first
                // library card, so the preview tracks every later change to the menu. The
                // items are moved into a StackPanel because a ContextMenu can't be captured
                // without being opened; their visibility triggers bind to the panel's DataContext.
                var sampleCard = _mainViewModel.Games.FirstOrDefault();
                if (sampleCard == null)
                {
                    var sample = new GameEntry { Name = "Cyberpunk 2077", Category = "RPG", ExecutablePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe") };
                    sampleCard = _mainViewModel.CreateCardViewModel(sample);
                }

                var realMenu = (ContextMenu)_mainWindow.FindResource("GameItemContextMenu");
                var panel = new StackPanel { DataContext = sampleCard };
                var menuItems = realMenu.Items.OfType<UIElement>().ToList();
                realMenu.Items.Clear();
                foreach (var item in menuItems)
                {
                    panel.Children.Add(item);
                }

                container.Child = panel;
                testWin.Content = container;
                testWin.Height = 700;

                CaptureVisual(testWin, 320, 700, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-tray-menu", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-tray-menu", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];

                if (_trayIcon == null) InitializeTrayIcon();
                _mainViewModel.Settings.ShowRecentInTray = true;
                _mainViewModel.Settings.GroupTrayMenuByCategory = false;
                if (_mainViewModel.Games.Count > 0)
                {
                    _mainViewModel.Games[0].Game.LastPlayed = DateTime.Now.AddHours(-2);
                }
                UpdateTrayContextMenu();

                var trayMenu = _trayIcon?.ContextMenu;
                if (trayMenu == null) throw new Exception("ContextMenu was null!");

                var testWin = new Window
                {
                    Title = "Tray Menu Preview",
                    Width = 380,
                    Height = 640,
                    Background = new SolidColorBrush(Color.FromRgb(14, 14, 18)),
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };

                var container = new Border
                {
                    Background = (Brush)FindResource("BrushBgDark"),
                    Padding = new Thickness(25),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                var menuBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(21, 21, 28)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(44, 44, 60)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(6),
                    MinWidth = 275
                };
                menuBorder.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 24,
                    ShadowDepth = 6,
                    Opacity = 0.7,
                    Color = Colors.Black
                };

                var panel = new StackPanel();
                var items = trayMenu.Items.OfType<UIElement>().ToList();
                trayMenu.Items.Clear();
                foreach (var item in items)
                {
                    panel.Children.Add(item);
                }

                menuBorder.Child = panel;
                container.Child = menuBorder;
                testWin.Content = container;

                CaptureVisual(testWin, 420, 640, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-tray-menu-grouped", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-tray-menu-grouped", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];

                if (_trayIcon == null) InitializeTrayIcon();
                _mainViewModel.Settings.ShowRecentInTray = true;
                _mainViewModel.Settings.GroupTrayMenuByCategory = true;
                if (_mainViewModel.Games.Count > 0)
                {
                    _mainViewModel.Games[0].Game.LastPlayed = DateTime.Now.AddHours(-2);
                }
                UpdateTrayContextMenu();

                var trayMenu = _trayIcon?.ContextMenu;
                if (trayMenu == null) throw new Exception("ContextMenu was null!");

                var testWin = new Window
                {
                    Title = "Tray Menu Grouped Preview",
                    Width = 420,
                    Height = 640,
                    Background = new SolidColorBrush(Color.FromRgb(14, 14, 18)),
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };

                var container = new Border
                {
                    Background = (Brush)FindResource("BrushBgDark"),
                    Padding = new Thickness(25),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                var menuBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(21, 21, 28)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(44, 44, 60)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(6),
                    MinWidth = 275
                };
                menuBorder.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 24,
                    ShadowDepth = 6,
                    Opacity = 0.7,
                    Color = Colors.Black
                };

                var panel = new StackPanel();
                var items = trayMenu.Items.OfType<UIElement>().ToList();
                trayMenu.Items.Clear();
                foreach (var item in items)
                {
                    panel.Children.Add(item);
                }

                menuBorder.Child = panel;
                container.Child = menuBorder;
                testWin.Content = container;

                CaptureVisual(testWin, 420, 640, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-picker", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-picker", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                var sampleCandidates = new List<GameCandidate>
                {
                    new GameCandidate("DOOM Eternal", @"C:\Games\DOOM Eternal\DOOMEternalx64tk.exe", @"C:\Games\DOOM Eternal", 88400000, 140, @"DOOMEternalx64tk.exe"),
                    new GameCandidate("DOOM Benchmark", @"C:\Games\DOOM Eternal\tools\DOOM_Benchmark.exe", @"C:\Games\DOOM Eternal\tools", 12400000, 20, @"tools\DOOM_Benchmark.exe"),
                    new GameCandidate("DOOM Config Tool", @"C:\Games\DOOM Eternal\tools\DOOMConfig.exe", @"C:\Games\DOOM Eternal\tools", 4500000, 10, @"tools\DOOMConfig.exe")
                };
                var picker = new GameCandidatePickerDialog(@"C:\Games\DOOM Eternal", sampleCandidates);
                CaptureVisual(picker, 540, 460, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-batch", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-batch", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                string sysDir = Environment.SystemDirectory;
                string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                var sampleCandidates = new List<GameCandidate>
                {
                    new GameCandidate("Baldur's Gate 3", Path.Combine(winDir, "explorer.exe"), @"D:\SteamLibrary\steamapps\common\Baldurs Gate 3\bin", 125000000, 160, @"bin\bg3.exe"),
                    new GameCandidate("Cyberpunk 2077", Path.Combine(sysDir, "cmd.exe"), @"D:\SteamLibrary\steamapps\common\Cyberpunk 2077\bin\x64", 78000000, 150, @"bin\x64\Cyberpunk2077.exe"),
                    new GameCandidate("Elden Ring", Path.Combine(sysDir, "notepad.exe"), @"D:\SteamLibrary\steamapps\common\ELDEN RING\Game", 65000000, 135, @"Game\eldenring.exe"),
                    new GameCandidate("Hades II", Path.Combine(sysDir, "taskmgr.exe"), @"D:\SteamLibrary\steamapps\common\Hades II\Ship", 35000000, 140, @"Ship\Hades2.exe"),
                    new GameCandidate("Silksong", Path.Combine(sysDir, "calc.exe"), @"D:\SteamLibrary\steamapps\common\Hollow Knight Silksong", 42000000, 140, @"Silksong.exe")
                };
                var existingPaths = new List<string>
                {
                    Path.Combine(sysDir, "cmd.exe")
                };
                var dlg = new FolderBatchImportDialog(@"D:\SteamLibrary\steamapps\common", sampleCandidates, c => existingPaths.Contains(c.ExePath, StringComparer.OrdinalIgnoreCase), isAlreadyScanLocation: false, onIgnoreCandidate: _ => { });
                CaptureVisual(dlg, 680, 560, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-batch-count", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-batch-count", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                string sysDir = Environment.SystemDirectory;
                string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                var sampleCandidates = new List<GameCandidate>
                {
                    new GameCandidate("Alan Wake 2", Path.Combine(winDir, "explorer.exe"), @"C:\Games\Alan Wake 2", 85000000, 150, "AlanWake2.exe"),
                    new GameCandidate("Dragon Ball Z: Kakarot", Path.Combine(sysDir, "cmd.exe"), @"C:\Games\DBZ Kakarot", 42000000, 140, "DBZ.exe"),
                    new GameCandidate("The Blood of Dawnwalker", Path.Combine(sysDir, "notepad.exe"), @"C:\Games\Dawnwalker", 78000000, 160, "Dawnwalker.exe")
                };
                var dlg = new FolderBatchImportDialog(@"C:\Games", sampleCandidates, _ => false, isAlreadyScanLocation: false, onIgnoreCandidate: _ => { });
                if (dlg.DataContext is FolderBatchImportViewModel vm && vm.Games.Count >= 3)
                {
                    // Unselect the first game (Alan Wake 2)
                    vm.Games[0].IsSelected = false;
                }
                CaptureVisual(dlg, 680, 560, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-batch-snapped", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-batch-snapped", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                string sysDir = Environment.SystemDirectory;
                string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

                using var mainBmp = CaptureVisualBitmap(_mainWindow, 960, 700);

                var sampleCandidates = new List<GameCandidate>
                {
                    new GameCandidate("Alan Wake 2", Path.Combine(winDir, "explorer.exe"), @"C:\Games\Alan Wake 2", 85000000, 150, "AlanWake2.exe"),
                    new GameCandidate("Dragon Ball Z: Kakarot", Path.Combine(sysDir, "cmd.exe"), @"C:\Games\DBZ Kakarot", 42000000, 140, "DBZ.exe"),
                    new GameCandidate("The Blood of Dawnwalker", Path.Combine(sysDir, "notepad.exe"), @"C:\Games\Dawnwalker", 78000000, 160, "Dawnwalker.exe")
                };
                var dlg = new FolderBatchImportDialog(@"C:\Games", sampleCandidates, _ => false, isAlreadyScanLocation: false, onIgnoreCandidate: _ => { });
                if (dlg.DataContext is FolderBatchImportViewModel vm && vm.Games.Count >= 3)
                {
                    vm.Games[0].IsSelected = false;
                }
                using var dlgBmp = CaptureVisualBitmap(dlg, 680, 540);

                using var composite = new System.Drawing.Bitmap(mainBmp.Width, mainBmp.Height);
                using (var g = System.Drawing.Graphics.FromImage(composite))
                {
                    g.DrawImage(mainBmp, 0, 0);
                    int dx = (mainBmp.Width - dlgBmp.Width) / 2;
                    int dy = (mainBmp.Height - dlgBmp.Height) / 2;
                    using var shadowBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(160, 0, 0, 0));
                    g.FillRectangle(shadowBrush, dx - 14, dy - 10, dlgBmp.Width + 28, dlgBmp.Height + 24);
                    g.DrawImage(dlgBmp, dx, dy);
                }

                string? pDir = Path.GetDirectoryName(targetPng);
                if (!string.IsNullOrEmpty(pDir) && !Directory.Exists(pDir)) Directory.CreateDirectory(pDir);
                composite.Save(targetPng, System.Drawing.Imaging.ImageFormat.Png);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-edit-poster", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-edit-poster", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                string sysDir = Environment.SystemDirectory;
                string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

                var sampleGame = _mainViewModel.Games.FirstOrDefault(g => g.Name.Contains("Dawnwalker", StringComparison.OrdinalIgnoreCase))?.Game
                    ?? new GameEntry
                    {
                        Name = "The Blood of Dawnwalker",
                        Category = "Action RPG",
                        SteamAppId = "3751260",
                        CoverImagePath = Path.Combine(SteamMetadataService.CoversDirectory, "3751260.jpg"),
                        ExecutablePath = @"C:\Games\Dawnwalker\Binaries\Win64\Dawnwalker.exe",
                        WorkingDirectory = @"C:\Games\Dawnwalker"
                    };

                if (string.IsNullOrEmpty(sampleGame.IconPath) || !File.Exists(sampleGame.IconPath))
                {
                    string fallbackExe = Path.Combine(winDir, "explorer.exe");
                    sampleGame.IconPath = _iconExtractorService.ExtractAndCacheIcon(sampleGame.Id, fallbackExe, sampleGame.Name);
                }

                var dlg = new GameEditDialog(sampleGame, _mainViewModel.Categories, _iconExtractorService);
                CaptureVisual(dlg, 560, 680, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-tray", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-tray", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];

                var testWin = new Window
                {
                    Title = "Tray Context Menu Preview",
                    Width = 280,
                    Height = 250,
                    Background = new SolidColorBrush(Color.FromRgb(18, 18, 20)),
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };

                var container = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(23, 23, 28)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(44, 44, 56)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(5),
                    Margin = new Thickness(20),
                    VerticalAlignment = VerticalAlignment.Center
                };

                var panel = new StackPanel();

                var catItem = new MenuItem 
                { 
                    Header = "New Games (1)",
                    FontWeight = FontWeights.Normal
                };
                var subGame = new MenuItem { Header = "The Blood of Dawnwalker" };
                catItem.Items.Add(subGame);

                var sep = new Separator();
                var exitItem = new MenuItem { Header = "Exit" };

                panel.Children.Add(catItem);
                panel.Children.Add(sep);
                panel.Children.Add(exitItem);

                container.Child = panel;
                testWin.Content = container;

                CaptureVisual(testWin, 280, 220, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-tray-recent", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-tray-recent", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];

                var testWin = new Window
                {
                    Title = "Tray Context Menu Preview - Persistent Recent Category",
                    Width = 460,
                    Height = 260,
                    Background = new SolidColorBrush(Color.FromRgb(18, 18, 20)),
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };

                var rootGrid = new Grid
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // 1. Root Tray Context Menu (Categorized)
                var container1 = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(23, 23, 28)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(44, 44, 56)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(5),
                    Margin = new Thickness(0, 0, 10, 0),
                    Width = 190
                };

                var panel1 = new StackPanel();

                var recentCatItem = new MenuItem 
                { 
                    Header = "Recent (2)",
                    IsSubmenuOpen = true
                };
                var sep1 = new Separator();
                var catAction = new MenuItem { Header = "Action (3)" };
                var catRpg = new MenuItem { Header = "RPG (2)" };
                var sep2 = new Separator();
                var exitItem = new MenuItem { Header = "Exit" };

                panel1.Children.Add(recentCatItem);
                panel1.Children.Add(sep1);
                panel1.Children.Add(catAction);
                panel1.Children.Add(catRpg);
                panel1.Children.Add(sep2);
                panel1.Children.Add(exitItem);

                container1.Child = panel1;
                Grid.SetColumn(container1, 0);
                rootGrid.Children.Add(container1);

                // 2. Open Submenu Preview for Recent
                var containerSub = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(26, 26, 34)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(50, 50, 64)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(5),
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 4, 0, 0),
                    Width = 180
                };

                var panelSub = new StackPanel();
                var sub1 = new MenuItem { Header = "Cyberpunk 2077" };
                var sub2 = new MenuItem { Header = "Hades II" };
                panelSub.Children.Add(sub1);
                panelSub.Children.Add(sub2);

                containerSub.Child = panelSub;
                Grid.SetColumn(containerSub, 1);
                rootGrid.Children.Add(containerSub);

                testWin.Content = rootGrid;

                CaptureVisual(testWin, 460, 260, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-tray-flat", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-tray-flat", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];

                var testWin = new Window
                {
                    Title = "Tray Context Menu Preview - Flat Mode with Persistent Recent",
                    Width = 460,
                    Height = 290,
                    Background = new SolidColorBrush(Color.FromRgb(18, 18, 20)),
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };

                var rootGrid = new Grid
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // 1. Root Tray Context Menu (Flat Mode)
                var container1 = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(23, 23, 28)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(44, 44, 56)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(5),
                    Margin = new Thickness(0, 0, 10, 0),
                    Width = 190
                };

                var panel1 = new StackPanel();

                var recentCatItem = new MenuItem { Header = "Recent (2)" };
                var sep1 = new Separator();
                var g1 = new MenuItem { Header = "Cyberpunk 2077" };
                var g2 = new MenuItem { Header = "DOOM Eternal" };
                var g3 = new MenuItem { Header = "Hades II" };
                var sep2 = new Separator();
                var exitItem = new MenuItem { Header = "Exit" };

                panel1.Children.Add(recentCatItem);
                panel1.Children.Add(sep1);
                panel1.Children.Add(g1);
                panel1.Children.Add(g2);
                panel1.Children.Add(g3);
                panel1.Children.Add(sep2);
                panel1.Children.Add(exitItem);

                container1.Child = panel1;
                Grid.SetColumn(container1, 0);
                rootGrid.Children.Add(container1);

                // 2. Open Submenu Preview for Recent
                var containerSub = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(26, 26, 34)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(50, 50, 64)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(5),
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 4, 0, 0),
                    Width = 180
                };

                var panelSub = new StackPanel();
                var sub1 = new MenuItem { Header = "Cyberpunk 2077" };
                var sub2 = new MenuItem { Header = "Hades II" };
                panelSub.Children.Add(sub1);
                panelSub.Children.Add(sub2);

                containerSub.Child = panelSub;
                Grid.SetColumn(containerSub, 1);
                rootGrid.Children.Add(containerSub);

                testWin.Content = rootGrid;

                CaptureVisual(testWin, 460, 290, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-settings-tray", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-settings-tray", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                _mainViewModel.CurrentSection = NavSection.Settings;
                _mainWindow.Show();
                _mainWindow.UpdateLayout();
                var sv = FindVisualChild<ScrollViewer>(_mainWindow, s => s.ScrollableHeight > 0);
                sv?.ScrollToVerticalOffset(180);
                _mainWindow.UpdateLayout();
                CaptureVisual(_mainWindow, 960, 750, targetPng);
                ExitApplication();
                return;
            }


            if ((e.Args[i].Equals("--screenshot-populated", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-populated", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];

                // Add sample games
                var validGame = new GameEntry
                {
                    Name = "Cyberpunk 2077",
                    Category = "RPG",
                    ExecutablePath = @"C:\Windows\explorer.exe", // valid file on system
                    CumulativePlaytimeMinutes = 145,
                    LastPlayed = DateTime.Now.AddHours(-3)
                };
                validGame.IconPath = _iconExtractorService.ExtractAndCacheIcon(validGame.Id, validGame.ExecutablePath, validGame.Name);
                var cpCover = Path.Combine(_storageService.BaseDirectory, "Covers", "1091500.jpg");
                if (File.Exists(cpCover))
                {
                    validGame.CoverImagePath = cpCover;
                }

                var missingGame = new GameEntry
                {
                    Name = "Elden Ring",
                    Category = "Action",
                    ExecutablePath = @"D:\Games\Elden Ring\eldenring.exe", // missing path
                    CumulativePlaytimeMinutes = 320,
                    LastPlayed = DateTime.Now.AddDays(-2)
                };
                missingGame.IconPath = _iconExtractorService.ExtractAndCacheIcon(missingGame.Id, missingGame.ExecutablePath, missingGame.Name);

                var dawnwalkerGame = new GameEntry
                {
                    Name = "The Blood of Dawnwalker",
                    Category = "Action RPG",
                    ExecutablePath = @"C:\Games\The Blood of Dawnwalker\Dawnwalker.exe",
                    IsSteamGame = false,
                    SteamAppId = "3751260",
                    CumulativePlaytimeMinutes = 95,
                    LastPlayed = DateTime.Now.AddHours(-1)
                };
                string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                dawnwalkerGame.IconPath = _iconExtractorService.ExtractAndCacheIcon(dawnwalkerGame.Id, Path.Combine(winDir, "explorer.exe"), dawnwalkerGame.Name);

                var existingCover = Path.Combine(SteamMetadataService.CoversDirectory, "3751260.jpg");
                if (File.Exists(existingCover))
                {
                    dawnwalkerGame.CoverImagePath = existingCover;
                }

                _mainViewModel.Games.Clear();
                _mainViewModel.Games.Add(_mainViewModel.CreateCardViewModel(validGame));
                _mainViewModel.Games.Add(_mainViewModel.CreateCardViewModel(missingGame));
                _mainViewModel.Games.Add(_mainViewModel.CreateCardViewModel(dawnwalkerGame));

                _mainViewModel.RebuildCategories();

                // Show undo toast
                _mainViewModel.UndoToastMessage = "Removed \"The Witcher 3\"";
                _mainViewModel.IsUndoToastVisible = true;

                _mainWindow.Show();
                _mainWindow.UpdateLayout();
                CaptureVisual(_mainWindow, 960, 700, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-library-all", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-library-all", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                _mainViewModel.CurrentSection = NavSection.Library;

                var g1 = new GameEntry
                {
                    Name = "Cyberpunk 2077",
                    Category = "RPG",
                    ExecutablePath = @"C:\Windows\explorer.exe",
                    CumulativePlaytimeMinutes = 145,
                    LastPlayed = DateTime.Now.AddHours(-3)
                };
                g1.IconPath = _iconExtractorService.ExtractAndCacheIcon(g1.Id, g1.ExecutablePath, g1.Name);
                var cpCover = Path.Combine(_storageService.BaseDirectory, "Covers", "1091500.jpg");
                if (File.Exists(cpCover)) g1.CoverImagePath = cpCover;

                var g2 = new GameEntry
                {
                    Name = "Elden Ring",
                    Category = "Action",
                    ExecutablePath = @"D:\Games\Elden Ring\eldenring.exe",
                    CumulativePlaytimeMinutes = 320,
                    LastPlayed = DateTime.Now.AddDays(-2)
                };
                g2.IconPath = _iconExtractorService.ExtractAndCacheIcon(g2.Id, g2.ExecutablePath, g2.Name);

                var g3 = new GameEntry
                {
                    Name = "The Blood of Dawnwalker",
                    Category = "Action RPG",
                    ExecutablePath = @"C:\Games\Dawnwalker\Dawnwalker.exe",
                    CumulativePlaytimeMinutes = 95,
                    LastPlayed = DateTime.Now.AddHours(-1)
                };
                g3.IconPath = _iconExtractorService.ExtractAndCacheIcon(g3.Id, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), g3.Name);
                var dwCover = Path.Combine(SteamMetadataService.CoversDirectory, "3751260.jpg");
                if (File.Exists(dwCover)) g3.CoverImagePath = dwCover;

                var g4 = new GameEntry
                {
                    Name = "Portal 2",
                    Category = "Steam",
                    IsSteamGame = true,
                    CumulativePlaytimeMinutes = 240,
                    LastPlayed = DateTime.Now.AddDays(-5)
                };
                g4.IconPath = _iconExtractorService.ExtractAndCacheIcon(g4.Id, "", g4.Name);

                _mainViewModel.Games.Clear();
                _mainViewModel.Games.Add(_mainViewModel.CreateCardViewModel(g1));
                _mainViewModel.Games.Add(_mainViewModel.CreateCardViewModel(g2));
                _mainViewModel.Games.Add(_mainViewModel.CreateCardViewModel(g3));
                _mainViewModel.Games.Add(_mainViewModel.CreateCardViewModel(g4));
                _mainViewModel.RebuildCategories();
                _mainViewModel.SelectedCategory = "All";

                _mainWindow.Show();
                _mainWindow.UpdateLayout();
                CaptureVisual(_mainWindow, 960, 750, targetPng);
                ExitApplication();
                return;
            }

            if ((e.Args[i].Equals("--screenshot-library-category", StringComparison.OrdinalIgnoreCase) ||
                 e.Args[i].Equals("-screenshot-library-category", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < e.Args.Length)
            {
                string targetPng = e.Args[i + 1];
                _mainViewModel.CurrentSection = NavSection.Library;

                var g1 = new GameEntry
                {
                    Name = "Cyberpunk 2077",
                    Category = "RPG",
                    ExecutablePath = @"C:\Windows\explorer.exe",
                    CumulativePlaytimeMinutes = 145,
                    LastPlayed = DateTime.Now.AddHours(-3)
                };
                g1.IconPath = _iconExtractorService.ExtractAndCacheIcon(g1.Id, g1.ExecutablePath, g1.Name);
                var cpCover = Path.Combine(_storageService.BaseDirectory, "Covers", "1091500.jpg");
                if (File.Exists(cpCover)) g1.CoverImagePath = cpCover;

                var g2 = new GameEntry
                {
                    Name = "Elden Ring",
                    Category = "Action",
                    ExecutablePath = @"D:\Games\Elden Ring\eldenring.exe",
                    CumulativePlaytimeMinutes = 320,
                    LastPlayed = DateTime.Now.AddDays(-2)
                };
                g2.IconPath = _iconExtractorService.ExtractAndCacheIcon(g2.Id, g2.ExecutablePath, g2.Name);

                var g3 = new GameEntry
                {
                    Name = "The Blood of Dawnwalker",
                    Category = "Action RPG",
                    ExecutablePath = @"C:\Games\Dawnwalker\Dawnwalker.exe",
                    CumulativePlaytimeMinutes = 95,
                    LastPlayed = DateTime.Now.AddHours(-1)
                };
                g3.IconPath = _iconExtractorService.ExtractAndCacheIcon(g3.Id, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), g3.Name);
                var dwCover = Path.Combine(SteamMetadataService.CoversDirectory, "3751260.jpg");
                if (File.Exists(dwCover)) g3.CoverImagePath = dwCover;

                var g4 = new GameEntry
                {
                    Name = "Portal 2",
                    Category = "Steam",
                    IsSteamGame = true,
                    CumulativePlaytimeMinutes = 240,
                    LastPlayed = DateTime.Now.AddDays(-5)
                };
                g4.IconPath = _iconExtractorService.ExtractAndCacheIcon(g4.Id, "", g4.Name);

                _mainViewModel.Games.Clear();
                _mainViewModel.Games.Add(_mainViewModel.CreateCardViewModel(g1));
                _mainViewModel.Games.Add(_mainViewModel.CreateCardViewModel(g2));
                _mainViewModel.Games.Add(_mainViewModel.CreateCardViewModel(g3));
                _mainViewModel.Games.Add(_mainViewModel.CreateCardViewModel(g4));
                _mainViewModel.RebuildCategories();
                _mainViewModel.SelectCategoryTab("RPG");

                _mainWindow.Show();
                _mainWindow.UpdateLayout();
                CaptureVisual(_mainWindow, 960, 750, targetPng);
                ExitApplication();
                return;
            }
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent, Func<T, bool> predicate) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed && predicate(typed))
                return typed;

            var descendant = FindVisualChild(child, predicate);
            if (descendant != null)
                return descendant;
        }
        return null;
    }

    /// <summary>
    /// Opens one of MainWindow's context menus against a data context and returns every header it
    /// rendered, submenu items included. Submenu items are only built once their parent has been
    /// expanded, so each parent is opened explicitly rather than just walked.
    ///
    /// Collapsed items (the Steam block on a non-Steam game, "Close Launcher" on a local exe) are
    /// left out, so the result is what a user would actually see.
    /// </summary>
    private List<string> OpenMenuAndReadHeaders(string resourceKey, object dataContext)
    {
        var menu = (ContextMenu)_mainWindow!.FindResource(resourceKey);
        menu.PlacementTarget = _mainWindow;
        menu.DataContext = dataContext;
        menu.IsOpen = true;

        var headers = new List<string>();
        CollectMenuHeaders(menu.Items, headers, depth: 0);

        menu.IsOpen = false;
        // The single-game menu binds its DataContext to PlacementTarget.DataContext; clearing the
        // local value here puts that binding back for the real cards.
        menu.ClearValue(FrameworkElement.DataContextProperty);
        return headers;
    }

    private static void CollectMenuHeaders(System.Collections.IEnumerable items, List<string> headers, int depth)
    {
        foreach (object item in items)
        {
            if (item is not MenuItem mi) continue;
            if (mi.Visibility != Visibility.Visible) continue;

            headers.Add(mi.Header?.ToString() ?? "");
            if (mi.Items.Count == 0 || depth >= 2) continue;

            // A submenu's items are realised on expansion; without this they read as empty.
            mi.IsSubmenuOpen = true;
            mi.UpdateLayout();
            CollectMenuHeaders(mi.Items, headers, depth + 1);
            mi.IsSubmenuOpen = false;
        }
    }

    /// <summary>Every descendant of <paramref name="parent"/> of type T, depth-first.</summary>
    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    private System.Drawing.Bitmap CaptureVisualBitmap(Window window, int width, int height)
    {
        window.Width = width;
        window.Height = height;
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Show();
        window.Activate();
        window.Focus();
        window.UpdateLayout();

        // Pump dispatcher events to allow DWM to paint titlebar
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        timer.Tick += (s, args) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);

        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        int clientW = width;
        int clientH = height;
        if (hwnd != IntPtr.Zero && GetClientRect(hwnd, out RECT cRect) && cRect.Right > 50 && cRect.Bottom > 50)
        {
            clientW = cRect.Right - cRect.Left;
            clientH = cRect.Bottom - cRect.Top;
        }

        double effDpiX = window.ActualWidth > 0 ? (96.0 * clientW / window.ActualWidth) : 96.0;
        double effDpiY = window.ActualHeight > 0 ? (96.0 * clientH / window.ActualHeight) : 96.0;

        // 1. Render client area with RenderTargetBitmap
        var rtb = new RenderTargetBitmap(clientW, clientH, effDpiX, effDpiY, PixelFormats.Pbgra32);
        rtb.Render(window);

        // Convert rtb to GDI Bitmap
        var clientBmp = new System.Drawing.Bitmap(clientW, clientH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var rectClient = new System.Drawing.Rectangle(0, 0, clientW, clientH);
        var bmpData = clientBmp.LockBits(rectClient, System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        rtb.CopyPixels(System.Windows.Int32Rect.Empty, bmpData.Scan0, bmpData.Height * bmpData.Stride, bmpData.Stride);
        clientBmp.UnlockBits(bmpData);

        try
        {
            if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out RECT rect) && (rect.Right - rect.Left > 50) && (rect.Bottom - rect.Top > 50))
            {
                int w = rect.Right - rect.Left;
                int h = rect.Bottom - rect.Top;
                using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.Clear(System.Drawing.Color.FromArgb(18, 18, 20));
                    IntPtr hdc = g.GetHdc();
                    bool printed = false;
                    try
                    {
                        printed = PrintWindow(hwnd, hdc, 2);
                    }
                    finally
                    {
                        try { g.ReleaseHdc(hdc); } catch { }
                    }

                    if (printed)
                    {
                        var pt = new POINT { X = 0, Y = 0 };
                        ClientToScreen(hwnd, ref pt);
                        int clientX = Math.Max(0, pt.X - rect.Left);
                        int clientY = Math.Max(0, pt.Y - rect.Top);

                        int finalW = clientX + clientW + clientX;
                        int finalH = clientY + clientH + clientX;

                        var finalBmp = new System.Drawing.Bitmap(finalW, finalH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        using (var gFinal = System.Drawing.Graphics.FromImage(finalBmp))
                        {
                            gFinal.Clear(System.Drawing.Color.FromArgb(18, 18, 20));
                            int captionH = Math.Min(clientY, h);
                            gFinal.DrawImage(bmp,
                                new System.Drawing.Rectangle(0, 0, finalW, captionH),
                                new System.Drawing.Rectangle(0, 0, Math.Min(finalW, w), captionH),
                                System.Drawing.GraphicsUnit.Pixel);
                            gFinal.DrawImage(clientBmp, new System.Drawing.Rectangle(clientX, clientY, clientW, clientH));
                        }

                        clientBmp.Dispose();
                        return finalBmp;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger($"[CaptureVisualBitmap - Native] Falling back to client screenshot: {ex.Message}");
        }

        return clientBmp;
    }

    /// <summary>Copies the window's on-screen pixels (including any open popup over it) to a PNG.</summary>
    private void CaptureScreen(Window window, string targetPng)
    {
        try
        {
            var transform = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
            var origin = window.PointToScreen(new Point(0, 0));
            int width = (int)Math.Round(window.ActualWidth * transform.M11);
            int height = (int)Math.Round(window.ActualHeight * transform.M22);
            using var bmp = new System.Drawing.Bitmap(width, height);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.CopyFromScreen((int)origin.X, (int)origin.Y, 0, 0, new System.Drawing.Size(width, height));
            }
            string? parentDir = Path.GetDirectoryName(targetPng);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir)) Directory.CreateDirectory(parentDir);
            bmp.Save(targetPng, System.Drawing.Imaging.ImageFormat.Png);
            _logger($"[CaptureScreen] Success: {targetPng}");
        }
        catch (Exception ex)
        {
            _logger($"[CaptureScreen] Error: {ex}");
        }
    }

    private void CaptureVisual(Window window, int width, int height, string targetPng)
    {
        try
        {
            // TRAYTRIGGER_SHOT_SIZE=WxH (device-independent units) overrides a mode's default
            // size - the README screenshots are taken at 1040x747, the review ones at 960x700.
            string? sizeOverride = Environment.GetEnvironmentVariable("TRAYTRIGGER_SHOT_SIZE");
            if (!string.IsNullOrWhiteSpace(sizeOverride))
            {
                var parts = sizeOverride.Split('x');
                if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h) && w > 0 && h > 0)
                {
                    width = w;
                    height = h;
                }
            }
            using var bmp = CaptureVisualBitmap(window, width, height);
            string? parentDir = Path.GetDirectoryName(targetPng);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            bmp.Save(targetPng, System.Drawing.Imaging.ImageFormat.Png);
            _logger($"[CaptureVisual] Success: {targetPng}");
        }
        catch (Exception ex)
        {
            _logger($"[CaptureVisual] Error: {ex}");
        }
    }
#endif
}
#endif
