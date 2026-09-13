using System;
using System.Collections.Generic;
using System.IO;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

public class DiagnosticReportServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TrayTriggerDiag_" + Guid.NewGuid().ToString("N"));

    public DiagnosticReportServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private DiagnosticReportService.Inputs SampleInputs(string? logPath = null) => new()
    {
        Settings = new AppSettings
        {
            EnableGameScripts = true,
            ScriptDefaults = new ScriptDefaults { Enabled = true, PreLaunchScriptPath = @"C:\x\pre.ps1" },
            UseSteamGridDbArt = true,
            SteamGridDbApiKey = "176bc030f3cff8a0252f3be80d48bc78",
            RawgApiKey = "67682b6127d84c62a7dc1a987f6700ef",
            GlobalManageHotkey = "Ctrl+Alt+G",
        },
        Games =
        [
            new GameEntry { Name = "Overwatch", IsBattleNetGame = true, BattleNetUid = "prometheus" },
            new GameEntry { Name = "Portal 2", IsSteamGame = true, SteamAppId = "620", Hotkey = "Ctrl+Alt+P" },
            new GameEntry { Name = "Fatekeeper", ExecutablePath = @"C:\definitely\missing\Fatekeeper.exe", IsHidden = true, PreLaunchScriptPath = @"C:\x\pre.ps1" },
        ],
        Launchers = [new("Steam", true, true), new("Battle.net", false, null)],
        Sessions = [new("Overwatch", "Battle.net", true)],
        AppliedTweaks = ["Windows Game Mode", "Disable Mouse Acceleration (Enhanced Pointer Precision)"],
        TrayPromotionStatus = "Applied.",
        DataDirectory = _dir,
        CacheDirectory = _dir,
        LogPath = logPath ?? Path.Combine(_dir, "debug.log"),
        AppVersion = "v1.4.2-beta.4",
    };

    [Fact]
    public void Build_CoversEverySection_AndNeverLeaksApiKeys()
    {
        string report = DiagnosticReportService.Build(SampleInputs(), () => "- Windows: Windows 11 24H2 (build 26100.1)");

        Assert.Contains("- Version: v1.4.2-beta.4", report);
        Assert.Contains("- Windows: Windows 11 24H2", report);
        Assert.Contains("| Steam | on | yes |", report);
        Assert.Contains("| Battle.net | off | n/a |", report);
        Assert.Contains("- Games: 3", report);
        Assert.Contains("Battle.net 1", report);
        Assert.Contains("Steam 1", report);
        Assert.Contains("Local 1", report);
        Assert.Contains("Hidden 1, with hotkey 1, with scripts 1, missing exe 1", report);
        Assert.Contains("Scripts: on; default scripts: on (pre-launch set, post-exit none)", report);
        Assert.Contains("SteamGridDB: on, key present; RAWG: off, key present", report);
        Assert.Contains("last result: Applied.", report);
        Assert.Contains("- Overwatch: running via Battle.net", report);
        Assert.Contains("Applied system tweaks (2): Windows Game Mode", report);
        Assert.Contains("None in the current log.", report);

        // The keys themselves must never appear, only their presence.
        Assert.DoesNotContain("176bc030", report);
        Assert.DoesNotContain("67682b61", report);
    }

    [Fact]
    public void Build_SurvivesAFailingMachineProbe()
    {
        string report = DiagnosticReportService.Build(SampleInputs(), () => throw new InvalidOperationException("WMI down"));

        Assert.Contains("- System details: unavailable", report);
        Assert.Contains("- Games: 3", report);
    }

    [Fact]
    public void RecentProblems_KeepsOnlyWarnAndError_LastNOldestFirst()
    {
        string log = Path.Combine(_dir, "debug.log");
        var lines = new List<string>();
        for (int i = 1; i <= 20; i++)
        {
            lines.Add($"[2026-09-13 10:00:{i:00}.000] [INFO ] [App] noise {i}");
            lines.Add($"[2026-09-13 10:00:{i:00}.500] [{(i % 2 == 0 ? "WARN " : "ERROR")}] [Launcher] problem {i}");
        }
        File.WriteAllLines(log, lines);

        var recent = DiagnosticReportService.RecentProblems(log, 5);

        Assert.Equal(5, recent.Count);
        Assert.Contains("problem 16", recent[0]);
        Assert.Contains("problem 20", recent[4]);
        Assert.DoesNotContain(recent, l => l.Contains("noise"));

        string report = DiagnosticReportService.Build(SampleInputs(log), () => "- Windows: x");
        Assert.Contains("problem 20", report);
        Assert.DoesNotContain("problem 1]", report);
    }

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\Programs\TrayTrigger\TrayTrigger.exe", "installed (per-user)")]
    [InlineData(@"D:\Tools\TrayTrigger\TrayTrigger.exe", "portable")]
    public void InstallKind_FollowsTheInstallerConvention(string exe, string expected)
    {
        // The per-user installer's DefaultDirName is {localappdata}\Programs\TrayTrigger.
        string localPrograms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        string path = expected.StartsWith("installed") ? Path.Combine(localPrograms, "TrayTrigger", "TrayTrigger.exe") : exe;
        Assert.Equal(expected, DiagnosticReportService.InstallKind(path));
    }
}
