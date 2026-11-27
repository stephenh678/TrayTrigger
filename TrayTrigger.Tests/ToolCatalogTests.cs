using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>The Tools section's rules: sorting, tabs, search, where a new tool lands, and what may become a tool.</summary>
public class ToolCatalogTests
{
    private static ToolEntry Tool(string name, string category = "Uncategorized", bool favorite = false, string target = @"C:\Tools\x.exe") =>
        new() { Name = name, Category = category, IsFavorite = favorite, TargetPath = target };

    private static readonly ToolEntry[] Sample =
    [
        Tool("vortex", "Mods"),
        Tool("DLSS Swapper", "Graphics", favorite: true),
        Tool("Afterburner", "Graphics"),
        Tool("Discord", favorite: true),
    ];

    [Fact]
    public void Sort_AToZ_IgnoresCase()
    {
        var names = ToolCatalog.Sort(Sample, ToolCatalog.SortAlphabetical).Select(t => t.Name);
        Assert.Equal(["Afterburner", "Discord", "DLSS Swapper", "vortex"], names);
    }

    [Fact]
    public void Sort_ZToA()
    {
        var names = ToolCatalog.Sort(Sample, ToolCatalog.SortAlphabeticalDescending).Select(t => t.Name);
        Assert.Equal(["vortex", "DLSS Swapper", "Discord", "Afterburner"], names);
    }

    [Fact]
    public void Sort_FavoritesFirst_ThenAToZ()
    {
        var names = ToolCatalog.Sort(Sample, ToolCatalog.SortFavoritesFirst).Select(t => t.Name);
        Assert.Equal(["Discord", "DLSS Swapper", "Afterburner", "vortex"], names);
    }

    [Fact]
    public void Sort_RecentlyUsed_NewestFirst_ThenNeverUsedAToZ()
    {
        var now = new DateTime(2026, 11, 26, 18, 0, 0, DateTimeKind.Utc);
        ToolEntry[] tools =
        [
            Tool("vortex"),
            new() { Name = "Discord", TargetPath = @"C:\Tools\d.exe", LastUsedUtc = now.AddDays(-2) },
            new() { Name = "Afterburner", TargetPath = @"C:\Tools\a.exe", LastUsedUtc = now },
            Tool("DLSS Swapper"),
        ];
        var names = ToolCatalog.Sort(tools, ToolCatalog.SortRecentlyUsed).Select(t => t.Name);
        Assert.Equal(["Afterburner", "Discord", "DLSS Swapper", "vortex"], names);
    }

    [Fact]
    public void Sort_RecentlyAdded_NewestFirst_ThenUnrecordedAToZ()
    {
        var now = new DateTime(2026, 11, 26, 18, 0, 0, DateTimeKind.Utc);
        ToolEntry[] tools =
        [
            Tool("vortex"), // added before the date was recorded
            new() { Name = "OBS", TargetPath = @"C:\Tools\o.exe", AddedUtc = now.AddMinutes(-5) },
            new() { Name = "Afterburner", TargetPath = @"C:\Tools\a.exe", AddedUtc = now },
            Tool("Discord"),
        ];
        var names = ToolCatalog.Sort(tools, ToolCatalog.SortRecentlyAdded).Select(t => t.Name);
        Assert.Equal(["Afterburner", "OBS", "Discord", "vortex"], names);
    }

    [Fact]
    public void Sort_WithGamesFirst_StartsThenClosesThenTheRest_EachAToZ()
    {
        ToolEntry[] tools =
        [
            Tool("vortex"),
            new() { Name = "OneDrive", TargetPath = @"C:\Tools\o.exe", CloseForGames = true },
            new() { Name = "RTSS", TargetPath = @"C:\Tools\r.exe", StartWithGames = true },
            new() { Name = "Afterburner", TargetPath = @"C:\Tools\a.exe", StartWithGames = true },
            new() { Name = "Discord", TargetPath = @"C:\Tools\d.exe", CloseForGames = true },
            // A script can't start with games, so a box left ticked on one doesn't count.
            new() { Name = "Backup", TargetPath = @"C:\Tools\b.ps1", StartWithGames = true },
        ];
        var names = ToolCatalog.Sort(tools, ToolCatalog.SortWithGamesFirst).Select(t => t.Name);
        Assert.Equal(["Afterburner", "RTSS", "Discord", "OneDrive", "Backup", "vortex"], names);
    }

