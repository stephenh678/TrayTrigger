using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>Game Details' requirements card: Steam's text as rows, this PC's part, and warnings only for sure shortfalls.</summary>
public class SystemRequirementsTests
{
    // The cleaned Steam text of a real game (the card in the 2.0.0-beta.3 review), "Minimum:" left in as it often is.
    private const string Minimum =
        "Minimum:\n• Requires a 64-bit processor and operating system\n• OS: Windows 10 (64 bit)\n" +
        "• Processor: AMD / Intel CPU running at 3.0 GHz or higher: AMD Kaveri A10-7850K or Intel Pentium DualCore G3220 or newer / Ryzen 5 2400G (for systems using an integrated GPU)\n" +
        "• Memory: 8 GB RAM\n" +
        "• Graphics: AMD/NVIDIA dedicated graphics card, with at least 4GB of dedicated VRAM and with at least DirectX 11.0 and Shader Model 5.1 support\n" +
        "• DirectX: Version 11\n• Storage: 16 GB available space\n• Sound Card: Integrated or dedicated DirectX 9 compatible soundcard\n" +
        "• Additional Notes: Windows 7 and 8.1 are supported but some AMD Graphics Cards might show weaker performance on those OS versions.";

    private static readonly PcSummary BigPc = new()
    {
        Os = "Windows 11 Pro", WindowsMajor = 11, Cpu = "Intel Core i9-12900K", CpuCores = 16, InstalledRamGb = 32,
        Gpu = "NVIDIA GeForce RTX 5080", GpuMemoryGb = 16, DirectXMajor = 12, DirectXLabel = "DirectX 12 Ultimate",
        SoundDevice = "Speakers (Realtek USB Audio)", Drives = [("C:", 447.3), ("D:", 120)],
    };

    private static readonly PcSummary SmallPc = BigPc with
    {
        WindowsMajor = 10, Os = "Windows 10 Home", InstalledRamGb = 8, GpuMemoryGb = 2, DirectXMajor = 11, DirectXLabel = "DirectX 11",
        Drives = [("C:", 10)],
    };

    private static RequirementRow Row(IEnumerable<RequirementRow> rows, RequirementKind kind) => rows.Single(r => r.Kind == kind);

    [Fact]
    public void Parse_SplitsLabelledLinesIntoRows_AndTheRestIntoNotes()
    {
        var parsed = SystemRequirements.Parse(Minimum);

        Assert.True(parsed.HasRows);
        Assert.Equal(["OS", "Processor", "Memory", "Graphics", "DirectX", "Storage", "Sound card"], parsed.Rows.Select(r => r.Label));
        Assert.StartsWith("AMD / Intel CPU running at 3.0 GHz or higher: AMD Kaveri", Row(parsed.Rows, RequirementKind.Processor).Value);
        Assert.Equal(["Requires a 64-bit processor and operating system", "Windows 7 and 8.1 are supported but some AMD Graphics Cards might show weaker performance on those OS versions."],
            parsed.Notes);
    }

    [Theory]
    [InlineData("• OS *: Windows 10\n* Starting January 1st, 2024, the Steam Client will only support Windows 10 and later versions.", "OS")]
    [InlineData("• Hard Drive: 20 GB\n", "Storage")]
    [InlineData("• Video Card: GTX 970\n", "Graphics")]
    public void Parse_KnowsSteamsOtherSpellings(string text, string label)
    {
        var parsed = SystemRequirements.Parse(text);
        Assert.Equal(label, parsed.Rows.First().Label);
    }

    [Fact]
    public void Parse_TextWithNoLabelsTheCardKnows_HasNoRows_SoTheCardShowsItAsItIs()
    {
        Assert.False(SystemRequirements.Parse("Any modern PC should run this game.").HasRows);
        Assert.False(SystemRequirements.Parse(null).HasRows);
    }

