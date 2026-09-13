using System;
using System.IO;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

public sealed class BattleNetScannerServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerBnetScanner", Guid.NewGuid().ToString("N"));
    private string CacheDir => Path.Combine(_root, "Cache");
    private string StorePath => Path.Combine(_root, "battlenet-codes.json");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private void WriteCacheFile(string relative, string content)
    {
        string path = Path.Combine(CacheDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private BattleNetScannerService NewScanner() => new(CacheDir, new BattleNetCodeStore(StorePath));

    [Theory]
    [InlineData("\"C:\\ProgramData\\Battle.net\\Agent\\Blizzard Uninstaller.exe\" --lang=enUS --uid=hs_beta --displayname=\"Hearthstone\"", "hs_beta")]
    [InlineData("\"C:\\ProgramData\\Battle.net\\Agent\\Blizzard Uninstaller.exe\" --lang=enUS --uid=battle.net --displayname=\"Battle.net\"", "battle.net")]
    [InlineData("\"C:\\ProgramData\\Battle.net\\Agent\\Blizzard Uninstaller.exe\" --uid=\"prometheus\" --lang=enUS", "prometheus")]
    public void ParseUid_ReadsBlizzardUninstallCommands(string uninstallString, string expected)
    {
        Assert.Equal(expected, BattleNetScannerService.ParseUid(uninstallString));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\"C:\\Program Files\\Other\\unins000.exe\" --uid=hs_beta")] // not Blizzard's uninstaller
    [InlineData("\"C:\\ProgramData\\Battle.net\\Agent\\Blizzard Uninstaller.exe\" --lang=enUS")]
    [InlineData("\"C:\\ProgramData\\Battle.net\\Agent\\Blizzard Uninstaller.exe\" --fakeuid=x")]
    public void ParseUid_RejectsEverythingElse(string? uninstallString)
    {
        Assert.Null(BattleNetScannerService.ParseUid(uninstallString));
    }

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Hearthstone\Hearthstone.exe", @"C:\Program Files (x86)\Hearthstone\Hearthstone.exe")]
    [InlineData("\"C:\\Program Files (x86)\\Overwatch\\_retail_\\Overwatch.exe\",0", @"C:\Program Files (x86)\Overwatch\_retail_\Overwatch.exe")]
    [InlineData(@"C:\Games\SC2\Support\SC2Switcher.exe,-101", @"C:\Games\SC2\Support\SC2Switcher.exe")]
    [InlineData("C:/Games/x/x.exe", @"C:\Games\x\x.exe")]
    public void ParseDisplayIconPath_StripsQuotesAndIconIndex(string raw, string expected)
    {
        Assert.Equal(expected, BattleNetScannerService.ParseDisplayIconPath(raw));
    }

    [Fact]
    public void LookUp_UsesLiveCatalog_AndSavesItsCodes()
    {
        WriteCacheFile(@"ab\39\ab3923de", """{"fragment_id":"hearthstone","products":[{"base":{"retail":{"uid":"hs_beta"}},"id":"WTCG"}]}""");
        WriteCacheFile(@"b3\77\b3776ae8", """{"fragment_id":"prometheus","products":[{"base":{"retail":{"uid":"prometheus"}},"id":"Pro"}]}""");
        WriteCacheFile(@"00\00\image", "\u0089PNG not json");

        var lookup = NewScanner().LookUpProgramId("hs_beta");

        Assert.Equal("WTCG", lookup.ProgramId);
        Assert.Equal("Battle.net catalog", lookup.Source);
        // Every unambiguous pair was saved, not only the one asked for.
        Assert.Equal("Pro", new BattleNetCodeStore(StorePath).Get("prometheus"));
    }

    [Fact]
    public void LookUp_CacheGone_FallsBackToSavedMap()
    {
        new BattleNetCodeStore(StorePath).Merge(new System.Collections.Generic.Dictionary<string, string> { ["s2"] = "S2" });

        var lookup = NewScanner().LookUpProgramId("s2");

        Assert.Equal("S2", lookup.ProgramId);
        Assert.Equal("saved code map", lookup.Source);
        Assert.Contains("doesn't exist", lookup.Detail);
    }

    [Fact]
    public void LookUp_CatalogChangedFormat_FallsBackToSavedMap_AndSaysSo()
    {
        new BattleNetCodeStore(StorePath).Merge(new System.Collections.Generic.Dictionary<string, string> { ["hs_beta"] = "WTCG" });
        WriteCacheFile(@"aa\bb\new-format", """{"catalog_v2":{"entries":[]}}""");

        var lookup = NewScanner().LookUpProgramId("hs_beta");

        Assert.Equal("WTCG", lookup.ProgramId);
        Assert.Contains("format may have changed", lookup.Detail);
    }

    [Fact]
    public void LookUp_LiveCatalogBeatsAStaleSavedCode()
    {
        new BattleNetCodeStore(StorePath).Merge(new System.Collections.Generic.Dictionary<string, string> { ["hs_beta"] = "OLD" });
        WriteCacheFile(@"ab\39\ab3923de", """{"fragment_id":"hearthstone","products":[{"base":{"uid":"hs_beta"},"id":"WTCG"}]}""");

        Assert.Equal("WTCG", NewScanner().LookUpProgramId("hs_beta").ProgramId);
        Assert.Equal("WTCG", new BattleNetCodeStore(StorePath).Get("hs_beta"));
    }

    [Fact]
    public void LookUp_NowhereToBeFound_ReturnsNullWithAReason()
    {
        WriteCacheFile(@"ab\39\ab3923de", """{"fragment_id":"hearthstone","products":[{"base":{"uid":"hs_beta"},"id":"WTCG"}]}""");

        var lookup = NewScanner().LookUpProgramId("wow_classic");

        Assert.False(lookup.Found);
        Assert.Contains("not in Battle.net's catalog", lookup.Detail);
    }

    [Fact]
    public void ScanInstalledGames_NeverThrows()
    {
        // Reads the real registry: empty on a machine without Battle.net, and never the client itself.
        var games = NewScanner().ScanInstalledGames([]);
        Assert.DoesNotContain(games, g => g.Uid.Equals(BattleNetScannerService.ClientUid, StringComparison.OrdinalIgnoreCase));
    }
}
