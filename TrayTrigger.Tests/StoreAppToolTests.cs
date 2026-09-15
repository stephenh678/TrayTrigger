using System.Linq;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>Store app tools: which app IDs are accepted, how they compare, search and launch, and reading dropped shell items.</summary>
public class StoreAppToolTests
{
    private const string XboxAppId = "Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App";

    private static ToolEntry StoreApp(string appId = XboxAppId, string arguments = "") => new() { Name = "Xbox", AppId = appId, Arguments = arguments };

    [Theory]
    [InlineData(XboxAppId)]
    [InlineData("Microsoft.WindowsTerminal_8wekyb3d8bbwe!App")]
    [InlineData("  Microsoft.WindowsTerminal_8wekyb3d8bbwe!App  ")]
    [InlineData("5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App")]
    public void ValidateAppId_AcceptsAStoreAppId(string appId)
    {
        Assert.Null(ToolCatalog.ValidateAppId(appId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Chrome")]
    [InlineData("Microsoft.Office.WINWORD.EXE.15")]
    [InlineData("Microsoft.GamingApp_8wekyb3d8bbwe")]
    [InlineData("Microsoft.GamingApp_8wekyb3d8bbwe!")]
    [InlineData("Microsoft.GamingApp_8wekyb3d8bbw!App")]
    [InlineData("Microsoft.GamingApp_8wekyb3d8bbwe!1App")]
    [InlineData("ab_8wekyb3d8bbwe!App")]
    [InlineData("Microsoft.GamingApp_8wekyb3d8bbwe!App extra")]
    [InlineData("Microsoft.GamingApp_8wekyb3d8bbwe!App\" /select,C:\\")]
    [InlineData("..\\Microsoft.GamingApp_8wekyb3d8bbwe!App")]
    [InlineData("{6D809377-6AF0-444B-8957-A3773F02200E}\\7-Zip\\7zFM.exe")]
    public void ValidateAppId_RefusesAnythingElse(string? appId)
    {
        Assert.NotNull(ToolCatalog.ValidateAppId(appId));
    }

    [Fact]
    public void PackageFamilyName_IsTheIdBeforeTheBang()
    {
        Assert.Equal("Microsoft.GamingApp_8wekyb3d8bbwe", ToolCatalog.PackageFamilyNameOf(XboxAppId));
        Assert.Null(ToolCatalog.PackageFamilyNameOf("Chrome"));
    }

    [Fact]
    public void AppsFolderPath_RoundTrips_AndNothingElseReadsAsAnApp()
    {
        Assert.Equal(XboxAppId, ToolCatalog.AppIdFromAppsFolderPath(ToolCatalog.AppsFolderPath(XboxAppId)));
        Assert.Null(ToolCatalog.AppIdFromAppsFolderPath(@"C:\Tools\Vortex.exe"));
        Assert.Null(ToolCatalog.AppIdFromAppsFolderPath(@"shell:AppsFolder\Chrome"));
        Assert.Null(ToolCatalog.AppIdFromAppsFolderPath(null));
    }

    [Fact]
    public void IsStoreApp_OnlyWithAnAppId()
    {
        Assert.True(ToolCatalog.IsStoreApp(StoreApp()));
        Assert.False(ToolCatalog.IsStoreApp(new ToolEntry { TargetPath = @"C:\Tools\Vortex.exe" }));
        Assert.False(ToolCatalog.IsStoreApp(new ToolEntry { AppId = "  " }));
    }

    [Fact]
    public void SameLaunch_ComparesStoreAppsByAppIdAndArguments()
    {
        Assert.True(ToolCatalog.IsSameLaunch(StoreApp(), StoreApp(XboxAppId.ToLowerInvariant())));
        Assert.False(ToolCatalog.IsSameLaunch(StoreApp(), StoreApp("Microsoft.WindowsTerminal_8wekyb3d8bbwe!App")));
        Assert.False(ToolCatalog.IsSameLaunch(StoreApp(), StoreApp(arguments: "--profile")));
        Assert.False(ToolCatalog.IsSameLaunch(StoreApp(), new ToolEntry { TargetPath = "" }));
    }

    [Fact]
    public void SearchAndListView_ShowTheAppId()
    {
        Assert.True(ToolCatalog.MatchesSearch(StoreApp(), "gamingapp"));
        Assert.Equal($"Store app: {XboxAppId}", ToolCatalog.LaunchDisplay(StoreApp()));
        Assert.Equal(@"C:\Tools\Vortex.exe", ToolCatalog.LaunchDisplay(new ToolEntry { TargetPath = @"C:\Tools\Vortex.exe" }));
    }

    [Fact]
    public void BatchRunAsAdmin_OnlyCountsPrograms()
    {
        var storeApp = StoreApp();
        var elevated = new ToolEntry { TargetPath = @"C:\Tools\Afterburner.exe", RunAsAdmin = true };
        Assert.False(ToolCatalog.ShouldRunAllAsAdmin([storeApp, elevated]));
        Assert.True(ToolCatalog.ShouldRunAllAsAdmin([storeApp, new ToolEntry { TargetPath = @"C:\Tools\Vortex.exe" }]));
    }

    [Fact]
    public void PackageVersion_BelongsToItsFamilyOnly()
    {
        Assert.True(PackagedApps.IsVersionOf("Microsoft.GamingApp_2608.1001.17.0_x64__8wekyb3d8bbwe", "Microsoft.GamingApp_8wekyb3d8bbwe"));
        Assert.False(PackagedApps.IsVersionOf("Microsoft.GamingServices_31.0.0.0_x64__8wekyb3d8bbwe", "Microsoft.GamingApp_8wekyb3d8bbwe"));
        Assert.False(PackagedApps.IsInstalled("Chrome"));
    }

    [Fact]
    public void Launch_RefusesAMalformedAppId_AndReportsAnUninstalledApp()
    {
        var malformed = ToolLauncherService.CheckStoreApp(StoreApp("Chrome\" /c calc"), _ => true);
        Assert.Equal(ToolLaunchOutcome.Failed, malformed?.Outcome);

        Assert.Equal(ToolLaunchOutcome.Missing, ToolLauncherService.CheckStoreApp(StoreApp(), _ => false)?.Outcome);
        Assert.Null(ToolLauncherService.CheckStoreApp(StoreApp(), id => id == XboxAppId));
    }

    [Fact]
    public void SanitizedTools_NeverHaveANullAppId()
    {
        var tool = Assert.Single(StorageService.SanitizeTools([new ToolEntry { Id = "t1", AppId = null! }]));
        Assert.Equal(string.Empty, tool.AppId);
    }

    // ------------------------------------------------------------------ dropped shell items

    [Fact]
    public void DropEffect_IsCopyWhenAllowed_ElseLink_ForAppsFolderDrags()
    {
        Assert.Equal(System.Windows.DragDropEffects.Copy, Views.ToolsView.DropEffectFor(System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Move | System.Windows.DragDropEffects.Link));
        Assert.Equal(System.Windows.DragDropEffects.Link, Views.ToolsView.DropEffectFor(System.Windows.DragDropEffects.Link));
        Assert.Equal(System.Windows.DragDropEffects.None, Views.ToolsView.DropEffectFor(System.Windows.DragDropEffects.Move));
    }

    /// <summary>A CIDA: count, then count + 1 offsets, then the ID lists they point at.</summary>
    private static byte[] IdListArray(params byte[][] idLists)
    {
        int count = idLists.Length - 1;
        int header = 4 + 4 * idLists.Length;
        var data = new List<byte>(BitConverter.GetBytes((uint)count));
        int offset = header;
        foreach (var list in idLists)
        {
            data.AddRange(BitConverter.GetBytes((uint)offset));
            offset += list.Length;
        }
        foreach (var list in idLists) data.AddRange(list);
        return data.ToArray();
    }

    private static readonly byte[] EmptyIdList = [0, 0];
    private static readonly byte[] OneItemIdList = [4, 0, 0xAB, 0xCD, 0, 0];

    [Fact]
    public void IdListArray_ReadsTheParentAndEachItem()
    {
        var offsets = ShellAppResolver.ParseIdListOffsets(IdListArray(EmptyIdList, OneItemIdList, OneItemIdList));
        Assert.NotNull(offsets);
        Assert.Equal([16, 18, 24], offsets);
    }

    [Fact]
    public void IdListArray_RefusesDamagedData()
    {
        Assert.Null(ShellAppResolver.ParseIdListOffsets([]));
        Assert.Null(ShellAppResolver.ParseIdListOffsets(IdListArray(EmptyIdList)));

        // An item whose size runs past the end, and one with no terminator.
        Assert.Null(ShellAppResolver.ParseIdListOffsets(IdListArray(EmptyIdList, [40, 0, 1, 2])));
        Assert.Null(ShellAppResolver.ParseIdListOffsets(IdListArray(EmptyIdList, [4, 0, 1, 2])));

        // An offset inside the header, or past the end.
        var data = IdListArray(EmptyIdList, OneItemIdList);
        BitConverter.GetBytes(4u).CopyTo(data, 8);
        Assert.Null(ShellAppResolver.ParseIdListOffsets(data));
        BitConverter.GetBytes(9999u).CopyTo(data, 8);
        Assert.Null(ShellAppResolver.ParseIdListOffsets(data));

        // A count far beyond anything dragged at once.
        var huge = IdListArray(EmptyIdList, OneItemIdList);
        BitConverter.GetBytes(100_000u).CopyTo(huge, 0);
        Assert.Null(ShellAppResolver.ParseIdListOffsets(huge));
    }

    [Fact]
    public void KnownFolderParsingNames_ExpandToAProgramPath()
    {
        var programFiles = new Guid("6D809377-6AF0-444B-8957-A3773F02200E");
        string? Folder(Guid id) => id == programFiles ? @"C:\Program Files" : null;

        Assert.Equal(@"C:\Program Files\7-Zip\7zFM.exe", ShellAppResolver.ExpandKnownFolder(@"{6D809377-6AF0-444B-8957-A3773F02200E}\7-Zip\7zFM.exe", Folder));
        Assert.Null(ShellAppResolver.ExpandKnownFolder(@"{11111111-1111-1111-1111-111111111111}\x.exe", Folder));
        Assert.Null(ShellAppResolver.ExpandKnownFolder(@"{6D809377-6AF0-444B-8957-A3773F02200E}\C:\Windows\x.exe", Folder));
        Assert.Null(ShellAppResolver.ExpandKnownFolder("Microsoft.Windows.Explorer", Folder));
        Assert.Null(ShellAppResolver.ExpandKnownFolder(XboxAppId, Folder));
        Assert.Null(ShellAppResolver.ExpandKnownFolder(null, Folder));
    }
}
