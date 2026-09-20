using System.IO;
using System.Text.RegularExpressions;
using TrayTrigger.Services;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>
/// The logging rules in CONTRIBUTING.md that a machine can hold the code to.
///
/// <para>They exist because a tester's log kept being unable to answer the question it was sent
/// for - the Steam leg of a scan said nothing, the DLSS card searched a folder nobody could name -
/// and each time the fix was to add the logging afterwards and ship another beta to get the log.
/// A rule that depends on remembering is how that happened three times.</para>
/// </summary>
public class LoggingConventionTests
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

    /// <summary>
    /// The two helpers the rules lean on write what they claim to, and only with verbose logging
    /// on. Reads the shared test log rather than redirecting it: other tests are writing to it at
    /// the same moment, and each line looked for here carries its own unique marker.
    /// </summary>
    [Fact]
    public void ShownAndSwallowed_WriteOnlyWhenVerboseLoggingIsOn()
    {
        string marker = Guid.NewGuid().ToString("N");
        bool was = LoggingService.IsVerboseEnabled;
        try
        {
            LoggingService.IsVerboseEnabled = false;
            LoggingService.Shown("Status", $"off-{marker}");
            LoggingService.Swallowed("Test", new InvalidOperationException($"off-{marker}"));

            LoggingService.IsVerboseEnabled = true;
            LoggingService.Shown("Status", $"No new games found. {marker}\nsecond line");
            LoggingService.Shown("Status", "   ");
            LoggingService.Swallowed("Test", new InvalidOperationException($"boom-{marker}"), "reading the thing");
        }
        finally
        {
            LoggingService.IsVerboseEnabled = was;
        }

        string log;
        using (var stream = new FileStream(LoggingService.LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
            log = reader.ReadToEnd();

        Assert.DoesNotContain($"off-{marker}", log);
        // One line, however many the text had: a log is read line by line.
        Assert.Contains($"[VERB ] [UI] Status: No new games found. {marker} second line", log);
        Assert.Contains($"[VERB ] [Test] {nameof(ShownAndSwallowed_WriteOnlyWhenVerboseLoggingIsOn)}: reading the thing - ignored InvalidOperationException: boom-{marker}", log);
    }

    private static readonly string[] SkippedFolders = ["bin", "obj", ".git", "publish", "site", "tools", "TrayTrigger.Tests"];

    private static IEnumerable<string> SourceFiles()
    {
        string root = RepoRoot;
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar).Any(part => SkippedFolders.Contains(part)));
    }

    // A catch at the start of a statement, its optional "(Type name)" and "when (...)", then "{".
    // Lines that are comments are skipped, so prose about catching something is not code.
    private static readonly Regex CatchBlock = new(
        @"^(?![ \t]*//)[^\n]*?\bcatch\b\s*(\((?<decl>[^)]*)\))?\s*(when\s*\([^{]*\))?\s*\{",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// A catch block must do one of: log, rethrow, use the exception it caught, or say in a
    /// comment why saying nothing is right. An empty <c>catch { }</c> makes "it failed quietly"
    /// look exactly like "it never ran".
    /// </summary>
    [Fact]
    public void EveryCatchBlock_LogsRethrowsUsesTheExceptionOrExplainsItself()
    {
        var offenders = new List<string>();
        string root = RepoRoot;

        foreach (string file in SourceFiles())
        {
            string source = File.ReadAllText(file);
            foreach (Match m in CatchBlock.Matches(source))
            {
                // "catch" inside a string literal on that line, as in a generated PowerShell command.
                string before = m.Value[..m.Value.LastIndexOf("catch", StringComparison.Ordinal)];
                if (before.Count(c => c == '"') % 2 == 1) continue;

                int i = m.Index + m.Length, depth = 1;
                while (i < source.Length && depth > 0)
                {
                    if (source[i] == '{') depth++;
                    else if (source[i] == '}') depth--;
                    i++;
                }
                string body = source[(m.Index + m.Length)..(i - 1)];
                int eol = source.IndexOf('\n', i);
                string restOfLine = source[i..(eol < 0 ? source.Length : eol)];

                string[] decl = m.Groups["decl"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string? variable = decl.Length >= 2 ? decl[^1] : null;

                bool explained =
                    body.Contains("LoggingService.") || body.Contains("throw") ||
                    body.Contains("//") || body.Contains("/*") || restOfLine.Contains("//") ||
                    (variable != null && Regex.IsMatch(body, $@"\b{Regex.Escape(variable)}\b"));

                if (!explained)
                {
                    int line = source.Take(m.Index + before.Length).Count(c => c == '\n') + 1;
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{line}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "These catch blocks neither log, rethrow, use the exception, nor say why silence is right. " +
            "Use LoggingService.Swallowed(category, ex, \"what was being attempted\"), or add a comment " +
            "saying why nothing is said (see CONTRIBUTING.md, Logging):\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// One spelling per area. A tester's log is searched by tag, and "SteamScan" beside
    /// "SteamScannerService" beside "Steam" means three searches and a missed line.
    /// </summary>
    [Fact]
    public void LogCategories_AreLiteralAndSpelledOneWay()
    {
        var spellings = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var call = new Regex(@"LoggingService\.(Info|Warn|Error|Verbose|Swallowed)\(\s*""(?<cat>[^""]+)""", RegexOptions.Compiled);

        foreach (string file in SourceFiles())
        {
            foreach (Match m in call.Matches(File.ReadAllText(file)))
            {
                string category = m.Groups["cat"].Value;
                if (!spellings.TryGetValue(category, out var seen)) spellings[category] = seen = new HashSet<string>(StringComparer.Ordinal);
                seen.Add(category);
            }
        }

        var clashes = spellings.Values.Where(s => s.Count > 1).Select(s => string.Join(" / ", s.OrderBy(x => x))).ToList();
        Assert.True(clashes.Count == 0, "Log categories that differ only by case:\n  " + string.Join("\n  ", clashes));

        // The area, not the class: "SteamScanner", never "SteamScannerService" - that pair is how
        // one area ended up under two names.
        var suffixed = spellings.Keys.Where(c => c.EndsWith("Service", StringComparison.Ordinal) || c.EndsWith("ViewModel", StringComparison.Ordinal)).ToList();
        Assert.True(suffixed.Count == 0, "Log categories name the area, without a Service or ViewModel suffix:\n  " + string.Join("\n  ", suffixed));
    }
}
