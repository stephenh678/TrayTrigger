using System.IO;
using TrayTrigger.Services;
using TrayTrigger.Tests.Fakes;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>
/// The elevated step runs with administrator rights, so what it will do is written down and checked at both ends:
/// only the settings and values in <see cref="NvidiaAdminOp.Allowed"/>, and only a .reg file TrayTrigger made.
/// </summary>
public class ElevatedHelperTests
{
    private const uint Rebar = PerformanceProfileService.NvResizableBarSettingId;

    [Fact]
    public void OnlyResizableBar_IsAllowed_WithOffOrOn()
    {
        Assert.Equal([Rebar], NvidiaAdminOp.Allowed.Keys.ToArray());
        Assert.True(new NvidiaAdminOp(Rebar, NvidiaAdminAction.Set, 0).IsAllowed);
        Assert.True(new NvidiaAdminOp(Rebar, NvidiaAdminAction.Set, 1).IsAllowed);
        Assert.False(new NvidiaAdminOp(Rebar, NvidiaAdminAction.Set, 2).IsAllowed);
        Assert.True(new NvidiaAdminOp(Rebar, NvidiaAdminAction.Delete).IsAllowed);
        Assert.True(new NvidiaAdminOp(Rebar, NvidiaAdminAction.RestoreDefault).IsAllowed);
        // Another setting, even a harmless-looking one, is not this step's to change.
        Assert.False(new NvidiaAdminOp(PerformanceProfileService.NvPowerModeSettingId, NvidiaAdminAction.Set, 1).IsAllowed);
    }

    [Theory]
    [InlineData("set:000F00BA:1")]
    [InlineData("set:000F00BA:0")]
    [InlineData("delete:000F00BA")]
    [InlineData("default:000F00BA")]
    public void AnArgument_RoundTrips(string argument)
    {
        Assert.True(NvidiaAdminOp.TryParse(argument, out var op));
        Assert.Equal(argument, op.ToArgument());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("set")]
    [InlineData("set:000F00BA")]
    [InlineData("set:000F00BA:2")]
    [InlineData("set:000F00BA:-1")]
    [InlineData("set:000F00BA:1:extra")]
    [InlineData("set:0F00BA:1")]
    [InlineData("set:xxxxxxxx:1")]
    [InlineData("set:00000001:1")]
    [InlineData("delete:00000001")]
    [InlineData("delete:000F00BA:1")]
    [InlineData("run:000F00BA")]
    [InlineData("set:000F00BA:1; calc")]
    public void AnythingElse_IsRefused(string? argument)
    {
        Assert.False(NvidiaAdminOp.TryParse(argument, out _));
    }

    [Fact]
    public void TheNvidiaChanges_AreMadeOnTheGlobalProfile_AndReadBack()
    {
        var driver = new FakeDrsBackend();

        Assert.True(ElevatedHelper.ApplyNvidiaOps([new NvidiaAdminOp(Rebar, NvidiaAdminAction.Set, 1)], driver, out var error), error);
        Assert.Equal((1u, false), driver.Global.Settings[Rebar]);

        Assert.True(ElevatedHelper.ApplyNvidiaOps([new NvidiaAdminOp(Rebar, NvidiaAdminAction.Delete)], driver, out error), error);
        Assert.False(driver.Global.Settings.ContainsKey(Rebar));
    }

    [Fact]
    public void ARefusedChange_IsAFailure_WithTheDriversReason()
    {
        var driver = new FakeDrsBackend();
        driver.RefusedWrites[Rebar] = "NVAPI_INVALID_USER_PRIVILEGE (-137)";

        Assert.False(ElevatedHelper.ApplyNvidiaOps([new NvidiaAdminOp(Rebar, NvidiaAdminAction.Set, 1)], driver, out var error));
        Assert.Contains("-137", error);
        Assert.False(driver.Global.Settings.ContainsKey(Rebar));
    }

    [Fact]
    public void AChangeThatIsNotAllowed_IsNotMade()
    {
        var driver = new FakeDrsBackend();

        Assert.False(ElevatedHelper.ApplyNvidiaOps([new NvidiaAdminOp(PerformanceProfileService.NvPowerModeSettingId, NvidiaAdminAction.Set, 1)], driver, out _));
        Assert.Empty(driver.Global.Settings);
    }

    private static readonly SystemTweaksService.RegFileEntry Responsiveness =
        new(PerformanceProfileService.SystemResponsivenessPath, "SystemResponsiveness", 10, Microsoft.Win32.RegistryValueKind.DWord, Delete: false);
    private static readonly SystemTweaksService.RegFileEntry Category =
        new(SystemTweaksService.MmcssGamesTaskPath, "Scheduling Category", "High", Microsoft.Win32.RegistryValueKind.String, Delete: false);

