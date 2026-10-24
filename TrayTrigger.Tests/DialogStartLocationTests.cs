using System;
using System.IO;
using Microsoft.Win32;
using TrayTrigger.Services;
using Xunit;

namespace TrayTrigger.Tests;

public sealed class DialogStartLocationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tt-dialog-" + Guid.NewGuid().ToString("N"));

    public DialogStartLocationTests() => Directory.CreateDirectory(Path.Combine(_root, "Games", "Doom"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Doom => Path.Combine(_root, "Games", "Doom");

    [Fact]
    public void AFile_OpensInItsFolder_AndAFolder_OpensItself()
    {
        string exe = Path.Combine(Doom, "doom.exe");
        File.WriteAllText(exe, "x");
        Assert.Equal(Doom, DialogStartLocation.ExistingFolder(exe));
        Assert.Equal(Doom, DialogStartLocation.ExistingFolder(Doom));
        Assert.Equal(Doom, DialogStartLocation.ExistingFolder("  \"" + Doom + "\"  "));
    }

    [Fact]
    public void AMissingPath_OpensAtTheNearestFolderAboveItThatExists()
    {
        string gone = Path.Combine(Doom, "Old Version", "bin", "doom.exe");
        Assert.Equal(Doom, DialogStartLocation.ExistingFolder(gone));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"relative\folder\game.exe")]
    [InlineData("steam://rungameid/570")]
    [InlineData(@"\\server\share\game.exe")]
    [InlineData("C:\\bad|name\\game.exe")]
    public void NotALocalRootedPath_GivesNothing(string? text)
    {
        Assert.Null(DialogStartLocation.ExistingFolder(text));
    }

    [Fact]
    public void FallbacksAreTriedInOrder_WhenTheBoxHasNothingUsable()
    {
        Assert.Equal(Doom, DialogStartLocation.FirstExisting("steam://x", "", Doom, _root));
        Assert.Null(DialogStartLocation.FirstExisting("", null, "relative"));
    }

    [Fact]
    public void ForFile_SetsTheStartFolder_AndSelectsAnExistingFile()
    {
        string exe = Path.Combine(Doom, "doom.exe");
        File.WriteAllText(exe, "x");
        var dialog = new OpenFileDialog();
        DialogStartLocation.ForFile(dialog, exe);
        Assert.Equal(Doom, dialog.InitialDirectory);
        Assert.Equal("doom.exe", dialog.FileName);

        var missing = new OpenFileDialog();
        DialogStartLocation.ForFile(missing, Path.Combine(Doom, "gone.exe"));
        Assert.Equal(Doom, missing.InitialDirectory);
        Assert.Equal("", missing.FileName);
    }

    [Fact]
    public void ForFolder_UsesTheFolderInTheBox_OrTheFallback()
    {
        var dialog = new OpenFolderDialog();
        DialogStartLocation.ForFolder(dialog, "", Path.Combine(Doom, "doom.exe"));
        Assert.Equal(Doom, dialog.InitialDirectory);
    }
}
