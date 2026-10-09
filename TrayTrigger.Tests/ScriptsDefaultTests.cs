using System.IO;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>
/// Game scripts are on for a new install (1.6.1), so Edit Game shows its Scripts tab from the
/// start; settings saved by an earlier version keep the value they had.
/// </summary>
public class ScriptsDefaultTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private StorageService Storage() => new(Path.Combine(_root, "roaming"), Path.Combine(_root, "local"));

    [Fact]
    public void ANewInstall_HasScriptsOn()
    {
        Assert.True(Storage().LoadSettings().EnableGameScripts);
    }

    [Fact]
    public void AnUpgrade_KeepsScriptsOff_WhenTheyWereOff()
    {
        var storage = Storage();
        var settings = storage.LoadSettings();
        settings.EnableGameScripts = false;
        storage.SaveSettings(settings);

        Assert.False(Storage().LoadSettings().EnableGameScripts);
    }
}
