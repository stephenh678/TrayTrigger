using System;
using System.Collections.Generic;
using System.IO;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>
/// The catalog shapes below are trimmed copies of Battle.net's real cache files
/// (%LOCALAPPDATA%\Battle.net\Cache): each product's "id" is the launch code, and the install uids
/// it owns sit nested somewhere inside it.
/// </summary>
public class BattleNetCatalogTests
{
    private const string Hearthstone = """
        {"fragment_id":"hearthstone","installs":{"hs_beta":{"requires_sso_token":true,"tact_product":"hsb"}},
         "products":[{"base":{"variants":{"retail":{"supported_regions":["US","EU"],"uid":"hs_beta"},
                                          "tournament":{"uid":"hs_tournament"}}},"id":"WTCG"}]}
        """;

    private const string Overwatch = """
        {"fragment_id":"prometheus","installs":{},"products":[{"base":{"variants":[{"uid":"prometheus"},{"uid":"prometheus_test"}]},"id":"Pro"}]}
        """;

    // A second copy of Hearthstone's product with no install uids - some cache files look like this.
    private const string HearthstoneWithoutUids = """
        {"fragment_id":"hearthstone_presence","installs":{},"products":[{"base":{"name":"hearthstone#HS_NAME"},"id":"WTCG"}]}
        """;

    [Fact]
    public void ProductId_IsTheCodeForTheUidsItOwns()
    {
        var owners = BattleNetCatalog.NewOwnerMap();
        Assert.True(BattleNetCatalog.MergeInto(owners, Hearthstone));

        Assert.Equal("WTCG", BattleNetCatalog.Resolve(owners, "hs_beta", out bool ambiguous));
        Assert.False(ambiguous);
        Assert.Equal("WTCG", BattleNetCatalog.Resolve(owners, "hs_tournament", out _));
    }

    [Fact]
    public void ProgramId_KeepsItsCase_UidMatchIgnoresCase()
    {
        // The client rejects "pro"/"btlr" and accepts "Pro"/"BTLR".
        var owners = BattleNetCatalog.NewOwnerMap();
        BattleNetCatalog.MergeInto(owners, Overwatch);

        Assert.Equal("Pro", BattleNetCatalog.Resolve(owners, "PROMETHEUS", out _));
    }

    [Fact]
    public void CopyWithoutUids_DoesNotHideTheOwner()
    {
        var owners = BattleNetCatalog.NewOwnerMap();
        BattleNetCatalog.MergeInto(owners, HearthstoneWithoutUids);
        BattleNetCatalog.MergeInto(owners, Hearthstone);
        BattleNetCatalog.MergeInto(owners, HearthstoneWithoutUids);

        Assert.Equal("WTCG", BattleNetCatalog.Resolve(owners, "hs_beta", out _));
    }

    [Fact]
    public void UidClaimedByTwoProducts_IsNotGuessed()
    {
        var owners = BattleNetCatalog.NewOwnerMap();
        BattleNetCatalog.MergeInto(owners, Hearthstone);
        BattleNetCatalog.MergeInto(owners, """{"fragment_id":"x","products":[{"base":{"uid":"hs_beta"},"id":"HSX"}]}""");

        Assert.Null(BattleNetCatalog.Resolve(owners, "hs_beta", out bool ambiguous));
        Assert.True(ambiguous);
        Assert.False(BattleNetCatalog.Unambiguous(owners).ContainsKey("hs_beta"));
        Assert.Equal("WTCG", BattleNetCatalog.Unambiguous(owners)["hs_tournament"]);
    }

    [Fact]
    public void UnknownUid_ResolvesToNull()
    {
        var owners = BattleNetCatalog.NewOwnerMap();
        BattleNetCatalog.MergeInto(owners, Hearthstone);

        Assert.Null(BattleNetCatalog.Resolve(owners, "s2", out bool ambiguous));
        Assert.False(ambiguous);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"strings\":{}}")]                                   // JSON, but not a catalog
    [InlineData("{\"fragment_id\":\"x\",\"products\":[")]              // truncated mid-write
    [InlineData("{\"fragment_id\":\"x\",\"products\":{\"id\":\"A\"}}")] // products isn't an array
    public void NonCatalogText_IsRejectedWithoutThrowing(string text)
    {
        var owners = BattleNetCatalog.NewOwnerMap();
        Assert.False(BattleNetCatalog.MergeInto(owners, text));
        Assert.Empty(owners);
    }

    [Theory]
    [InlineData("WTCG", true)]
    [InlineData("Pro", true)]
    [InlineData("S2", true)]
    [InlineData("launch WTCG", false)]
    [InlineData("WTCG\" --x", false)]
    [InlineData("", false)]
    public void ProgramIdsThatReachTheCommandLine_AreValidated(string id, bool valid)
    {
        Assert.Equal(valid, BattleNetCatalog.IsValidProgramId(id));
    }

    [Fact]
    public void ProductWithAnUnsafeId_IsIgnored()
    {
        var owners = BattleNetCatalog.NewOwnerMap();
        BattleNetCatalog.MergeInto(owners, """{"fragment_id":"x","products":[{"base":{"uid":"evil"},"id":"A\" --exec=\"x"}]}""");
        Assert.Null(BattleNetCatalog.Resolve(owners, "evil", out _));
    }
}

