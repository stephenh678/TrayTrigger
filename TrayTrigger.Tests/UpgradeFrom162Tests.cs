using System.IO;
using System.Text.Json.Nodes;
using TrayTrigger.Models;
using TrayTrigger.Services;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>
/// The files a 1.6.2 install leaves behind, opened by this version: nothing is lost, and what 1.7.0 added starts at its default.
/// A 1.6.2 file is made from a current one by taking out the keys 1.7.0 added, so it has exactly what 1.6.2 wrote.
/// </summary>
public class UpgradeFrom162Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tt-upgrade-" + Guid.NewGuid().ToString("N"));

    private StorageService Storage() => new(Path.Combine(_root, "data"), Path.Combine(_root, "local"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a test folder left in %TEMP% is not worth failing a test over */ }
    }

    private static void Strip(string file, params string[] keys)
    {
        var node = JsonNode.Parse(File.ReadAllText(file))!;
        foreach (var obj in node is JsonArray array ? array.Select(n => n!.AsObject()) : [node.AsObject()])
            foreach (string key in keys) obj.Remove(key);
        File.WriteAllText(file, node.ToJsonString());
    }

    [Fact]
    public void ASettingsFileFrom162_LoadsWithTheNewSettingsAtTheirDefaults()
    {
        var storage = Storage();
        storage.SaveSettings(new AppSettings { ThemeId = "light", GlobalManageHotkey = "Ctrl+Alt+H", TextSizePercent = 130, FontName = "Verdana" });
        Strip(Path.Combine(storage.BaseDirectory, "settings.json"), nameof(AppSettings.TextSizePercent), nameof(AppSettings.FontName),
            nameof(AppSettings.WindowSizes), nameof(AppSettings.ThemeEditPreviewOpen));

        var loaded = Storage().LoadSettings();

        // What 1.6.2 had is kept; what 1.7.0 added starts as it ships.
        Assert.Equal("light", loaded.ThemeId);
        Assert.Equal("Ctrl+Alt+H", loaded.GlobalManageHotkey);
        Assert.Equal(100, loaded.TextSizePercent);
        Assert.Equal(string.Empty, loaded.FontName);
        Assert.Empty(loaded.WindowSizes);
        Assert.False(loaded.ThemeEditPreviewOpen);
    }

    [Fact]
    public void AGamesFileFrom162_LoadsWithTheNewGameSettingsAtTheirDefaults()
    {
        var storage = Storage();
        storage.SaveGames([new GameEntry { Name = "Old Game", ExecutablePath = @"C:\Games\old.exe", Category = "Action", DetectedAntiCheat = "Easy Anti-Cheat", AutoHdr = HdrMode.On }]);
        Strip(Path.Combine(storage.BaseDirectory, "games.json"), nameof(GameEntry.DetectedAntiCheat), nameof(GameEntry.AutoHdr));

        var games = Storage().LoadGames();

        var game = Assert.Single(games);
        Assert.Equal("Old Game", game.Name);
        Assert.Equal("Action", game.Category);
        Assert.Null(game.DetectedAntiCheat);
        Assert.Equal(HdrMode.ProfileDefault, game.AutoHdr);
    }
}
