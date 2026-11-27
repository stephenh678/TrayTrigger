using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using TrayTrigger.Models;

namespace TrayTrigger.Services;

/// <summary>What a requirement line is about, from its Steam label.</summary>
public enum RequirementKind { Other, Os, Processor, Memory, Graphics, DirectX, Storage, Sound }

/// <summary>One row of Game Details' requirements card: Steam's label and text, this PC's part, and a
/// warning only when the PC is sure to fall short.</summary>
public sealed record RequirementRow(string Label, RequirementKind Kind, string Value)
{
    public string Yours { get; init; } = "";
    public string Warning { get; init; } = "";
    public bool HasYours => Yours.Length > 0;
    public bool HasWarning => Warning.Length > 0;
}

/// <summary>A requirements list split into labelled rows and the lines with no label (and Additional Notes).</summary>
public sealed record ParsedRequirements(IReadOnlyList<RequirementRow> Rows, IReadOnlyList<string> Notes)
{
    /// <summary>False when no line had a label the card knows: the card then shows Steam's text as it is.</summary>
    public bool HasRows => Rows.Any(r => r.Kind != RequirementKind.Other);
}

/// <summary>
/// Game Details' PC system requirements: Steam's text as rows, this PC's part under each, and a warning
/// where the PC is sure to fall short. Warn, never approve: no row ever says it meets a requirement, and
/// the processor and graphics card are never judged, only shown - telling whether one chip is faster than
/// another needs a benchmark table that is often wrong. The sure checks are the numbers a line states:
/// memory, graphics memory, DirectX level, free space for a game not installed, and Windows 11 when the
/// line asks for nothing older. A line without a clear number gets no warning.
/// </summary>
public static partial class SystemRequirements
{
    [GeneratedRegex(@"^(?<label>[A-Za-z][A-Za-z .&/-]{0,28}?)\s*\*?\s*:\s*(?<value>.+)$")]
    private static partial Regex LabelledLine();

