using System.IO;
using System.Text.RegularExpressions;
using TrayTrigger.Services;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>
/// tools/Export-Wiki.ps1 generates the GitHub wiki from the same Help/ files and example scripts
/// the app embeds, and its section table is a copy of HelpContentService.SectionOrder. These
/// keep the copies from drifting apart.
/// </summary>
public class ExportWikiScriptTests
{
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
    public void SectionTable_MatchesTheAppsIndexOrderAndLabels()
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot, "tools", "Export-Wiki.ps1"));
        var rows = Regex.Matches(script, @"@\{\s*Key\s*=\s*'([^']+)';\s*Label\s*=\s*'([^']+)'\s*\}")
            .Select(m => (Key: m.Groups[1].Value, Label: m.Groups[2].Value))
            .ToList();

        Assert.Equal(HelpContentService.GetIndex().Select(g => g.Section), rows.Select(r => r.Key));
        Assert.All(rows, r => Assert.Equal(HelpContentService.SectionLabel(r.Key + "/"), r.Label));
    }

    [Fact]
    public void WikiWorkflow_TriggersOnEveryInputTheScriptReads()
    {
        string workflow = File.ReadAllText(Path.Combine(RepoRoot, ".github", "workflows", "wiki.yml"));
        Assert.Contains("'Help/**'", workflow);
        Assert.Contains("'tools/Export-Wiki.ps1'", workflow);
        Assert.Contains("'Scripts/Library/Example-*.ps1'", workflow);
    }
}