    [Fact]
    public void PageSorts_TurnRound_WithTheGroupAndUnusedToolsKeptInPlace()
    {
        var now = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
        var tools = new[]
        {
            new ToolEntry { Name = "Bravo", IsFavorite = true, LastUsedUtc = now.AddHours(-1) },
            new ToolEntry { Name = "Alpha", LastUsedUtc = now.AddHours(-5) },
            new ToolEntry { Name = "Charlie", IsFavorite = true },
            new ToolEntry { Name = "Delta", LastUsedUtc = now },
        };
        List<string> Sorted(string option, bool reversed) => ToolCatalog.Sort(tools, t => t, option, reversed).Select(t => t.Name).ToList();

        Assert.Equal(["Alpha", "Bravo", "Charlie", "Delta"], Sorted(ToolCatalog.PageSortName, false));
        Assert.Equal(["Delta", "Charlie", "Bravo", "Alpha"], Sorted(ToolCatalog.PageSortName, true));
        Assert.Equal(["Charlie", "Bravo", "Delta", "Alpha"], Sorted(ToolCatalog.PageSortFavoritesFirst, true));
        Assert.Equal(["Delta", "Bravo", "Alpha", "Charlie"], Sorted(ToolCatalog.SortRecentlyUsed, false));
        Assert.Equal(["Alpha", "Bravo", "Delta", "Charlie"], Sorted(ToolCatalog.SortRecentlyUsed, true));
    }

    [Theory]
    [InlineData("Alphabetical (A - Z)", ToolCatalog.PageSortName, false)]
    [InlineData("Alphabetical (Z - A)", ToolCatalog.PageSortName, true)]
    [InlineData("Favorites First (A - Z)", ToolCatalog.PageSortFavoritesFirst, false)]
    [InlineData("With Games First (A - Z)", ToolCatalog.PageSortWithGamesFirst, false)]
    [InlineData("Recently Used", ToolCatalog.SortRecentlyUsed, false)]
    [InlineData("Nonsense", ToolCatalog.PageSortName, false)]
    [InlineData(null, ToolCatalog.PageSortName, false)]
    public void ASavedPageSort_OpensInTheSameOrder_UnderItsNewName(string? saved, string option, bool reversed)
    {
        Assert.Equal((option, reversed), ToolCatalog.NormalizePageSortOption(saved));
    }