    [Fact]
    public void Compare_ShowsThisPcsPart_AndNeverSaysItMeetsAnything()
    {
        var rows = SystemRequirements.Compare(SystemRequirements.Parse(Minimum).Rows, BigPc, new("C:", false));

        Assert.All(rows, r => Assert.False(r.HasWarning));
        Assert.Equal("Windows 11 Pro", Row(rows, RequirementKind.Os).Yours);
        Assert.Equal("Intel Core i9-12900K, 16 cores", Row(rows, RequirementKind.Processor).Yours);
        Assert.Equal("32 GB", Row(rows, RequirementKind.Memory).Yours);
        Assert.Equal("NVIDIA GeForce RTX 5080, 16 GB", Row(rows, RequirementKind.Graphics).Yours);
        Assert.Equal("DirectX 12 Ultimate", Row(rows, RequirementKind.DirectX).Yours);
        Assert.Equal("Installed on C:", Row(rows, RequirementKind.Storage).Yours);
        Assert.Equal("Speakers (Realtek USB Audio)", Row(rows, RequirementKind.Sound).Yours);
    }

    [Fact]
    public void Compare_WarnsOnlyWhereTheLineStatesWhatThisPcLacks()
    {
        var rows = SystemRequirements.Compare(SystemRequirements.Parse(
            "• OS: Windows 11\n• Processor: Intel Core i7-13700K\n• Memory: 16 GB RAM\n• Graphics: GTX 1060 6GB or RX 580 8GB\n• DirectX: Version 12\n• Storage: 90 GB available space").Rows,
            SmallPc, new(null, NotInstalled: true));

        Assert.Equal("Below: needs Windows 11", Row(rows, RequirementKind.Os).Warning);
        Assert.False(Row(rows, RequirementKind.Processor).HasWarning);
        Assert.Equal("Below: needs 16 GB", Row(rows, RequirementKind.Memory).Warning);
        Assert.Equal("Below: needs 6 GB of graphics memory", Row(rows, RequirementKind.Graphics).Warning);
        Assert.Equal("Below: your graphics card supports DirectX 11", Row(rows, RequirementKind.DirectX).Warning);
        Assert.Equal("10 GB free on C:", Row(rows, RequirementKind.Storage).Yours);
        Assert.Equal("Not enough free space: needs 90 GB", Row(rows, RequirementKind.Storage).Warning);
    }

    [Fact]
    public void Compare_GivesNoWarningWhereItCantBeSure()
    {
        var rows = SystemRequirements.Compare(SystemRequirements.Parse(
            "• OS: Windows 10 / 11\n• Graphics: NVIDIA GeForce GTX 1070\n• Storage: 90 GB available space").Rows,
            SmallPc, new(null, NotInstalled: false));

        Assert.All(rows, r => Assert.False(r.HasWarning));
        // Installed by its launcher's account but with no folder TrayTrigger can see (a game added from a Steam
        // shortcut): no free-space line at all, so no "Not enough free space" for a game that is installed.
        Assert.False(Row(rows, RequirementKind.Storage).HasYours);
    }

    [Fact]
    public void Storage_ForAGameItsLauncherSaysIsNotInstalled_UsesFreeSpace_EvenWithAFolderLeftBehind()
    {
        var rows = SystemRequirements.Compare(SystemRequirements.Parse("• Storage: 90 GB available space").Rows,
            SmallPc, new("C:", NotInstalled: true));
        Assert.Equal("10 GB free on C:", rows[0].Yours);
        Assert.True(rows[0].HasWarning);
    }

    [Fact]
    public void Storage_AFullDrive_StillWarns()
    {
        var rows = SystemRequirements.Compare(SystemRequirements.Parse("• Storage: 50 GB available space").Rows,
            SmallPc with { Drives = [("C:", 0)] }, new(null, NotInstalled: true));
        Assert.Equal("0 GB free on C:", rows[0].Yours);
        Assert.Equal("Not enough free space: needs 50 GB", rows[0].Warning);
    }

    [Fact]
    public void Memory_WhenTheInstalledAmountCantBeRead_ShowsWhatWindowsCanUse_AndNeverWarns()
    {
        var pc = BigPc with { InstalledRamGb = 0, UsableRamGb = 31.7 };
        var rows = SystemRequirements.Compare(SystemRequirements.Parse("• Memory: 64 GB RAM").Rows, pc, default);
        Assert.Equal("32 GB", rows[0].Yours);
        Assert.False(rows[0].HasWarning);
    }

