using Microsoft.Win32;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>
/// The crash-recovery snapshot is user-writable JSON whose values used to be interpolated into an
/// elevated "cmd.exe /c reg add ..." line. These tests pin both halves of the fix: the values are
/// validated on load, and the elevated write path can no longer be escaped from by data.
/// </summary>
public class ProfileSnapshotSecurityTests
{
    [Fact]
    public void Sanitize_DropsInjectedSchedulingCategory()
    {
        var snapshot = new PerformanceProfileSessionSnapshot
        {
            SchedulingCategoryCaptured = true,
            PreviousSchedulingCategory = "x\" & powershell -c calc & \""
        };

        var problems = ProfileSnapshotValidator.Sanitize(snapshot);

        Assert.Single(problems);
        Assert.False(snapshot.SchedulingCategoryCaptured);
        Assert.Null(snapshot.PreviousSchedulingCategory);
    }

    [Theory]
    [InlineData("High")]
    [InlineData("medium")]
    [InlineData("Low")]
    public void Sanitize_KeepsDocumentedSchedulingCategories(string value)
    {
        var snapshot = new PerformanceProfileSessionSnapshot { SchedulingCategoryCaptured = true, PreviousSchedulingCategory = value };
        Assert.Empty(ProfileSnapshotValidator.Sanitize(snapshot));
        Assert.True(snapshot.SchedulingCategoryCaptured);
    }

    [Fact]
    public void Sanitize_RequiresGuidPowerScheme()
    {
        var bad = new PerformanceProfileSessionSnapshot { PowerPlanCaptured = true, PreviousPowerSchemeGuid = "381b4222-f694-41f0-9685-ff5bb260df2e /delete 0" };
        Assert.Single(ProfileSnapshotValidator.Sanitize(bad));
        Assert.False(bad.PowerPlanCaptured);

        var good = new PerformanceProfileSessionSnapshot { PowerPlanCaptured = true, PreviousPowerSchemeGuid = "381b4222-f694-41f0-9685-ff5bb260df2e" };
        Assert.Empty(ProfileSnapshotValidator.Sanitize(good));
        Assert.True(good.PowerPlanCaptured);
    }

    [Fact]
    public void Sanitize_ClampsSystemResponsiveness()
    {
        var snapshot = new PerformanceProfileSessionSnapshot { SystemResponsivenessCaptured = true, PreviousSystemResponsiveness = 5000 };
        Assert.Single(ProfileSnapshotValidator.Sanitize(snapshot));
        Assert.False(snapshot.SystemResponsivenessCaptured);
    }

    [Fact]
    public void Sanitize_RejectsNonLocalPerGamePaths()
    {
        var snapshot = new PerformanceProfileSessionSnapshot
        {
            PerGameSnapshots =
            {
                new PerGameProfileSnapshot { GameId = "a", DefenderExclusionCaptured = true, DefenderExclusionPath = @"\\evil\share\x.exe" },
                new PerGameProfileSnapshot { GameId = "b", GpuPreferenceCaptured = true, GpuPreferenceExecutablePath = "relative\\x.exe" },
                new PerGameProfileSnapshot { GameId = "c", GpuPreferenceCaptured = true, GpuPreferenceExecutablePath = @"C:\Games\x.exe", PreviousGpuPreferenceValue = "GpuPreference=1;" },
                new PerGameProfileSnapshot { GameId = "d", GpuPreferenceCaptured = true, GpuPreferenceExecutablePath = @"C:\Games\y.exe", PreviousGpuPreferenceValue = "GpuPreference=1;\r\nevil" },
            }
        };

        var problems = ProfileSnapshotValidator.Sanitize(snapshot);

        Assert.Equal(3, problems.Count);
        Assert.False(snapshot.PerGameSnapshots[0].DefenderExclusionCaptured);
        Assert.False(snapshot.PerGameSnapshots[1].GpuPreferenceCaptured);
        Assert.True(snapshot.PerGameSnapshots[2].GpuPreferenceCaptured);
        Assert.False(snapshot.PerGameSnapshots[3].GpuPreferenceCaptured);
    }

    [Fact]
    public void Sanitize_ToleratesNullCollections()
    {
        var snapshot = new PerformanceProfileSessionSnapshot { PerGameSnapshots = null!, PreviousHdrStates = null! };
        Assert.Empty(ProfileSnapshotValidator.Sanitize(snapshot));
        Assert.NotNull(snapshot.PerGameSnapshots);
        Assert.NotNull(snapshot.PreviousHdrStates);
    }

    [Fact]
    public void RegFile_EscapesQuotesAndBackslashes_SoDataCannotBreakOut()
    {
        string injected = "x\" & calc & \"";
        string content = SystemTweaksService.BuildRegFileContent(
        [
            new SystemTweaksService.RegFileEntry(SystemTweaksService.MmcssGamesTaskPath, "Scheduling Category", injected, RegistryValueKind.String, Delete: false)
        ]);

        Assert.StartsWith("Windows Registry Editor Version 5.00", content);
        Assert.Contains(@"[HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games]", content);
        // The value is one quoted token with every inner quote escaped; there is no unescaped
        // quote, no shell, and no second line.
        Assert.Contains("\"Scheduling Category\"=\"x\\\" & calc & \\\"\"", content);
        Assert.DoesNotContain("cmd", content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, content.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void RegFile_FormatsDwordsAndDeletes()
    {
        string content = SystemTweaksService.BuildRegFileContent(
        [
            new SystemTweaksService.RegFileEntry(@"SOFTWARE\Test", "Val", 10, RegistryValueKind.DWord, Delete: false),
            new SystemTweaksService.RegFileEntry(@"SOFTWARE\Test", "Gone", null, RegistryValueKind.None, Delete: true),
            new SystemTweaksService.RegFileEntry(@"SOFTWARE\Test", "Neg", -1, RegistryValueKind.DWord, Delete: false),
            new SystemTweaksService.RegFileEntry(@"SOFTWARE\Other\", "Path", @"C:\a\b", RegistryValueKind.String, Delete: false),
        ]);

        Assert.Contains("\"Val\"=dword:0000000a", content);
        Assert.Contains("\"Gone\"=-", content);
        Assert.Contains("\"Neg\"=dword:ffffffff", content);
        Assert.Contains("[HKEY_LOCAL_MACHINE\\SOFTWARE\\Other]", content);
        Assert.Contains("\"Path\"=\"C:\\\\a\\\\b\"", content);
    }

    [Fact]
    public void RegFile_RejectsLineBreaksInData()
    {
        Assert.Throws<ArgumentException>(() => SystemTweaksService.BuildRegFileContent(
        [
            new SystemTweaksService.RegFileEntry(@"SOFTWARE\Test", "Val", "a\r\n[HKEY_LOCAL_MACHINE\\SYSTEM]", RegistryValueKind.String, Delete: false)
        ]));
    }
}
