using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrayTrigger.Models;

/// <summary>
/// How long cached game info (Steam and RAWG details) is trusted before the Game Details window
/// re-fetches it in the background. The window always paints from the disk cache first; this
/// only decides whether a fresh copy is requested behind it. Manual actions (Refresh now,
/// Refresh metadata, Change match) always bypass it.
/// </summary>
public enum MetadataRefreshInterval
{
    /// <summary>Re-fetch every time the details window opens (the pre-1.3.9 Steam behaviour).</summary>
    EveryOpen,
    Daily,
    /// <summary>The default: news and ratings stay current enough, at a third of the requests.</summary>
    Every3Days,
    Weekly,
    Monthly,
    /// <summary>Never re-fetch on its own; only the manual refresh actions update the cache.</summary>
    Never,
}

/// <summary>
/// Persists <see cref="MetadataRefreshInterval"/> by name ("Every3Days"), as JsonStringEnumConverter
/// did, but reads leniently: a name it doesn't know (a settings.json written by a newer build that
/// added an interval, or a hand edit), a number, or null falls back to <see cref="Fallback"/>
/// instead of throwing. A throw here would make StorageService treat the whole settings file as
/// corrupt - archived and replaced with defaults, API keys and hotkey included - for one setting.
/// Every other enum is stored as a number, which the serializer already accepts when undefined.
/// </summary>
public sealed class MetadataRefreshIntervalJsonConverter : JsonConverter<MetadataRefreshInterval>
{
    public const MetadataRefreshInterval Fallback = MetadataRefreshInterval.Every3Days;

    public override bool HandleNull => true;

    public override MetadataRefreshInterval Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return Enum.TryParse(reader.GetString(), ignoreCase: true, out MetadataRefreshInterval named) && Enum.IsDefined(named)
                    ? named
                    : Fallback;
            case JsonTokenType.Number:
                return reader.TryGetInt32(out int number) && Enum.IsDefined((MetadataRefreshInterval)number)
                    ? (MetadataRefreshInterval)number
                    : Fallback;
            default:
                return Fallback;
        }
    }

    public override void Write(Utf8JsonWriter writer, MetadataRefreshInterval value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
