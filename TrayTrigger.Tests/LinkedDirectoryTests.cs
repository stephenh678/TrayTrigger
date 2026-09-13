using System.Diagnostics;
using System.IO;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>Real junctions on disk, made the way the Xbox app makes them (mklink /J needs no admin).</summary>
public sealed class LinkedDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TrayTriggerLinkTests_" + Guid.NewGuid().ToString("N"));

    public LinkedDirectoryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void CreateJunction(string link, string target) => Cmd($"mklink /J \"{link}\" \"{target}\"");

    private static void Cmd(string command) =>
        Assert.True(TryCmd(command, out string output), $"'{command}' failed: {output}");

    private static bool TryCmd(string command, out string output)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c {command}")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        output = p.StandardOutput.ReadToEnd();
        output = p.StandardError.ReadToEnd() + output;
        p.WaitForExit();
        return p.ExitCode == 0;
    }

    [Fact]
    public void PlainFolder_IsFolder()
    {
        Assert.Equal(LinkedDirectoryState.Folder, LinkedDirectory.Resolve(_root, out string resolved));
        Assert.Equal(_root, resolved);
    }

    [Fact]
    public void JunctionToExistingFolder_ResolvesToTarget()
    {
        string target = Directory.CreateDirectory(Path.Combine(_root, "XboxGames", "Game", "Content")).FullName;
        string link = Path.Combine(_root, "Package_1.0.0.0_x64__hash");
        CreateJunction(link, target);

        Assert.Equal(LinkedDirectoryState.Linked, LinkedDirectory.Resolve(link, out string resolved));
        Assert.Equal(target, resolved, ignoreCase: true);
    }

    [Fact]
    public void JunctionWhoseTargetWasDeleted_IsBroken()
    {
        string target = Directory.CreateDirectory(Path.Combine(_root, "XboxGames", "Doom", "Content")).FullName;
        string link = Path.Combine(_root, "BethesdaSoftworks.ProjectTitan_1.2.26.0_x64__hash");
        CreateJunction(link, target);
        Directory.Delete(Path.Combine(_root, "XboxGames"), recursive: true);

        // The root cause: the link itself still "exists".
        Assert.True(Directory.Exists(link));
        Assert.Equal(LinkedDirectoryState.BrokenLink, LinkedDirectory.Resolve(link, out string resolved));
        Assert.Equal(link, resolved);
    }

    [Fact]
    public void JunctionToDisconnectedDrive_IsBroken()
    {
        // Directory.Exists alone isn't enough: an offline mapped drive or an empty DVD drive also reports false.
        string[] inUse = Environment.GetLogicalDrives();
        string? freeDrive = "QRSTUVWXYZ".Select(c => $"{c}:")
            .FirstOrDefault(d => !Directory.Exists(d + "\\") && !inUse.Any(u => u.StartsWith(d, StringComparison.OrdinalIgnoreCase)));
        if (freeDrive == null) return; // Every letter in use; nothing to map.

        // mklink refuses a target on a volume that doesn't exist, so make the drive with subst,
        // link into it, then take the drive away: the state an unplugged game drive leaves.
        string driveRoot = Directory.CreateDirectory(Path.Combine(_root, "drive")).FullName;
        Directory.CreateDirectory(Path.Combine(driveRoot, "XboxGames", "Game", "Content"));
        string link = Path.Combine(_root, "GunMedia.Game_1.0.7.0_x64__hash");

        if (!TryCmd($"subst {freeDrive} \"{driveRoot}\"", out _)) return; // The letter is taken after all (a mapping Windows doesn't list).
        try
        {
            CreateJunction(link, $"{freeDrive}\\XboxGames\\Game\\Content");
            Assert.Equal(LinkedDirectoryState.Linked, LinkedDirectory.Resolve(link, out _));
        }
        finally
        {
            Cmd($"subst {freeDrive} /d");
        }

        Assert.True(Directory.Exists(link));
        Assert.Equal(LinkedDirectoryState.BrokenLink, LinkedDirectory.Resolve(link, out _));
    }
}
