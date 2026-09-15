using System.IO;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>tools.json: saved beside games.json, round-trips every field, recovers from its backup, and never touches the game library.</summary>
public class ToolStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerToolTests_" + Guid.NewGuid().ToString("N"));
    private readonly StorageService _storage;

    public ToolStorageTests()
    {
        _storage = new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void NoFile_LoadsEmpty()
    {
        Assert.Empty(_storage.LoadTools());
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEveryField()
    {
        var tool = new ToolEntry
        {
            Id = "t1",
            Name = "DLSS Swapper",
            TargetPath = @"C:\Tools\DLSS Swapper.exe",
            Arguments = "--minimized",
            WorkingDirectory = @"C:\Tools",
            RunAsAdmin = true,
            IconPath = @"C:\cache\Icons\t1.png",
            Hotkey = "Ctrl+Alt+D",
            Category = "Graphics",
            IsFavorite = true
        };
        _storage.SaveTools([tool]);

        var loaded = Assert.Single(_storage.LoadTools());
        Assert.Equivalent(tool, loaded);
        Assert.True(File.Exists(Path.Combine(_storage.BaseDirectory, "tools.json")));
    }

    [Fact]
    public void SavingTools_LeavesGamesJsonAlone()
    {
        _storage.SaveGames([new GameEntry { Id = "g1", Name = "Elden Ring" }]);
        string gamesBefore = File.ReadAllText(Path.Combine(_storage.BaseDirectory, "games.json"));

        _storage.SaveTools([new ToolEntry { Id = "t1", Name = "Vortex" }]);

        Assert.Equal(gamesBefore, File.ReadAllText(Path.Combine(_storage.BaseDirectory, "games.json")));
        Assert.Single(_storage.LoadGames());
    }

    [Fact]
    public void CorruptPrimary_RecoversFromBackup()
    {
        _storage.SaveTools([new ToolEntry { Id = "t1", Name = "First" }]);
        _storage.SaveTools([new ToolEntry { Id = "t1", Name = "First" }, new ToolEntry { Id = "t2", Name = "Second" }]);
        File.WriteAllText(Path.Combine(_storage.BaseDirectory, "tools.json"), "{ not json");

        var fresh = new StorageService(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"));
        var recovered = fresh.LoadTools();

        Assert.Equal("First", Assert.Single(recovered).Name);
        Assert.NotNull(fresh.ToolsLoadWarning);
    }

    [Fact]
    public void HandEditedFile_DropsNullsFillsTextAndRepairsIds()
    {
        _storage.SaveTools(new ToolEntry[]
        {
            null!,
            new() { Id = "a1", Name = null!, TargetPath = null!, Arguments = null!, Hotkey = null!, Category = null! },
            new() { Id = "A1", Name = "Same id" },
            new() { Id = @"..\..\settings", Name = "Unsafe id" },
            new() { Id = "", Name = "Blank id" }
        });

        var tools = _storage.LoadTools();

        Assert.Equal(4, tools.Count);
        Assert.Equal("a1", tools[0].Id);
        Assert.Equal("", tools[0].Name);
        Assert.Equal("", tools[0].TargetPath);
        Assert.Equal("", tools[0].Arguments);
        Assert.Equal("", tools[0].Hotkey);
        Assert.Equal(LibraryConstants.Uncategorized, tools[0].Category);
        Assert.Equal(4, tools.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(tools, t => Assert.True(t.Id.Length > 0 && t.Id.All(char.IsAsciiLetterOrDigit)));
    }
}
