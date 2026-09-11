using System;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>
/// The staleness rule shared by the Steam and RAWG detail caches, plus the "Updated 3 hours ago"
/// wording the details window shows beside its Refresh link.
/// </summary>
public static class MetadataFreshness
{
    /// <summary>The trust window for an interval; null for <see cref="MetadataRefreshInterval.Never"/>.</summary>
    public static TimeSpan? MaxAge(MetadataRefreshInterval interval) => interval switch
    {
        MetadataRefreshInterval.EveryOpen => TimeSpan.Zero,
        MetadataRefreshInterval.Daily => TimeSpan.FromDays(1),
        MetadataRefreshInterval.Every3Days => TimeSpan.FromDays(3),
        MetadataRefreshInterval.Weekly => TimeSpan.FromDays(7),
        MetadataRefreshInterval.Monthly => TimeSpan.FromDays(30),
        _ => null,
    };

    /// <summary>
    /// True when an entry fetched at <paramref name="fetchedUtc"/> should be re-fetched under
    /// <paramref name="interval"/>. An entry with no timestamp (written before 1.3.9-beta.3)
    /// counts as stale so it picks one up on its next open, unless the interval is Never.
    /// </summary>
    public static bool IsStale(DateTime fetchedUtc, MetadataRefreshInterval interval, DateTime? nowUtc = null)
    {
        var maxAge = MaxAge(interval);
        if (maxAge == null)
            return false;
        if (fetchedUtc == default)
            return true;

        var now = nowUtc ?? DateTime.UtcNow;
        return now - fetchedUtc >= maxAge.Value;
    }

    /// <summary>"Updated just now" / "Updated 3 hours ago" / "Updated 2 weeks ago" - coarse on
    /// purpose, the way a chat client dates messages. Empty when there is no timestamp.</summary>
    public static string Describe(DateTime fetchedUtc, DateTime? nowUtc = null)
    {
        if (fetchedUtc == default)
            return string.Empty;

        var now = nowUtc ?? DateTime.UtcNow;
        var age = now - fetchedUtc;
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;

        string when;
        if (age < TimeSpan.FromMinutes(1))
            when = "just now";
        else if (age < TimeSpan.FromHours(1))
            when = Plural((int)age.TotalMinutes, "minute");
        else if (age < TimeSpan.FromDays(1))
            when = Plural((int)age.TotalHours, "hour");
        else if (age < TimeSpan.FromDays(2))
            when = "yesterday";
        else if (age < TimeSpan.FromDays(14))
            when = Plural((int)age.TotalDays, "day");
        else if (age < TimeSpan.FromDays(60))
            when = Plural((int)(age.TotalDays / 7), "week");
        else if (age < TimeSpan.FromDays(365))
            when = Plural((int)(age.TotalDays / 30), "month");
        else
            when = Plural((int)(age.TotalDays / 365), "year");

        return "Updated " + when;
    }

    private static string Plural(int n, string unit) => n == 1 ? $"1 {unit} ago" : $"{n} {unit}s ago";
}
