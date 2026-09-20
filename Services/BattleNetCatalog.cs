using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TrayTrigger.Services;

/// <summary>
/// Reads Battle.net's own product catalog - the JSON files the client caches under
/// %LOCALAPPDATA%\Battle.net\Cache - to find the launch code ("program ID") for an install uid.
/// <c>Battle.net.exe --exec="launch &lt;X&gt;"</c> only accepts the program ID, and it can't be
/// derived from the uid (Hearthstone's uid is hs_beta, its program ID WTCG), so TrayTrigger keeps
/// no table of its own: each catalog file holds <c>products[]</c>, each product's <c>id</c> is a
/// program ID, and every install uid that product owns appears as a nested <c>"uid"</c> value.
///
/// The same product can appear in several cache files, and some copies list no uids at all, so
/// every file is merged before a uid is resolved. A uid claimed by two different program IDs is
/// treated as unresolved rather than guessed. See docs/adding-a-platform-integration.md
/// section 3.1 for the tests behind this.
/// </summary>
public static partial class BattleNetCatalog
{
    /// <summary>Cheap pre-check before parsing: the cache folder also holds images, strings and other JSON.</summary>
    public static bool LooksLikeCatalog(string text)
        => text.Contains("\"fragment_id\"", StringComparison.Ordinal) && text.Contains("\"products\"", StringComparison.Ordinal);

    /// <summary>A program ID TrayTrigger is willing to put on a command line.</summary>
    public static bool IsValidProgramId(string? programId)
        => !string.IsNullOrWhiteSpace(programId) && ProgramIdRegex().IsMatch(programId);

    /// <summary>
    /// Adds every (uid -> program ID) pair in one catalog file to <paramref name="owners"/>.
    /// Returns false when the text isn't a readable catalog; a malformed file never throws.
    /// </summary>
    public static bool MergeInto(Dictionary<string, HashSet<string>> owners, string json)
    {
        if (string.IsNullOrWhiteSpace(json) || !LooksLikeCatalog(json)) return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("products", out var products) ||
                products.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var product in products.EnumerateArray())
            {
                if (product.ValueKind != JsonValueKind.Object) continue;
                if (!product.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String) continue;

                string? programId = idElement.GetString();
                if (!IsValidProgramId(programId)) continue;

                foreach (var uid in CollectUids(product))
                {
                    if (!owners.TryGetValue(uid, out var ids))
                    {
                        ids = new HashSet<string>(StringComparer.Ordinal);
                        owners[uid] = ids;
                    }
                    ids.Add(programId!);
                }
            }
            return true;
        }
        catch (JsonException ex)
        {
            LoggingService.Swallowed("BattleNetCatalog", ex, "parsing the saved catalog");
            return false;
        }
    }

    /// <summary>A fresh uid -> owners map, keyed case-insensitively (the registry and the catalog agree on case today, but nothing guarantees it).</summary>
    public static Dictionary<string, HashSet<string>> NewOwnerMap() => new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The program ID for <paramref name="uid"/>, or null when no product owns it or more than one
    /// does. Program IDs keep their case: the client rejects "btlr" and accepts "BTLR".
    /// </summary>
    public static string? Resolve(IReadOnlyDictionary<string, HashSet<string>> owners, string uid, out bool ambiguous)
    {
        ambiguous = false;
        if (string.IsNullOrWhiteSpace(uid) || !owners.TryGetValue(uid, out var ids) || ids.Count == 0) return null;
        if (ids.Count > 1)
        {
            ambiguous = true;
            return null;
        }
        return ids.First();
    }

    /// <summary>Every uid with exactly one owning product - what gets written to the saved code map.</summary>
    public static Dictionary<string, string> Unambiguous(IReadOnlyDictionary<string, HashSet<string>> owners)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (uid, ids) in owners)
        {
            if (ids.Count == 1) result[uid] = ids.First();
        }
        return result;
    }

    private static IEnumerable<string> CollectUids(JsonElement product)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<JsonElement>();
        stack.Push(product);
        while (stack.Count > 0)
        {
            var element = stack.Pop();
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.String)
                        {
                            if (property.NameEquals("uid") && property.Value.GetString() is { Length: > 0 } uid)
                                found.Add(uid);
                        }
                        else
                        {
                            stack.Push(property.Value);
                        }
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray()) stack.Push(item);
                    break;
            }
        }
        return found;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_\-]{1,32}$")]
    private static partial Regex ProgramIdRegex();
}
