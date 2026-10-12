using System.IO;
using System.Text.RegularExpressions;

namespace TrayTrigger.Tests;

/// <summary>
/// The repository's own source files, for the guard tests that keep colors, text sizes and corner radii
/// in their one file each (ColorGuardTests, TypographyGuardTests, ShapeGuardTests).
/// </summary>
internal static class SourceTree
{
    internal static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "TrayTrigger.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("TrayTrigger.csproj not found above the test folder.");
    }

    /// <summary>
    /// Every file matching <paramref name="pattern"/> in the app, as repo-relative paths with forward
    /// slashes. Build output, the tests, the build-time tools and the dev diagnostics are not the app's UI.
    /// </summary>
    internal static IEnumerable<string> Sources(string pattern) =>
        Directory.EnumerateFiles(Root, pattern, SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Root, f).Replace('\\', '/'))
            .Where(f => !f.StartsWith("bin/") && !f.StartsWith("obj/") && !f.StartsWith("publish/")
                     && !f.StartsWith("TrayTrigger.Tests/") && !f.StartsWith("tools/") && !f.StartsWith("Diagnostics/"));

    internal static string Read(string relativePath) => File.ReadAllText(Path.Combine(Root, relativePath));

    /// <summary>Lines in <paramref name="files"/> that match <paramref name="pattern"/>, as "path:line  text". Comment lines are skipped.</summary>
    internal static List<string> Offenders(IEnumerable<string> files, Regex pattern) =>
        files
            .SelectMany(f => File.ReadAllLines(Path.Combine(Root, f)).Select((line, i) => (f, n: i + 1, line)))
            .Where(x => pattern.IsMatch(x.line) && !x.line.TrimStart().StartsWith("//"))
            .Select(x => $"{x.f}:{x.n}  {x.line.Trim()}")
            .ToList();
}
