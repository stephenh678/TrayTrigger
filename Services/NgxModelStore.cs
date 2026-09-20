using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace TrayTrigger.Services;

/// <summary>
/// Reads the NVIDIA driver's own store of DLSS runtimes at
/// <c>C:\ProgramData\NVIDIA\NGX\models</c>. This is what the driver substitutes when the DLL
/// override is on, and it is the reason TrayTrigger never needs to download or swap a file: the
/// runtimes are already present, signed, and current.
///
/// <para>Everything here is read-only and comes from files the driver maintains. Nothing in this
/// class writes to the store.</para>
/// </summary>
public static class NgxModelStore
{
    /// <summary>The driver's model store. Fixed by the driver installer; not configurable.</summary>
    public static string DefaultRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NVIDIA", "NGX", "models");

    /// <summary>
    /// The three DLSS features, in one place. Everything that needs to name them - the model
    /// store's folders, the game's DLL names, the short code the records use, and the label the
    /// card shows - comes from here.
    /// </summary>
    public static readonly DlssFeature[] Features =
    {
        new("SR", "Super Resolution",   "dlss",  "nvngx_dlss"),
        new("RR", "Ray Reconstruction", "dlssd", "nvngx_dlssd"),
        new("FG", "Frame Generation",   "dlssg", "nvngx_dlssg")
    };

    /// <param name="Code">"SR", "RR" or "FG" - what the ownership record and observations store.</param>
    /// <param name="Name">What the card shows.</param>
    /// <param name="StoreFolder">Its folder under the driver's model store.</param>
    /// <param name="DllPrefix">The file name stem the game ships, with no extension.</param>
    public sealed record DlssFeature(string Code, string Name, string StoreFolder, string DllPrefix);

    /// <summary>One runtime held by the driver, as a version folder plus the payload inside it.</summary>
    public sealed record StoredRuntime(string Feature, string Version, uint EncodedVersion, string FilePath, long SizeBytes);

    /// <summary>
    /// Decodes the numeric folder names under <c>&lt;feature&gt;\versions</c>. NVIDIA packs the
    /// version as <c>(major &lt;&lt; 16) | (minor &lt;&lt; 8) | patch</c>: 20318464 is 310.9.0,
    /// 65644 is 1.0.108. Confirmed against nvngx_config.txt, which spells the same versions out.
    /// </summary>
    public static string DecodeVersion(uint encoded) =>
        $"{encoded >> 16}.{(encoded >> 8) & 0xFF}.{encoded & 0xFF}";

    /// <summary>
    /// Every runtime the driver holds, newest first per feature. An empty list means either no
    /// NVIDIA driver or a driver too old to keep a model store.
    /// </summary>
    public static List<StoredRuntime> Enumerate(string? root = null)
    {
        string baseDir = root ?? DefaultRoot;
        var result = new List<StoredRuntime>();
        if (!Directory.Exists(baseDir)) return result;

        foreach (var feature in Features)
        {
            string versionsDir = Path.Combine(baseDir, feature.StoreFolder, "versions");
            if (!Directory.Exists(versionsDir)) continue;

            foreach (string dir in SafeDirectories(versionsDir))
            {
                string name = Path.GetFileName(dir);
                if (!uint.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out uint encoded)) continue;

                // The payload is a hashed .bin, not an nvngx_*.dll. Path is what identifies it,
                // never the name.
                string filesDir = Path.Combine(dir, "files");
                foreach (string file in SafeFiles(filesDir))
                {
                    long size = 0;
                    try { size = new FileInfo(file).Length; } catch { /* reported as 0 */ }
                    result.Add(new StoredRuntime(feature.Name, DecodeVersion(encoded), encoded, file, size));
                }
            }
        }

        return result
            .OrderBy(r => r.Feature, StringComparer.Ordinal)
            .ThenByDescending(r => r.EncodedVersion)
            .ToList();
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try { return Directory.EnumerateDirectories(path); }
        catch (Exception ex) { LoggingService.Swallowed("NgxModelStore", ex, "listing the driver's DLSS store"); return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeFiles(string path)
    {
        try { return Directory.Exists(path) ? Directory.EnumerateFiles(path) : Array.Empty<string>(); }
        catch (Exception ex) { LoggingService.Swallowed("NgxModelStore", ex, "listing the driver's DLSS store"); return Array.Empty<string>(); }
    }
}
