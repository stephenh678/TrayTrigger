using System;
using System.Collections.Generic;

namespace TrayTrigger.Models;

/// <summary>
/// How much an Activity &amp; History entry matters. See <see cref="Services.ActivityService"/>.
/// The order is the order of importance: anything at <see cref="Problem"/> or above lights the
/// sidebar bell's dot until the page has been opened, and is toasted after the last game exits.
/// </summary>
public enum ActivityLevel
{
    /// <summary>Something TrayTrigger did on its own, in the background, that changes what you see.</summary>
    Activity = 0,
    /// <summary>Something TrayTrigger changed and put back: the record that makes "puts back what it
    /// changed" checkable.</summary>
    Change = 1,
    /// <summary>Something didn't do what you asked.</summary>
    Problem = 2,
    /// <summary>Your PC was left changed.</summary>
    Critical = 3,
}

/// <summary>One line in Activity &amp; History. Plain language, written for players, not for the log.</summary>
public class ActivityEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime TimeUtc { get; set; } = DateTime.UtcNow;
    public ActivityLevel Level { get; set; }
    /// <summary>The line itself, e.g. "Elden Ring's post-exit script failed (exit code 1)".</summary>
    public string Text { get; set; } = string.Empty;
    /// <summary>Optional second line, shown when the entry is expanded and copied with it.</summary>
    public string? Detail { get; set; }
    /// <summary>The game or tool it is about, for search. Blank when it is about TrayTrigger itself.</summary>
    public string? Subject { get; set; }
    /// <summary>
    /// Entries with the same key are one row on the page, with a count ("7 TIMES") and every time
    /// it happened. Defaults to the level and text, so the same failure for the same game groups.
    /// </summary>
    public string GroupKey { get; set; } = string.Empty;
    /// <summary>When a Critical entry was put right (Restore Previous); null until then. Shown as FIXED.</summary>
    public DateTime? FixedUtc { get; set; }
}

/// <summary>activity.json: the history, and when the page was last opened.</summary>
public class ActivityLogFile
{
    /// <summary>When Activity &amp; History was last opened. A problem recorded after this lights the bell.</summary>
    public DateTime LastViewedUtc { get; set; }
    public List<ActivityEntry> Entries { get; set; } = new();
}
