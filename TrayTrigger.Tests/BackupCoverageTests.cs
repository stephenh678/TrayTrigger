using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TrayTrigger.Services;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>
/// Every data file TrayTrigger writes is either in a backup or listed here as staying behind, with the
/// reason. A new file then has to be decided on when it is added, not found missing after a restore.
/// </summary>
public class BackupCoverageTests
{
    /// <summary>Files that are not in a backup on purpose, and why.</summary>
    private static readonly Dictionary<string, string> ThisPcOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        ["backup.json"] = "the manifest inside a backup, not a file TrayTrigger keeps",
        ["activity.json"] = "an option when backing up (BackupService.Create includeActivity), not part of every backup",
        ["profile-session.json"] = "what a running game's profile changed on this PC, so it can be put back",
        ["pending-removal.json"] = "a removal still inside its undo window",
        ["suspended-games.json"] = "games frozen on this PC right now",
        ["steam-cache.json"] = "Steam details; fetched again",
        ["rawg-cache.json"] = "RAWG details; fetched again",
        ["poster-sources.json"] = "where each cached poster came from; a poster without a record is looked at once more",
        ["nvidia-products.xml"] = "NVIDIA's own product list, downloaded for this PC's driver",
        ["dlss-roundtrip-records.json"] = "what DLSS Override changed in this PC's driver",
        ["display-session.json"] = "the display mode a game set on this PC, so it can be put back after a crash",
        ["audio-input-session.json"] = "the microphone a game set on this PC, so it can be put back after a crash",
        ["audio-session.json"] = "the sound output a game set on this PC, so it can be put back after a crash",
        ["installerdata.xml"] = "Ubisoft Connect's own file, which TrayTrigger only reads",
    };

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "TrayTrigger.csproj")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("TrayTrigger.csproj not found above " + AppContext.BaseDirectory);
        }
    }

    [Fact]
    public void EveryDataFileInTheCode_IsInTheBackupOrSaysWhyNot()
    {
        var named = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var literal = new Regex("\"([A-Za-z0-9][A-Za-z0-9._-]*\\.(?:json|xml))\"");
        foreach (var folder in new[] { "Services", "ViewModels", "Models" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot, folder), "*.cs", SearchOption.AllDirectories))
            {
                foreach (Match m in literal.Matches(File.ReadAllText(file)))
                {
                    string name = m.Groups[1].Value;
                    // A theme's own file; the backup takes the whole Themes folder's .theme.json files.
                    if (name.EndsWith(".theme.json", StringComparison.OrdinalIgnoreCase)) continue;
                    named.Add(name);
                }
            }
        }

        var undecided = named
            .Where(n => !BackupService.DataFiles.Contains(n, StringComparer.OrdinalIgnoreCase) && !ThisPcOnly.ContainsKey(n))
            .ToList();

        Assert.True(undecided.Count == 0,
            "These files are named in TrayTrigger's code but are neither in BackupService.DataFiles nor listed in "
            + "BackupCoverageTests.ThisPcOnly: " + string.Join(", ", undecided));
    }

    [Fact]
    public void NothingIsBothBackedUpAndLeftOut()
    {
        var both = BackupService.DataFiles.Where(ThisPcOnly.ContainsKey).ToList();
        Assert.Empty(both);
    }
}
