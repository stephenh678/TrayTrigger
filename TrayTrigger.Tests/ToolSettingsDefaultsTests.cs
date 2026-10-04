using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>Tools is on by default, its tray submenu off, and the page opens in its default view, sort and tab.</summary>
public class ToolSettingsDefaultsTests
{
    [Fact]
    public void Tools_AreOnByDefault_TheTraySubmenuOff_WithDefaultViewSortAndTab()
    {
        var settings = new AppSettings();

        Assert.True(settings.EnableTools);
        Assert.False(settings.ShowToolsInTray);
        Assert.Equal(ToolCatalog.SortAlphabetical, settings.ToolsTraySortOption);
        Assert.Equal(ToolCatalog.SortAlphabetical, settings.ToolsSortOption);
        Assert.Equal(ToolCatalog.ViewLargeIcons, settings.ToolsViewMode);
        Assert.Equal(LibraryConstants.AllCategory, settings.LastToolsCategoryTab);
    }

    /// <summary>
    /// 1.4.8 turns Tools on once for an existing install, whose saved "off" was mostly the old
    /// default. Once only: a user who turns it off afterwards keeps it off.
    /// </summary>
    [Fact]
    public void Upgrade_TurnsToolsOnOnce_ThenLeavesTheChoiceAlone()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize(
            "{\"EnableTools\": false}", AppJsonContext.Default.AppSettings)!;
        Assert.False(settings.HasTurnedOnToolsFor148);

        Assert.True(settings.ApplyOneTimeUpgrades());
        Assert.True(settings.EnableTools);

        settings.EnableTools = false;
        Assert.False(settings.ApplyOneTimeUpgrades());
        Assert.False(settings.EnableTools);
    }

    /// <summary>The flag is saved, so the next start doesn't turn Tools back on.</summary>
    [Fact]
    public void Upgrade_IsRememberedAcrossASave()
    {
        var settings = new AppSettings();
        settings.ApplyOneTimeUpgrades();
        settings.EnableTools = false;

        string json = System.Text.Json.JsonSerializer.Serialize(settings, AppJsonContext.Default.AppSettings);
        var reloaded = System.Text.Json.JsonSerializer.Deserialize(json, AppJsonContext.Default.AppSettings)!;

        Assert.False(reloaded.ApplyOneTimeUpgrades());
        Assert.False(reloaded.EnableTools);
    }

    /// <summary>Restoring a backup from before 1.4.8 must not switch Tools back on over a later "off".</summary>
    [Fact]
    public void Restore_OfAnOlderBackup_KeepsTheUpgradeDone()
    {
        var restored = new AppSettings { EnableTools = false, HasTurnedOnToolsFor148 = false };
        var current = new AppSettings { EnableTools = false, HasTurnedOnToolsFor148 = true, HasTurnedOnCompactTrayFor149 = true };

        BackupService.MergeMachineSettings(restored, current, _ => true);

        Assert.True(restored.HasTurnedOnToolsFor148);
        Assert.False(restored.ApplyOneTimeUpgrades());
        Assert.False(restored.EnableTools);
    }

    /// <summary>
    /// 1.5.0 turns the compact tray menu on once for an existing install, whose saved "off" was
    /// mostly the old default. Once only: a user who turns it off afterwards keeps it off.
    /// </summary>
    [Fact]
    public void Upgrade_TurnsTheCompactTrayMenuOnOnce_ThenLeavesTheChoiceAlone()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize(
            "{\"CompactTrayMenu\": false, \"HasTurnedOnToolsFor148\": true}", AppJsonContext.Default.AppSettings)!;
        Assert.False(settings.HasTurnedOnCompactTrayFor149);

        Assert.True(settings.ApplyOneTimeUpgrades());
        Assert.True(settings.CompactTrayMenu);

        settings.CompactTrayMenu = false;
        string json = System.Text.Json.JsonSerializer.Serialize(settings, AppJsonContext.Default.AppSettings);
        var reloaded = System.Text.Json.JsonSerializer.Deserialize(json, AppJsonContext.Default.AppSettings)!;
        Assert.False(reloaded.ApplyOneTimeUpgrades());
        Assert.False(reloaded.CompactTrayMenu);
    }

    /// <summary>Restoring a backup from before 1.5.0 must not switch the compact menu back on over a later "off".</summary>
    [Fact]
    public void Restore_OfAPre149Backup_KeepsTheCompactUpgradeDone()
    {
        var restored = new AppSettings { CompactTrayMenu = false, HasTurnedOnToolsFor148 = true, HasTurnedOnCompactTrayFor149 = false };
        var current = new AppSettings { CompactTrayMenu = false, HasTurnedOnToolsFor148 = true, HasTurnedOnCompactTrayFor149 = true };

        BackupService.MergeMachineSettings(restored, current, _ => true);

        Assert.True(restored.HasTurnedOnCompactTrayFor149);
        Assert.False(restored.ApplyOneTimeUpgrades());
        Assert.False(restored.CompactTrayMenu);
    }

    [Fact]
    public void NewTool_IsUncategorizedAndNotAFavorite()
    {
        var tool = new ToolEntry();

        Assert.Equal(LibraryConstants.Uncategorized, tool.Category);
        Assert.False(tool.IsFavorite);
        Assert.False(tool.RunAsAdmin);
        Assert.False(string.IsNullOrWhiteSpace(tool.Id));
    }
}
