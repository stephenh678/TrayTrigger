using System.IO;
using System.Text;
using System.Text.Json;
using TrayTrigger.Services;
using Xunit;

namespace TrayTrigger.Tests;

/// <summary>
/// A coverage check over a folder of saved PCGamingWiki answers (each file the JSON of one
/// <c>action=parse&amp;prop=wikitext</c> call). It only runs when PCGW_SURVEY_DIR names such a folder, so
/// the normal test run never touches it; PCGW_SURVEY_REPORT names the file the report is written to.
/// It is how "which facts does the wiki give us for nearly every game" was measured.
/// </summary>
public class PcGamingWikiSurveyTests
{
    [Fact]
    public void EverySurveyedPageWithFacts_GivesTheCoreFacts()
    {
        string? dir = Environment.GetEnvironmentVariable("PCGW_SURVEY_DIR");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return;

        var perLabel = new SortedDictionary<string, int>();
        var perLabelValues = new SortedDictionary<string, SortedDictionary<string, int>>();
        var short_ = new List<string>();
        int pages = 0, withFacts = 0, noWikitext = 0, redirects = 0;

        foreach (string file in Directory.GetFiles(dir, "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            string name = doc.RootElement.GetProperty("name").GetString() ?? file;
            if (!doc.RootElement.TryGetProperty("response", out var response)
                || response.ValueKind != JsonValueKind.Object
                || !response.TryGetProperty("parse", out var parse)
                || !parse.TryGetProperty("wikitext", out var wt))
            {
                noWikitext++;
                continue;
            }

            pages++;
            string wikitext = wt.GetString() ?? string.Empty;
            if (wikitext.TrimStart().StartsWith("#REDIRECT", StringComparison.OrdinalIgnoreCase))
            {
                redirects++;
                continue;
            }

            var facts = PcGamingWikiParser.Parse(wikitext);
            if (facts.Count == 0) { short_.Add($"{name}: no facts"); continue; }
            withFacts++;
            if (facts.Count < 8) short_.Add($"{name}: only {facts.Count} facts");

            foreach (var fact in facts)
            {
                perLabel[fact.Label] = perLabel.GetValueOrDefault(fact.Label) + 1;
                if (!perLabelValues.TryGetValue(fact.Label, out var values))
                    perLabelValues[fact.Label] = values = new SortedDictionary<string, int>();
                string v = fact.Support.ToString();
                values[v] = values.GetValueOrDefault(v) + 1;
            }
        }

        var report = new StringBuilder();
        report.AppendLine($"pages {pages}, redirect pages {redirects}, pages with facts {withFacts}, answers with no wikitext {noWikitext}");
        report.AppendLine();
        foreach (var (label, count) in perLabel)
            report.AppendLine($"{label,-26} {count,3}/{withFacts} ({100.0 * count / Math.Max(1, withFacts):0}%)  " + string.Join(", ", perLabelValues[label].Select(kv => $"{kv.Key}:{kv.Value}")));
        report.AppendLine();
        report.AppendLine("Pages with few or no facts:");
        foreach (string line in short_) report.AppendLine("  " + line);

        string? reportPath = Environment.GetEnvironmentVariable("PCGW_SURVEY_REPORT");
        if (!string.IsNullOrWhiteSpace(reportPath))
            File.WriteAllText(reportPath, report.ToString());

        Assert.True(withFacts > 0, "no survey page produced any facts");
        Assert.DoesNotContain(short_, s => s.EndsWith("no facts", StringComparison.Ordinal));
    }
}