    [Theory]
    [InlineData("Minimum Requirements:\n• OS: Windows 10")]
    [InlineData("RECOMMENDED SYSTEM REQUIREMENTS\n• OS: Windows 10")]
    [InlineData("Minimum: OS: Windows 10")]
    public void Parse_SkipsSteamsHeadings_AndReadsALineAfterOne(string text)
    {
        var parsed = SystemRequirements.Parse(text);
        Assert.Equal("OS", Assert.Single(parsed.Rows).Label);
        Assert.Empty(parsed.Notes);
    }

    [Fact]
    public void Parse_AHeadingWithTheListsFirstLine_KeepsThatLineAsANote()
    {
        var parsed = SystemRequirements.Parse("Minimum: Requires a 64-bit processor and operating system\n• OS: Windows 10");
        Assert.Equal(["OS"], parsed.Rows.Select(r => r.Label));
        Assert.Equal(["Requires a 64-bit processor and operating system"], parsed.Notes);
    }

    [Fact]
    public void Parse_TextWithNoBreaksBetweenParts_StillGivesOneRowEach()
    {
        var parsed = SystemRequirements.Parse("OS: Windows 10 64-bit Processor: Intel Core i5-4460 Memory: 8 GB RAM Graphics: GTX 970 Storage: 50 GB available space");
        Assert.Equal(["OS", "Processor", "Memory", "Graphics", "Storage"], parsed.Rows.Select(r => r.Label));
        Assert.Equal("Windows 10 64-bit", parsed.Rows[0].Value);
        Assert.Equal("8 GB RAM", parsed.Rows[2].Value);
    }

    [Theory]
    [InlineData(0xc200, null, 12, "DirectX 12 Ultimate")]
    [InlineData(0xc000, null, 12, "DirectX 12")]
    [InlineData(0xb000, null, 12, "DirectX 12")] // a GTX 650 Ti: DirectX 12 at feature level 11_0
    [InlineData(null, 0xb100, 11, "DirectX 11")] // DirectX 12 refuses it, DirectX 11 takes it
    [InlineData(null, 0xa100, null, "")]         // feature level 10: not claimed either way
    [InlineData(null, null, null, "")]
    public void DirectXOf_ADirectX12CardAtAnyFeatureLevel_IsDirectX12(int? d3d12, int? d3d11, int? major, string label) =>
        Assert.Equal((major, label), SystemInfoService.DirectXOf(d3d12, d3d11));

    [Fact]
    public void Compare_WithoutThePcRead_LeavesTheRowsAsTheyAre()
    {
        var rows = SystemRequirements.Parse(Minimum).Rows;
        Assert.Same(rows, SystemRequirements.Compare(rows, null, default));
    }

    [Fact]
    public void ThirtyTwoGigabytesReadAs31Point9_IsNotBelow32()
    {
        var rows = SystemRequirements.Compare(SystemRequirements.Parse("• Memory: 32 GB RAM").Rows, BigPc with { InstalledRamGb = 31.9 }, default);
        Assert.False(rows[0].HasWarning);
    }

    [Theory]
    [InlineData("8 GB RAM", 8.0)]
    [InlineData("512 MB", 0.5)]
    [InlineData("GTX 1060 6GB or RX 580 8GB", 6.0)]
    [InlineData("GeForce GTX 970", null)]
    public void SmallestGb_ReadsTheLeastTheLineAsksFor(string value, double? expected) =>
        Assert.Equal(expected, SystemRequirements.SmallestGb(value));

    [Theory]
    [InlineData(32, "32 GB")]
    [InlineData(1.5, "1.5 GB")]
    [InlineData(447.3, "447 GB")]
    [InlineData(11110, "10.8 TB")]
    public void FormatGb_ReadsLikeExplorer(double gb, string expected) =>
        Assert.Equal(expected, SystemRequirements.FormatGb(gb));

    [Theory]
    [InlineData("Version 11", 11)]
    [InlineData("Version 9.0c", 9)]
    [InlineData("DirectX 12", 12)]
    [InlineData("Compatible", null)]
    public void RequiredDirectX_ReadsTheVersion(string value, int? expected) =>
        Assert.Equal(expected, SystemRequirements.RequiredDirectX(value));
}