    [Fact]
    public void ThePageSortNames_DontSayWhichWay_TheTrayOnesStillDo()
    {
        Assert.Equal(["Name", "Favorites First", "With Games First", "Recently Used", "Recently Added"], ToolCatalog.PageSortOptions);
        Assert.Contains("Alphabetical (Z - A)", ToolCatalog.SortOptions);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Most Recently Played")]
    public void UnknownSortOption_FallsBackToAToZ(string? option)
    {
        Assert.Equal(ToolCatalog.SortAlphabetical, ToolCatalog.NormalizeSortOption(option));
        Assert.Equal("Afterburner", ToolCatalog.Sort(Sample, option).First().Name);
    }

    [Theory]
    [InlineData("large icons", ToolCatalog.ViewLargeIcons)]
    [InlineData("Small Icons", ToolCatalog.ViewSmallIcons)]
    [InlineData("LIST", ToolCatalog.ViewList)]
    [InlineData("Poster Grid", ToolCatalog.ViewLargeIcons)]
    [InlineData(null, ToolCatalog.ViewLargeIcons)]
    public void NormalizeViewMode_KnownModesOrLargeIcons(string? input, string expected)
    {
        Assert.Equal(expected, ToolCatalog.NormalizeViewMode(input));
    }

    [Fact]
    public void Tabs_AreAllFavoritesThenCategoriesAToZ_Deduplicated()
    {
        var tools = Sample.Append(Tool("Nexus", "mods")).Append(Tool("Blank", "  "));
        Assert.Equal(["All", "Favorites", "Graphics", "Mods", "Uncategorized"], ToolCatalog.TabsFor(tools));
    }

    [Fact]
    public void Tabs_ForNoTools_AreAllAndFavorites()
    {
        Assert.Equal(["All", "Favorites"], ToolCatalog.TabsFor([]));
    }

    [Fact]
    public void Tabs_DisappearWhenTheLastToolLeavesACategory()
    {
        var tool = Tool("Vortex", "Mods");
        Assert.Contains("Mods", ToolCatalog.TabsFor([tool]));

        tool.Category = "Graphics";
        Assert.DoesNotContain("Mods", ToolCatalog.TabsFor([tool]));
    }

    [Fact]
    public void IsInTab_AllFavoritesAndCategory()
    {
        var dlss = Sample[1];
        Assert.True(ToolCatalog.IsInTab(dlss, "All"));
        Assert.True(ToolCatalog.IsInTab(dlss, "Favorites"));
        Assert.True(ToolCatalog.IsInTab(dlss, "graphics"));
        Assert.False(ToolCatalog.IsInTab(dlss, "Mods"));
        Assert.False(ToolCatalog.IsInTab(Sample[0], "Favorites"));
    }

    [Fact]
    public void Search_MatchesNameCategoryOrPath()
    {
        var tool = Tool("Vortex", "Mods", target: @"C:\Program Files\Black Tree Gaming\Vortex.exe");
        Assert.True(ToolCatalog.MatchesSearch(tool, "vort"));
        Assert.True(ToolCatalog.MatchesSearch(tool, "MODS"));
        Assert.True(ToolCatalog.MatchesSearch(tool, "black tree"));
        Assert.True(ToolCatalog.MatchesSearch(tool, "  "));
        Assert.False(ToolCatalog.MatchesSearch(tool, "afterburner"));
    }

    [Theory]
    [InlineData("All", "Uncategorized")]
    [InlineData("Favorites", "Uncategorized")]
    [InlineData("Mods", "Mods")]
    [InlineData(null, "Uncategorized")]
    public void NewTool_GoesIntoTheSelectedCategoryTab(string? tab, string expected)
    {
        Assert.Equal(expected, ToolCatalog.CategoryForNewTool(tab));
    }

    [Fact]
    public void ValidateTarget_AcceptsAnExistingLocalExe()
    {
        Assert.Null(ToolCatalog.ValidateTarget(@"C:\Tools\DLSS Swapper.exe", _ => true));
    }

    [Theory]
    [InlineData(@"C:\Tools\backup.vbs")]
    [InlineData(@"C:\Tools\setup.py")]
    [InlineData(@"C:\Tools\readme.txt")]
    [InlineData("https://www.nexusmods.com")]
    [InlineData("steam://rungameid/10")]
    [InlineData(@"\\server\share\tool.exe")]
    [InlineData("")]
    public void ValidateTarget_RefusesAnythingButALocalExeOrScript(string target)
    {
        Assert.NotNull(ToolCatalog.ValidateTarget(target, _ => true));
    }

    [Fact]
    public void ValidateTarget_RefusesAMissingExe()
    {
        Assert.NotNull(ToolCatalog.ValidateTarget(@"C:\Tools\gone.exe", _ => false));
    }

    [Theory]
    [InlineData(@"C:\Users\me\Desktop\Vortex - Shortcut.lnk", "Vortex")]
    [InlineData(@"C:\Tools\DLSS Swapper.exe", "DLSS Swapper")]
    [InlineData(@"C:\Tools\.exe", "Unnamed Tool")]
    public void NameFromFile_DropsExtensionAndShortcutSuffix(string path, string expected)
    {
        Assert.Equal(expected, ToolCatalog.NameFromFile(path));
    }

    [Fact]
    public void HotkeyBinding_IsATool()
    {
        var tool = new ToolEntry { Id = "t1", Name = "Vortex", Hotkey = "Ctrl+Alt+V" };
        var binding = ToolCatalog.HotkeyBindingFor(tool);
        Assert.Equal(new HotkeyBinding("t1", "Vortex", "Ctrl+Alt+V", HotkeyOwnerKind.Tool), binding);
    }

    [Fact]
    public void ToolCategories_AreBuiltOnlyFromTools()
    {
        // Games aren't an input: a game category can never create a tool tab.
        var tabs = ToolCatalog.TabsFor([Tool("Vortex", "Mods")]);
        Assert.DoesNotContain("RPG", tabs);
        Assert.Equal(3, tabs.Count);
    }

    [Fact]
    public void BatchFavorite_AddsAllUnlessAllAreFavorites()
    {
        Assert.True(ToolCatalog.ShouldFavoriteAll([Tool("A", favorite: true), Tool("B")]));
        Assert.True(ToolCatalog.ShouldFavoriteAll([Tool("A"), Tool("B")]));
        Assert.False(ToolCatalog.ShouldFavoriteAll([Tool("A", favorite: true), Tool("B", favorite: true)]));
    }

    [Fact]
    public void BatchRunAsAdmin_TicksAllUnlessAllRunAsAdmin()
    {
        var admin = new ToolEntry { Name = "A", RunAsAdmin = true };
        var normal = new ToolEntry { Name = "B" };
        Assert.True(ToolCatalog.ShouldRunAllAsAdmin([admin, normal]));
        Assert.False(ToolCatalog.ShouldRunAllAsAdmin([admin, new ToolEntry { Name = "C", RunAsAdmin = true }]));
    }

    [Fact]
    public void BatchCategory_StartsWithTheSharedCategoryOrBlank()
    {
        Assert.Equal("Mods", ToolCatalog.CommonCategory([Tool("A", "Mods"), Tool("B", "mods")]));
        Assert.Equal("", ToolCatalog.CommonCategory([Tool("A", "Mods"), Tool("B", "Graphics")]));
    }

    [Theory]
    [InlineData("DLSS Swapper.exe")]
    [InlineData(@"C:DLSS Swapper.exe")]
    [InlineData(@"Tools\DLSS Swapper.exe")]
    public void ValidateTarget_RefusesARelativePath(string target)
    {
        Assert.NotNull(ToolCatalog.ValidateTarget(target, _ => true, _ => false));
    }

    [Fact]
    public void ValidateTarget_RefusesAMappedNetworkDrive()
    {
        Assert.NotNull(ToolCatalog.ValidateTarget(@"Z:\Tools\DLSS Swapper.exe", _ => true, _ => true));
    }

    [Theory]
    [InlineData("", @"D:\New")]           // blank
    [InlineData(@"C:\Gone", @"D:\New")]   // no longer there
    [InlineData(@"C:\Old\", @"D:\New")]   // just the old program's own folder
    [InlineData(@"C:\Saves", @"C:\Saves")] // chosen elsewhere: kept
    public void WorkingDirectory_FollowsTheProgramUnlessChosenElsewhere(string current, string expected)
    {
        string[] existing = [@"C:\Old\", @"C:\Old", @"C:\Saves"];
        Assert.Equal(expected, ToolCatalog.WorkingDirectoryAfterRetarget(
            current, @"C:\Old\app.exe", @"D:\New\app.exe", path => existing.Contains(path, StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public void SameLaunch_IsTheSameProgramWithTheSameArguments()
    {
        var backup = new ToolEntry { TargetPath = @"C:\Windows\System32\cmd.exe", Arguments = "/c backup.bat" };
        Assert.True(ToolCatalog.IsSameLaunch(backup, new ToolEntry { TargetPath = @"c:\windows\system32\CMD.exe", Arguments = " /c backup.bat " }));
        Assert.False(ToolCatalog.IsSameLaunch(backup, new ToolEntry { TargetPath = backup.TargetPath, Arguments = "/c cleanup.bat" }));
        Assert.False(ToolCatalog.IsSameLaunch(backup, new ToolEntry { TargetPath = backup.TargetPath }));
        Assert.True(ToolCatalog.IsSameLaunch(new ToolEntry { TargetPath = @"C:\Tools\Vortex.exe" }, new ToolEntry { TargetPath = @"C:\Tools\Vortex.exe" }));
    }

    [Fact]
    public void Sort_OrdersAnythingByItsTool()
    {
        var cards = Sample.Select(t => (Tool: t, Label: t.Name.ToUpperInvariant())).ToList();
        var labels = ToolCatalog.Sort(cards, c => c.Tool, ToolCatalog.SortAlphabetical).Select(c => c.Label);
        Assert.Equal(["AFTERBURNER", "DISCORD", "DLSS SWAPPER", "VORTEX"], labels);
    }
}
