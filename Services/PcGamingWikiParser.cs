using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// Reads the structured templates on a PCGamingWiki page (Video, Input, API, Network/Multiplayer,
/// Save game cloud syncing and Game data) from the page's wikitext. These are the same
/// <c>name = value</c> lines the wiki's own tables are built from, with a small fixed vocabulary
/// (true, false, hackable, limited, always on, unknown, n/a), so they are far steadier to read than
/// the rendered page. Pure text in, facts out: nothing here touches the network or the disk.
///
/// <para>Only what a value plainly says is kept. A value that is unknown, not applicable or missing is
/// left out, a note that still has wiki markup in it after cleaning is dropped, and a page with none
/// of these templates (a stub, a redirect) gives no facts, so the card never shows a guess.</para>
/// </summary>
internal static class PcGamingWikiParser
{
    /// <summary>A real page is 20 to 100 KB; anything this large is not a game page.</summary>
    internal const int MaxWikitextLength = 2_000_000;

    private const int MaxTemplates = 4000;
    private const int MaxNoteLength = 240;
    private const int MaxDetailLength = 200;
    private const int MaxSavePaths = 2;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private static readonly Regex Comments = new(@"<!--.*?-->", RegexOptions.Singleline, RegexTimeout);
    private static readonly Regex Refs = new(@"<ref\b[^>]*?/>|<ref\b[^>]*>.*?</ref>", RegexOptions.Singleline | RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Regex LineBreaks = new(@"<br\s*/?>", RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Regex SimpleTags = new(@"</?(?:code|small|sup|sub|b|i|u|span|nowiki)\b[^>]*>", RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Regex WikiLinks = new(@"\[\[(?:[^\]|]*\|)?([^\]]*)\]\]", RegexOptions.None, RegexTimeout);
    private static readonly Regex UrlWithText = new(@"\[(?:https?:)?//\S+\s+([^\]]+)\]", RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Regex BareUrl = new(@"\[(?:https?:)?//\S+\]", RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Regex InnerTemplate = new(@"\{\{[^{}]*\}\}", RegexOptions.None, RegexTimeout);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.None, RegexTimeout);
    private static readonly Regex PathToken = new(@"\{\{\s*p\s*\|\s*([^{}|]+?)\s*\}\}", RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Regex VersionToken = new(@"^\d+(\.\d+)?[A-Za-z]{0,2}\+?$", RegexOptions.None, RegexTimeout);
    private static readonly Regex PlayerCount = new(@"^(\d{1,3})(?:\s*[-–]\s*(\d{1,3}))?$", RegexOptions.None, RegexTimeout);

    internal sealed record Template(string Name, List<string> Positional, Dictionary<string, string> Named);

    /// <summary>What was read from one page: its facts, and the Steam App IDs its infobox lists.</summary>
    internal sealed record PageReading(List<PcGamingWikiFact> Facts, List<string> SteamAppIds);

    /// <summary>Display, Controls, Audio and accessibility, Online, Saves and Technical facts, in that order. Empty when the page has none.</summary>
    public static List<PcGamingWikiFact> Parse(string? wikitext) => Read(wikitext).Facts;

    /// <summary>
    /// The facts, and the Steam App IDs the page's own infobox gives (<c>steam appid</c>). The IDs are how
    /// a page found by name is checked to be the game that was asked about, and how a page is picked out
    /// of a text search for an ID.
    /// </summary>
    internal static PageReading Read(string? wikitext)
    {
        var facts = new List<PcGamingWikiFact>();
        var ids = new List<string>();
        if (string.IsNullOrWhiteSpace(wikitext) || wikitext.Length > MaxWikitextLength)
            return new PageReading(facts, ids);

        try
        {
            string text = Refs.Replace(Comments.Replace(wikitext, string.Empty), string.Empty);
            var templates = TopLevelTemplates(text);

            AddVideo(facts, Find(templates, "Video"));
            AddInput(facts, Find(templates, "Input"));
            AddAudioAndAccessibility(facts, Find(templates, "Audio"), Find(templates, "Video"));
            AddNetwork(facts, Find(templates, "Network/Multiplayer"));
            AddSaves(facts, Find(templates, "Save game cloud syncing"), templates);
            AddGraphicsApi(facts, Find(templates, "API"));

            if (Find(templates, "Infobox game") is { } infobox)
            {
                // "steam appid side" holds the game's other Steam entries (an original beside its remaster, a bundle).
                foreach (string field in new[] { "steam appid", "steam appid side" })
                {
                    foreach (string token in Raw(infobox, field).Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (token.Length is > 0 and <= 10 && token.All(char.IsAsciiDigit) && !ids.Contains(token))
                            ids.Add(token);
                    }
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // A page the cleaning could not finish in two seconds is not worth showing half of.
            facts.Clear();
            ids.Clear();
        }

        return new PageReading(facts, ids);
    }

    // ------------------------------------------------------------------ the rows

    private static void AddVideo(List<PcGamingWikiFact> facts, Template? video)
    {
        if (video == null) return;
        AddRated(facts, "Display", "Ultrawide", video, "ultrawidescreen");
        AddRated(facts, "Display", "Multi-monitor", video, "multimonitor");
        AddRated(facts, "Display", "4K", video, "4k ultra hd");
        AddRated(facts, "Display", "HDR", video, "hdr");
        AddRated(facts, "Display", "120+ FPS", video, "120 fps");
        AddRated(facts, "Display", "Field of view setting", video, "fov");
        AddRated(facts, "Display", "Borderless windowed", video, "borderless windowed");
        AddRated(facts, "Display", "Ray tracing", video, "ray tracing");
        AddRated(facts, "Display", "Upscaling", video, "upscaling", detailKey: "upscaling tech", maxDetail: 60);
    }

    private static void AddInput(List<PcGamingWikiFact> facts, Template? input)
    {
        if (input == null) return;

        var support = ParseSupport(Raw(input, "controller support"));
        var full = ParseSupport(Raw(input, "full controller"));
        if (support != null || full == PcGamingWikiSupport.Yes)
        {
            var rating = support ?? PcGamingWikiSupport.Yes;
            string detail = string.Empty;
            if (rating == PcGamingWikiSupport.Yes)
            {
                if (full == PcGamingWikiSupport.Yes)
                {
                    detail = "full controller support";
                }
                else if (full is PcGamingWikiSupport.No or PcGamingWikiSupport.Limited)
                {
                    rating = PcGamingWikiSupport.Limited;
                    detail = "partial controller support";
                }
            }
            facts.Add(new PcGamingWikiFact
            {
                Group = "Controls",
                Label = "Controller support",
                Support = rating,
                Detail = detail,
                Note = CleanText(Raw(input, "controller support notes")),
            });
        }

        AddRated(facts, "Controls", "PlayStation controllers", input, "playstation controllers");
        AddRated(facts, "Controls", "Key remapping", input, "key remap");
    }

    private static void AddAudioAndAccessibility(List<PcGamingWikiFact> facts, Template? audio, Template? video)
    {
        const string group = "Audio and accessibility";
        if (audio != null)
        {
            AddRated(facts, group, "Subtitles", audio, "subtitles");
            AddRated(facts, group, "Closed captions", audio, "closed captions");
        }
        if (video != null)
            AddRated(facts, group, "Color blind mode", video, "color blind");
        if (audio != null)
            AddRated(facts, group, "Mute on focus lost", audio, "mute on focus lost");
    }

    private static void AddNetwork(List<PcGamingWikiFact> facts, Template? network)
    {
        if (network == null) return;
        AddRated(facts, "Online", "Local play", network, "local play", playersKey: "local play players");
        AddRated(facts, "Online", "LAN play", network, "lan play", playersKey: "lan play players");
        AddRated(facts, "Online", "Online play", network, "online play", playersKey: "online play players");
        AddRated(facts, "Online", "Crossplay", network, "crossplay");
    }

    private static void AddSaves(List<PcGamingWikiFact> facts, Template? cloud, List<Template> templates)
    {
        if (cloud != null)
            AddRated(facts, "Saves", "Steam Cloud", cloud, "steam cloud");

        string paths = SaveLocations(templates);
        if (paths.Length > 0)
        {
            facts.Add(new PcGamingWikiFact { Group = "Saves", Label = "Save location", Support = PcGamingWikiSupport.Info, Detail = paths });
        }
    }

    private static void AddGraphicsApi(List<PcGamingWikiFact> facts, Template? api)
    {
        if (api == null) return;

        var parts = new List<string>();
        string? d3d = VersionList(Raw(api, "direct3d versions"));
        if (d3d != null) parts.Add("DirectX " + d3d);

        string vulkanRaw = Raw(api, "vulkan versions");
        string? vulkan = VersionList(vulkanRaw);
        if (vulkan != null) parts.Add("Vulkan " + vulkan);
        else if (vulkanRaw.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)) parts.Add("Vulkan");

        string? opengl = VersionList(Raw(api, "opengl versions"));
        if (opengl != null) parts.Add("OpenGL " + opengl);

        if (parts.Count > 0)
        {
            facts.Add(new PcGamingWikiFact { Group = "Technical", Label = "Graphics API", Support = PcGamingWikiSupport.Info, Detail = string.Join(" · ", parts) });
        }
    }

    private static void AddRated(List<PcGamingWikiFact> facts, string group, string label, Template template, string key,
        string? detailKey = null, int maxDetail = MaxDetailLength, string? playersKey = null)
    {
        var support = ParseSupport(Raw(template, key));
        if (support == null) return;

        string detail = string.Empty;
        if (playersKey != null && support is PcGamingWikiSupport.Yes or PcGamingWikiSupport.Fix or PcGamingWikiSupport.Limited)
            detail = PlayersText(Raw(template, playersKey));
        else if (detailKey != null && support is PcGamingWikiSupport.Yes or PcGamingWikiSupport.Fix or PcGamingWikiSupport.Limited)
            detail = CleanText(Raw(template, detailKey), maxDetail);

        facts.Add(new PcGamingWikiFact
        {
            Group = group,
            Label = label,
            Support = support.Value,
            Detail = detail,
            Note = CleanText(Raw(template, key + " notes")),
        });
    }

    // ------------------------------------------------------------------ values

    /// <summary>The wiki's rating word as a rating; null for unknown, n/a, empty or anything else, which is left out.</summary>
    internal static PcGamingWikiSupport? ParseSupport(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "true" => PcGamingWikiSupport.Yes,
        "false" => PcGamingWikiSupport.No,
        "hackable" => PcGamingWikiSupport.Fix,
        "limited" => PcGamingWikiSupport.Limited,
        "always on" => PcGamingWikiSupport.AlwaysOn,
        _ => null,
    };

    /// <summary>"11, 12" or "9.0c" as a list of versions; null when it is empty or has anything else in it.</summary>
    internal static string? VersionList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var tokens = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || tokens.Length > 6) return null;
        return tokens.All(t => VersionToken.IsMatch(t)) ? string.Join(", ", tokens) : null;
    }

    /// <summary>"up to 4 players" or "2-4 players"; empty when the wiki gave no plain number.</summary>
    internal static string PlayersText(string? raw)
    {
        var match = PlayerCount.Match(raw?.Trim() ?? string.Empty);
        if (!match.Success) return string.Empty;
        return match.Groups[2].Success
            ? $"{match.Groups[1].Value}–{match.Groups[2].Value} players"
            : $"up to {match.Groups[1].Value} players";
    }

    /// <summary>
    /// A wiki note as plain text: links become their label, references, templates and simple tags
    /// are removed. If any markup is still left the note is dropped rather than shown half-cleaned.
    /// </summary>
    internal static string CleanText(string? value, int maxLength = MaxNoteLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        try
        {
            string s = Comments.Replace(value, string.Empty);
            s = Refs.Replace(s, string.Empty);
            s = LineBreaks.Replace(s, " ");
            s = SimpleTags.Replace(s, string.Empty);
            s = WikiLinks.Replace(s, "$1");
            s = UrlWithText.Replace(s, "$1");
            s = BareUrl.Replace(s, string.Empty);
            for (int pass = 0; pass < 6 && s.Contains("{{", StringComparison.Ordinal); pass++)
                s = InnerTemplate.Replace(s, string.Empty);
            s = s.Replace("'''", string.Empty).Replace("''", string.Empty);
            s = WebUtility.HtmlDecode(s);
            s = Whitespace.Replace(s, " ").Trim();

            if (s.IndexOfAny(['{', '}', '[', ']', '<', '>', '|']) >= 0) return string.Empty;
            if (s.Length <= maxLength) return s;

            int cut = s.LastIndexOf(' ', maxLength);
            return s[..(cut > maxLength / 2 ? cut : maxLength)].TrimEnd(' ', ',', ';', '.') + "…";
        }
        catch (RegexMatchTimeoutException)
        {
            // A note the cleaning cannot finish in two seconds is left out; the fact itself still shows.
            return string.Empty;
        }
    }

    // ------------------------------------------------------------------ save locations

    /// <summary>
    /// The Windows save folder or folders (at most two) with the wiki's path tokens spelled out the
    /// way Windows writes them (%APPDATA%, %USERPROFILE%\Documents), joined with " ; ". It is text to
    /// read: nothing is opened or checked on disk.
    /// </summary>
    private static string SaveLocations(List<Template> templates)
    {
        var paths = new List<string>();
        foreach (var gameData in templates.Where(t => Is(t, "Game data")))
        {
            foreach (string chunk in gameData.Positional)
            {
                foreach (var row in TopLevelTemplates(chunk))
                {
                    if (!Is(row, "Game data/saves") || row.Positional.Count < 2) continue;
                    if (!row.Positional[0].Equals("Windows", StringComparison.OrdinalIgnoreCase)) continue;

                    for (int i = 1; i < row.Positional.Count && paths.Count < MaxSavePaths; i++)
                    {
                        string? path = ExpandPath(row.Positional[i]);
                        if (path != null && !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                            paths.Add(path);
                    }
                }
            }
        }
        return string.Join(" ; ", paths);
    }

    /// <summary>One wiki path with its tokens expanded; null for a path that is empty, has markup left, a network address or is too long.</summary>
    internal static string? ExpandPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            // Tokens become private-use markers first, so any markup left over can be told from our own placeholders.
            var placeholders = new List<string>();
            string s = PathToken.Replace(raw.Trim(), m =>
            {
                placeholders.Add(TokenText(m.Groups[1].Value));
                return "\uE000" + (placeholders.Count - 1) + "\uE001";
            });

            if (s.IndexOfAny(['{', '}', '[', ']', '<', '>', '|']) >= 0) return null;
            if (s.StartsWith(@"\\", StringComparison.Ordinal) || s.StartsWith("//", StringComparison.Ordinal)) return null;
            if (s.Any(char.IsControl)) return null;

            for (int i = 0; i < placeholders.Count; i++)
                s = s.Replace("\uE000" + i + "\uE001", placeholders[i], StringComparison.Ordinal);

            s = s.Trim();
            return s.Length is > 0 and <= MaxDetailLength ? s : null;
        }
        catch (RegexMatchTimeoutException)
        {
            // A path the expanding cannot finish in two seconds is left out rather than shown half done.
            return null;
        }
    }

    private static string TokenText(string token)
    {
        string key = token.Trim().Replace('/', '\\').ToLowerInvariant();
        return key switch
        {
            "appdata" => "%APPDATA%",
            "localappdata" => "%LOCALAPPDATA%",
            "userprofile" => "%USERPROFILE%",
            @"userprofile\documents" => @"%USERPROFILE%\Documents",
            "programdata" => "%PROGRAMDATA%",
            "public" => "%PUBLIC%",
            "programfiles" => "%PROGRAMFILES%",
            "windir" => "%WINDIR%",
            "game" => "<game folder>",
            "steam" => "<Steam folder>",
            "uid" => "<user ID>",
            "hkcu" => "HKEY_CURRENT_USER",
            "hklm" => "HKEY_LOCAL_MACHINE",
            _ => "<" + new string(key.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '\\' or '_').ToArray()) + ">",
        };
    }

    // ------------------------------------------------------------------ the template reader

    private static bool Is(Template t, string name) => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase);

    private static Template? Find(List<Template> templates, string name) => templates.FirstOrDefault(t => Is(t, name));

    private static string Raw(Template t, string key) => t.Named.TryGetValue(key, out string? v) ? v : string.Empty;

    /// <summary>Every top-level <c>{{...}}</c> in the text, with its name and its parameters. Stops at the first unbalanced one.</summary>
    internal static List<Template> TopLevelTemplates(string text)
    {
        var result = new List<Template>();
        int i = 0, n = text.Length;
        while (i < n - 1 && result.Count < MaxTemplates)
        {
            if (text[i] != '{' || text[i + 1] != '{')
            {
                i++;
                continue;
            }

            int depth = 1, j = i + 2;
            while (j < n - 1 && depth > 0)
            {
                if (text[j] == '{' && text[j + 1] == '{') { depth++; j += 2; }
                else if (text[j] == '}' && text[j + 1] == '}') { depth--; j += 2; }
                else j++;
            }
            if (depth != 0) break;

            result.Add(ReadTemplate(text.Substring(i + 2, j - i - 4)));
            i = j;
        }
        return result;
    }

    private static Template ReadTemplate(string body)
    {
        var parts = SplitTopLevel(body);
        var positional = new List<string>();
        var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int k = 1; k < parts.Count; k++)
        {
            string part = parts[k];
            int eq = part.IndexOf('=');
            string key = eq > 0 ? part[..eq].Trim() : string.Empty;
            if (key.Length is > 0 and <= 60 && key.IndexOfAny(['{', '}', '[', ']', '<', '>', '|']) < 0)
                named.TryAdd(key, part[(eq + 1)..].Trim());
            else
                positional.Add(part.Trim());
        }
        return new Template(parts[0].Trim(), positional, named);
    }

    /// <summary>Splits on pipes that are not inside a <c>{{ }}</c> or <c>[[ ]]</c>.</summary>
    private static List<string> SplitTopLevel(string body)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < body.Length; i++)
        {
            if (i + 1 < body.Length)
            {
                string two = body.Substring(i, 2);
                if (two is "{{" or "[[") { depth++; i++; continue; }
                if (two is "}}" or "]]") { if (depth > 0) depth--; i++; continue; }
            }
            if (body[i] == '|' && depth == 0)
            {
                parts.Add(body[start..i]);
                start = i + 1;
            }
        }
        parts.Add(body[start..]);
        return parts;
    }
}