public sealed class BattleNetCodeStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TrayTriggerBnetStore", Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "battlenet-codes.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Merge_AddsAndPersists()
    {
        var store = new BattleNetCodeStore(FilePath);
        Assert.Equal(2, store.Merge(new Dictionary<string, string> { ["hs_beta"] = "WTCG", ["s2"] = "S2" }));

        var reopened = new BattleNetCodeStore(FilePath);
        Assert.Equal("WTCG", reopened.Get("hs_beta"));
        Assert.Equal("S2", reopened.Get("S2"));
    }

    [Fact]
    public void Merge_UpdatesChangedCodes_AndNeverRemoves()
    {
        var store = new BattleNetCodeStore(FilePath);
        store.Merge(new Dictionary<string, string> { ["hs_beta"] = "WTCG", ["prometheus"] = "Pro" });

        // A later catalog without prometheus and with a changed code for hs_beta.
        Assert.Equal(1, store.Merge(new Dictionary<string, string> { ["hs_beta"] = "HSNEW" }));

        var reopened = new BattleNetCodeStore(FilePath);
        Assert.Equal("HSNEW", reopened.Get("hs_beta"));
        Assert.Equal("Pro", reopened.Get("prometheus"));
    }

    [Fact]
    public void Merge_NothingNew_DoesNotWrite()
    {
        var store = new BattleNetCodeStore(FilePath);
        Assert.Equal(0, store.Merge(new Dictionary<string, string>()));
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void InvalidCodes_AreNeverStored()
    {
        var store = new BattleNetCodeStore(FilePath);
        Assert.Equal(0, store.Merge(new Dictionary<string, string> { ["x"] = "bad code" }));
        Assert.Null(store.Get("x"));
    }

    [Fact]
    public void CorruptFile_ReadsAsEmpty_AndIsRewrittenOnNextMerge()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ not json");

        var store = new BattleNetCodeStore(FilePath);
        Assert.Null(store.Get("hs_beta"));
        store.Merge(new Dictionary<string, string> { ["hs_beta"] = "WTCG" });
        Assert.Equal("WTCG", new BattleNetCodeStore(FilePath).Get("hs_beta"));
    }

    /// <summary>
    /// Codes mirrors Blizzard's whole catalog (hundreds of vendor/PTR uids), so the names of the
    /// games actually installed are what make the file readable as a backup.
    /// </summary>
    [Fact]
    public void RememberInstalled_WritesNamesBesideTheCodes_AndSurvivesReload()
    {
        var store = new BattleNetCodeStore(FilePath);
        store.Merge(new Dictionary<string, string> { ["hs_beta"] = "WTCG", ["hs_vendor_1"] = "WTCG" });
        Assert.Equal(1, store.RememberInstalled([("hs_beta", "Hearthstone")]));

        var reopened = new BattleNetCodeStore(FilePath);
        Assert.Equal("Hearthstone", reopened.GetInstalledName("hs_beta"));
        Assert.Null(reopened.GetInstalledName("hs_vendor_1"));
        Assert.Equal("WTCG", reopened.Get("hs_beta"));

        // The file reads top-down: names first, then the catalog mirror.
        string json = File.ReadAllText(FilePath);
        Assert.True(json.IndexOf("\"Installed\"", StringComparison.Ordinal) < json.IndexOf("\"Codes\"", StringComparison.Ordinal));
    }

    [Fact]
    public void RememberInstalled_NeverRemoves_AndWritesOnlyOnChange()
    {
        var store = new BattleNetCodeStore(FilePath);
        store.RememberInstalled([("hs_beta", "Hearthstone"), ("prometheus", "Overwatch")]);
        var written = File.GetLastWriteTimeUtc(FilePath);

        // Overwatch uninstalled: the scan no longer lists it. Nothing changed, nothing written.
        Assert.Equal(0, store.RememberInstalled([("hs_beta", "Hearthstone")]));
        Assert.Equal(written, File.GetLastWriteTimeUtc(FilePath));
        Assert.Equal("Overwatch", store.GetInstalledName("prometheus"));

        // A renamed entry (Blizzard retitled the game) is updated.
        Assert.Equal(1, store.RememberInstalled([("prometheus", "Overwatch 2")]));
        Assert.Equal("Overwatch 2", new BattleNetCodeStore(FilePath).GetInstalledName("prometheus"));
    }

    /// <summary>A beta.2/beta.3 file has no Installed section; it must still load and gain one on the next save.</summary>
    [Fact]
    public void FileWithoutInstalledSection_LoadsAndUpgrades()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ \"Version\": 1, \"UpdatedUtc\": \"2026-09-13T03:12:45Z\", \"Codes\": { \"wow\": \"WoW\" } }");

        var store = new BattleNetCodeStore(FilePath);
        Assert.Equal("WoW", store.Get("wow"));
        store.RememberInstalled([("wow", "World of Warcraft")]);

        var reopened = new BattleNetCodeStore(FilePath);
        Assert.Equal("WoW", reopened.Get("wow"));
        Assert.Equal("World of Warcraft", reopened.GetInstalledName("wow"));
    }
}
