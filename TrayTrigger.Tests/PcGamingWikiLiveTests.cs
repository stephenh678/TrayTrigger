using System.IO;
using System.Text;
using TrayTrigger.Models;
using TrayTrigger.Services;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>
/// The real service against the real wiki, for a handful of games. It only runs when PCGW_LIVE=1, so the
/// normal test run never goes to the network; PCGW_LIVE_REPORT names a file for what it found. It exists
/// to prove that the app's own HTTP client is let through (not just curl) and that the whole path, from
/// a Steam App ID or a name to a list of facts, works on pages nobody wrote a fixture for.
/// </summary>
public class PcGamingWikiLiveTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tt-pcgw-live-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { /* a test folder left in %TEMP% is not worth failing a test over */ }
    }

    // (Steam App ID, name as TrayTrigger might hold it, whether the wiki is expected to have facts)
    private static readonly (string? AppId, string Name, bool ExpectFacts)[] Games =
    [
        ("1245620", "ELDEN RING", true),
        ("730", "Counter-Strike 2", true),
        ("2357570", "Overwatch 2", true),
        ("1086940", "Baldur's Gate 3", true),
        ("322330", "Don't Starve Together", true),
        ("1817070", "Marvel's Spider-Man Remastered", true),
        ("413150", "Stardew Valley", true),
        ("1091500", "Cyberpunk 2077", true),
        (null, "Baldur's Gate 3", true),
        (null, "Don't Starve Together", true),
        (null, "Disco Elysium", true),
        (null, "Cities: Skylines", true),
        (null, "Return to Monkey Island", true),
        (null, "Hades™", true),
        (null, "Minecraft", true),
        (null, "Zzyzx Not A Real Game", false),
        ("99999999", "Zzyzx Not A Real Game", false),
    ];

    [Fact]
    public async Task TheRealWiki_AnswersTheAppsOwnClient()
    {
        if (Environment.GetEnvironmentVariable("PCGW_LIVE") != "1")
            return;

        var service = new PcGamingWikiService(null, Path.Combine(_folder, "cache.json"), null, null);
        var report = new StringBuilder();
        var problems = new List<string>();

        // PCGW_LIVE_LIST names a file of "app id|name|1 or 0" lines (an empty id means a game with none; 0 means no facts expected).
        var games = Games.ToList();
        string? listPath = Environment.GetEnvironmentVariable("PCGW_LIVE_LIST");
        if (!string.IsNullOrWhiteSpace(listPath) && File.Exists(listPath))
        {
            games = File.ReadAllLines(listPath)
                .Select(l => l.Split('|'))
                .Where(p => p.Length == 3)
                .Select(p => ((string?)(p[0].Length > 0 ? p[0] : null), p[1], p[2] == "1"))
                .ToList();
        }

        foreach (var (appId, name, expectFacts) in games)
        {
            var result = await service.GetAsync(appId, name);
            string line = result.Status == PcGamingWikiStatus.Found
                ? $"{name} [{appId ?? "name"}] -> {result.Facts!.PageTitle}: {result.Facts.Facts.Count} facts"
                : $"{name} [{appId ?? "name"}] -> {result.Status}";
            report.AppendLine(line);

            if (expectFacts && result.Status != PcGamingWikiStatus.Found) problems.Add(line);
            if (!expectFacts && result.Status == PcGamingWikiStatus.Found) problems.Add(line + " (expected no facts)");
            if (result.Status == PcGamingWikiStatus.Failed) problems.Add(line);
        }

        string? path = Environment.GetEnvironmentVariable("PCGW_LIVE_REPORT");
        if (!string.IsNullOrWhiteSpace(path))
            File.WriteAllText(path, report.ToString());

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