    [GeneratedRegex(@"(?<n>\d+(?:\.\d+)?)\s*(?<unit>TB|GB|MB)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SizeAmount();

    [GeneratedRegex(@"\b(?<n>9|1[0-2])(?:\.\d+)?[a-z]?\b", RegexOptions.IgnoreCase)]
    private static partial Regex DirectXNumber();

    [GeneratedRegex(@"\bWin(?:dows)?\s*11\b", RegexOptions.IgnoreCase)]
    private static partial Regex Windows11();

    [GeneratedRegex(@"\bWin(?:dows)?\s*(?:10|8(?:\.1)?|7|XP|Vista)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OlderWindows();

    [GeneratedRegex(@"^(Minimum|Recommended)\s*:?$", RegexOptions.IgnoreCase)]
    private static partial Regex ListHeading();

    /// <summary>Steam's cleaned text ("• OS: Windows 10\n• Memory: 8 GB RAM\n...") as rows and notes.</summary>
    public static ParsedRequirements Parse(string? text)
    {
        var rows = new List<RequirementRow>();
        var notes = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return new(rows, notes);

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim().TrimStart('•', '-', '·').Trim();
            if (line.Length == 0 || ListHeading().IsMatch(line)) continue;

            var match = LabelledLine().Match(line);
            if (!match.Success)
            {
                notes.Add(line);
                continue;
            }
            string label = match.Groups["label"].Value.Trim().TrimEnd('*').Trim();
            string value = match.Groups["value"].Value.Trim();
            var (kind, display, isNote) = Classify(label);
            if (isNote) notes.Add(value);
            else rows.Add(new RequirementRow(display, kind, value));
        }
        return new(rows, notes);
    }

    private static (RequirementKind Kind, string Display, bool IsNote) Classify(string label) => label.ToLowerInvariant() switch
    {
        "os" or "operating system" or "os version" => (RequirementKind.Os, "OS", false),
        "processor" or "cpu" => (RequirementKind.Processor, "Processor", false),
        "memory" or "ram" or "system memory" => (RequirementKind.Memory, "Memory", false),
        "graphics" or "graphics card" or "video card" or "video" or "gpu" => (RequirementKind.Graphics, "Graphics", false),
        "directx" or "direct x" => (RequirementKind.DirectX, "DirectX", false),
        "storage" or "hard drive" or "hard disk" or "hard disk space" or "disk space" or "hdd" => (RequirementKind.Storage, "Storage", false),
        "sound card" or "sound" or "audio" => (RequirementKind.Sound, "Sound card", false),
        "additional notes" or "notes" or "additional" or "other" => (RequirementKind.Other, label, true),
        _ => (RequirementKind.Other, SentenceCase(label), false),
    };

    /// <summary>"Sound Card" reads "Sound card"; a word in capitals ("VR Support") keeps them.</summary>
    private static string SentenceCase(string label)
    {
        string[] words = label.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i < words.Length; i++)
            if (words[i].Skip(1).All(char.IsLower)) words[i] = words[i].ToLowerInvariant();
        return string.Join(' ', words);
    }

    /// <summary>The game's install, as far as the storage row needs it.</summary>
    /// <param name="InstalledOn">The drive the game is installed on ("C:"), or null.</param>
    /// <param name="MayBeInstalled">True when the game could be installed where TrayTrigger can't see a
    /// folder (an Xbox game), so a missing folder doesn't mean it still needs the space.</param>
    public readonly record struct InstallState(string? InstalledOn, bool MayBeInstalled);

    /// <summary>Adds this PC's part and any sure shortfall to each row. Rows stay as they are without a PC summary.</summary>
    public static IReadOnlyList<RequirementRow> Compare(IReadOnlyList<RequirementRow> rows, PcSummary? pc, InstallState install)
    {
        if (pc == null) return rows;
        return rows.Select(row => row.Kind switch
        {
            RequirementKind.Os => row with { Yours = pc.Os, Warning = OsWarning(row.Value, pc) },
            RequirementKind.Processor => row with { Yours = Join(pc.Cpu, pc.CpuCores > 0 ? $"{pc.CpuCores} cores" : "") },
            RequirementKind.Memory => row with
            {
                Yours = pc.InstalledRamGb > 0 ? FormatGb(pc.InstalledRamGb) : "",
                Warning = Short(SmallestGb(row.Value), pc.InstalledRamGb) is { } need ? $"Below: needs {FormatGb(need)}" : "",
            },
            RequirementKind.Graphics => row with
            {
                Yours = Join(pc.Gpu, pc.GpuMemoryGb > 0 ? FormatGb(pc.GpuMemoryGb) : ""),
                Warning = Short(SmallestGb(row.Value), pc.GpuMemoryGb) is { } need ? $"Below: needs {FormatGb(need)} of graphics memory" : "",
            },
            RequirementKind.DirectX => row with
            {
                Yours = pc.DirectXLabel,
                Warning = RequiredDirectX(row.Value) is int want && pc.DirectXMajor is int have && have < want
                    ? $"Below: your graphics card supports DirectX {have}" : "",
            },
            RequirementKind.Storage => StorageRow(row, pc, install),
            RequirementKind.Sound => row with { Yours = pc.SoundDevice },
            _ => row,
        }).ToList();
    }

    private static RequirementRow StorageRow(RequirementRow row, PcSummary pc, InstallState install)
    {
        if (install.InstalledOn != null) return row with { Yours = $"Installed on {install.InstalledOn}" };
        if (install.MayBeInstalled || pc.Drives.Count == 0) return row;
        var roomiest = pc.Drives.MaxBy(d => d.FreeGb);
        return row with
        {
            Yours = $"{FormatGb(roomiest.FreeGb)} free on {roomiest.Letter}",
            Warning = Short(SmallestGb(row.Value), roomiest.FreeGb) is { } need ? $"Not enough free space: needs {FormatGb(need)}" : "",
        };
    }

    /// <summary>Only "Windows 11" with nothing older beside it, on a PC running Windows 10.</summary>
    private static string OsWarning(string value, PcSummary pc) =>
        pc.WindowsMajor == 10 && Windows11().IsMatch(value) && !OlderWindows().IsMatch(value) ? "Below: needs Windows 11" : "";

    /// <summary>The smallest size the line states, in GB: "6 GB" in "GTX 1060 6GB or RX 580 8GB", so a
    /// line naming several only warns below the least it asks for.</summary>
    internal static double? SmallestGb(string value)
    {
        double? smallest = null;
        foreach (Match m in SizeAmount().Matches(value))
        {
            double n = double.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
            double gb = m.Groups["unit"].Value.ToUpperInvariant() switch { "TB" => n * 1024, "MB" => n / 1024, _ => n };
            if (gb > 0 && (smallest == null || gb < smallest)) smallest = gb;
        }
        return smallest;
    }

    /// <summary>The DirectX version a line asks for ("Version 11", "DirectX 9.0c"), or null.</summary>
    internal static int? RequiredDirectX(string value) =>
        DirectXNumber().Match(value) is { Success: true } m ? int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture) : null;

    /// <summary>The amount needed when this PC has less, with a little slack for rounding (a "32 GB" PC can read 31.9).</summary>
    private static double? Short(double? need, double have) => need is double n && have > 0 && have + 0.25 < n ? n : null;

    internal static string FormatGb(double gb) =>
        gb >= 1024 ? $"{(gb / 1024).ToString("0.#", CultureInfo.CurrentCulture)} TB"
        : gb >= 10 || gb == Math.Floor(gb) ? $"{Math.Round(gb):0} GB"
        : $"{gb.ToString("0.#", CultureInfo.CurrentCulture)} GB";

    private static string Join(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : $"{a}, {b}";
}