    /// <summary>What a profile really sends is what the helper accepts, and it reads back exactly the same.</summary>
    [Fact]
    public void TheRegistryChangesAProfileMakes_AreAccepted_AndReadBackAsTheyWere()
    {
        string content = SystemTweaksService.BuildRegFileContent([Responsiveness, Category]);

        Assert.True(ElevatedHelper.TryReadRegistryChanges(content, out var entries, out var error), error);
        Assert.Equal([Responsiveness, Category], entries);
    }

    [Fact]
    public void ThePutBackValues_AreAccepted()
    {
        var back = new[]
        {
            new SystemTweaksService.RegFileEntry(PerformanceProfileService.SystemResponsivenessPath, "SystemResponsiveness", 20, Microsoft.Win32.RegistryValueKind.DWord, Delete: false),
            new SystemTweaksService.RegFileEntry(SystemTweaksService.MmcssGamesTaskPath, "Scheduling Category", "Medium", Microsoft.Win32.RegistryValueKind.String, Delete: false),
        };

        Assert.True(ElevatedHelper.TryReadRegistryChanges(SystemTweaksService.BuildRegFileContent(back), out var entries, out var error), error);
        Assert.Equal(back, entries);
    }

    /// <summary>The file's name and place don't make it TrayTrigger's: every other key, value and form is refused, and nothing is applied.</summary>
    [Theory]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Evil", "\"C:\\evil.exe\"")]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\Evil", "ImagePath", "\"C:\\evil.exe\"")]
    [InlineData(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", "dword:00000065")]
    [InlineData(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", "\"10\"")]
    [InlineData(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", "dword:ffffffff")]
    [InlineData(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Scheduling Category", "\"Evil\"")]
    [InlineData(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Scheduling Category", "dword:00000001")]
    [InlineData(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "GPU Priority", "dword:00000008")]
    public void ARegistryChangeThatIsNotOneOfTheTwo_IsRefused(string key, string name, string value)
    {
        string content = "Windows Registry Editor Version 5.00\r\n\r\n[HKEY_LOCAL_MACHINE\\" + key + "]\r\n\"" + name + "\"=" + value + "\r\n";

        Assert.False(ElevatedHelper.TryReadRegistryChanges(content, out var entries, out var error));
        Assert.NotNull(error);
        Assert.Empty(entries);
    }

    [Theory]
    [InlineData("Windows Registry Editor Version 5.00\r\n[HKEY_CURRENT_USER\\Software\\X]\r\n")]
    [InlineData("Windows Registry Editor Version 4.00\r\n")]
    [InlineData("")]
    [InlineData("[HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile]\r\n")]
    [InlineData("Windows Registry Editor Version 5.00\r\n\"SystemResponsiveness\"=dword:0000000a\r\n")]
    [InlineData("Windows Registry Editor Version 5.00\r\n\r\n[HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile]\r\n\"SystemResponsiveness\"=-\r\n")]
    [InlineData("Windows Registry Editor Version 5.00\r\n\r\n[-HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile]\r\n")]
    [InlineData("Windows Registry Editor Version 5.00\r\n\r\n[HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile]\r\n@=\"x\"\r\n")]
    public void AFileThatIsNotWhatAProfileWrites_IsRefused(string content)
    {
        Assert.False(ElevatedHelper.TryReadRegistryChanges(content, out var entries, out _));
        Assert.Empty(entries);
    }

    [Fact]
    public void AGoodLineBesideABadOne_RefusesTheWholeFile_AndAValueTwiceIsRefused()
    {
        string good = SystemTweaksService.BuildRegFileContent([Responsiveness]);
        string bad = good + "\"NetworkThrottlingIndex\"=dword:ffffffff\r\n";
        string twice = good + "\"SystemResponsiveness\"=dword:00000014\r\n";

        Assert.False(ElevatedHelper.TryReadRegistryChanges(bad, out var entries, out _));
        Assert.Empty(entries);
        Assert.False(ElevatedHelper.TryReadRegistryChanges(twice, out entries, out _));
        Assert.Empty(entries);
    }

    [Fact]
    public void TheFile_IsReadOnce_ByItsNameAndContent_NotByWhereItIs()
    {
        string content = SystemTweaksService.BuildRegFileContent([Responsiveness, Category]);
        string good = Path.Combine(Path.GetTempPath(), $"TrayTrigger-{Guid.NewGuid():N}.reg");
        string wrongName = Path.Combine(Path.GetTempPath(), "evil.reg");
        string elsewhere = Path.Combine(Directory.GetCurrentDirectory(), $"TrayTrigger-{Guid.NewGuid():N}.reg");
        string evil = Path.Combine(Path.GetTempPath(), $"TrayTrigger-{Guid.NewGuid():N}.reg");
        try
        {
            File.WriteAllText(good, content, System.Text.Encoding.Unicode);
            File.WriteAllText(wrongName, content, System.Text.Encoding.Unicode);
            File.WriteAllText(elsewhere, content, System.Text.Encoding.Unicode);
            File.WriteAllText(evil, content.Replace("SystemResponsiveness", "NetworkThrottlingIndex"), System.Text.Encoding.Unicode);

            Assert.True(ElevatedHelper.TryReadRegFile(good, out var entries, out var error), error);
            Assert.Equal(2, entries.Count);
            // Another folder is fine - another account's temp folder, when an administrator approves for a standard user - because
            // what counts is what is in it.
            Assert.True(ElevatedHelper.TryReadRegFile(elsewhere, out entries, out error), error);
            // A name that is not TrayTrigger's, however good the content.
            Assert.False(ElevatedHelper.TryReadRegFile(wrongName, out _, out _));
            // A TrayTrigger name on content that is not.
            Assert.False(ElevatedHelper.TryReadRegFile(evil, out entries, out _));
            Assert.Empty(entries);
            Assert.False(ElevatedHelper.TryReadRegFile(Path.Combine(Path.GetTempPath(), $"TrayTrigger-{Guid.NewGuid():N}.reg"), out _, out _));
            Assert.False(ElevatedHelper.TryReadRegFile(@"C:\Windows\System32\config\SAM", out _, out _));
            Assert.False(ElevatedHelper.TryReadRegFile("not a path \0", out _, out _));
        }
        finally
        {
            foreach (var f in new[] { good, wrongName, elsewhere, evil }) { try { File.Delete(f); } catch { } }
        }
    }

    [Fact]
    public void AFileFarLargerThanTheTwoValues_IsRefused()
    {
        string big = Path.Combine(Path.GetTempPath(), $"TrayTrigger-{Guid.NewGuid():N}.reg");
        try
        {
            File.WriteAllText(big, SystemTweaksService.BuildRegFileContent([Responsiveness]) + new string(' ', 40_000), System.Text.Encoding.Unicode);
            Assert.False(ElevatedHelper.TryReadRegFile(big, out _, out var error));
            Assert.Equal("too large", error);
        }
        finally { try { File.Delete(big); } catch { } }
    }

    // ---- What an elevated run's end means ------------------------------------------------------

    [Fact]
    public void ADeclinedPrompt_ChangedNothing_AndSaysSo()
    {
        var withRegistry = ElevatedHelper.Interpret(2, new ElevatedExit(false, null));
        Assert.False(withRegistry.RegistryOk);
        Assert.False(withRegistry.NvidiaOk);
        Assert.False(withRegistry.Unfinished);
        Assert.Contains("declined", withRegistry.Problem);

        // Nothing was asked of the registry, so nothing failed there.
        Assert.True(ElevatedHelper.Interpret(0, new ElevatedExit(false, null)).RegistryOk);
    }

    [Fact]
    public void AStepStillRunning_IsNotKnownToHaveFailed()
    {
        var result = ElevatedHelper.Interpret(2, new ElevatedExit(true, null));

        Assert.True(result.Unfinished);
        Assert.False(result.NvidiaOk);
        Assert.Contains("not known", result.Problem);
    }

    [Theory]
    [InlineData(0, true, true, false)]
    [InlineData(ElevatedHelper.RegistryFailedBit, false, true, false)]
    [InlineData(ElevatedHelper.NvidiaFailedBit, true, false, false)]
    [InlineData(ElevatedHelper.RegistryFailedBit | ElevatedHelper.NvidiaFailedBit, false, false, false)]
    [InlineData(ElevatedHelper.BadRequestBit, false, false, true)]
    public void TheExitCode_SaysWhichPartFailed_AndWhoRefused(int code, bool registryOk, bool nvidiaOk, bool refusedTheRequest)
    {
        var result = ElevatedHelper.Interpret(2, new ElevatedExit(true, code));

        Assert.Equal(registryOk, result.RegistryOk);
        Assert.Equal(nvidiaOk, result.NvidiaOk);
        Assert.False(result.Unfinished);
        if (nvidiaOk) Assert.Null(result.Problem);
        else if (refusedTheRequest) Assert.Contains("refused the request", result.Problem);
        else Assert.Contains("driver refused", result.Problem);
    }

    [Fact]
    public void TheHelper_RefusesWhatItWasNotAskedPlainly_BeforeDoingAnything()
    {
        // A change that is not allowed, whatever else is on the command line.
        Assert.Equal(ElevatedHelper.BadRequestBit, ElevatedHelper.RunAsHelper([ElevatedHelper.Switch, "--nvidia", "set:000F00BA:7"]));
        // Without administrator rights it does nothing: this process is normally not elevated.
        if (!SystemTweaksService.IsElevated)
            Assert.Equal(ElevatedHelper.BadRequestBit, ElevatedHelper.RunAsHelper([ElevatedHelper.Switch, "--nvidia", "set:000F00BA:1"]));
    }

    [Fact]
    public void ARun_WithNothingToDo_AsksNothing()
    {
        Assert.Equal(ElevatedResult.Nothing, ElevatedHelper.Run([], []));
    }
}
