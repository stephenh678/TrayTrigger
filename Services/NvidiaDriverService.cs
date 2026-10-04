using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TrayTrigger.Services;

public enum DriverCheckOutcome
{
    /// <summary>The installed driver is NVIDIA's newest for this GPU, or newer.</summary>
    UpToDate,
    NewerAvailable,
    /// <summary>NVIDIA's download site doesn't list this GPU by the name Windows gives it.</summary>
    GpuNotListed,
    /// <summary>Offline, NVIDIA's site didn't answer, or answered with something unexpected.</summary>
    Failed
}

/// <summary>What NVIDIA's site says the newest Game Ready driver for a GPU is.</summary>
public sealed record DriverCheckResult(DriverCheckOutcome Outcome, string? LatestVersion = null, string? ReleaseDate = null, string? DetailsUrl = null);

/// <summary>
/// Asks NVIDIA's own driver-download service for the newest Game Ready driver for a GPU - the
/// lookup nvidia.com/drivers runs when you pick your card. Two steps: find the GPU's product and
/// series IDs in NVIDIA's product list (cached for a week; it changes when a new card launches),
/// then ask for the newest WHQL, DCH driver for Windows 10/11 64-bit. NVIDIA doesn't document
/// either endpoint, so every failure is reported as "couldn't check", never as "up to date".
///
/// <para>Runs only when the user presses Check for Newer Driver: TrayTrigger makes no call to
/// NVIDIA on its own. Nothing about the PC is sent beyond which GPU model it asks about.</para>
/// </summary>
public class NvidiaDriverService
{
    private const string ProductListUrl = "https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3";
    private const string DriverLookupUrl = "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup&psid={0}&pfid={1}&osID=57&languageCode=1033&isWHQL=1&dch=1&sort1=0&numberOfResults=1";
    /// <summary>NVIDIA's own page for finding a driver, for a GPU its service doesn't list.</summary>
    public const string DriversPageUrl = "https://www.nvidia.com/en-us/drivers/";
    private static readonly TimeSpan ProductListMaxAge = TimeSpan.FromDays(7);

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        MaxResponseContentBufferSize = MetadataHttpLimits.MaxResponseBytes
    };

    private static readonly string DefaultProductListPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrayTrigger", "nvidia-products.xml");

    private readonly string _productListPath;

    public NvidiaDriverService(string? productListPath = null)
    {
        _productListPath = productListPath ?? DefaultProductListPath;
    }

    public async Task<DriverCheckResult> CheckAsync(string gpuName, string installedVersion, CancellationToken ct = default)
    {
        try
        {
            string? productList = await GetProductListAsync(ct).ConfigureAwait(false);
            if (productList == null)
                return new DriverCheckResult(DriverCheckOutcome.Failed);

            var product = FindProduct(productList, gpuName);
            if (product == null)
            {
                LoggingService.Info("SystemInfo", $"NVIDIA driver check: '{gpuName}' isn't in NVIDIA's product list.");
                return new DriverCheckResult(DriverCheckOutcome.GpuNotListed, DetailsUrl: DriversPageUrl);
            }

            string url = string.Format(CultureInfo.InvariantCulture, DriverLookupUrl, product.Value.SeriesId, product.Value.ProductId);
            string json = await HttpClient.GetStringAsync(url, ct).ConfigureAwait(false);
            var latest = ParseLatestDriver(json);
            if (latest == null)
            {
                LoggingService.Warn("SystemInfo", $"NVIDIA driver check: no driver in the reply for '{gpuName}' (psid {product.Value.SeriesId}, pfid {product.Value.ProductId}).");
                return new DriverCheckResult(DriverCheckOutcome.Failed);
            }

            var outcome = CompareVersions(installedVersion, latest.Value.Version) >= 0 ? DriverCheckOutcome.UpToDate : DriverCheckOutcome.NewerAvailable;
            LoggingService.Info("SystemInfo", $"NVIDIA driver check for '{gpuName}': installed {installedVersion}, newest Game Ready {latest.Value.Version} ({latest.Value.ReleaseDate}) - {outcome}.");
            return new DriverCheckResult(outcome, latest.Value.Version, latest.Value.ReleaseDate, latest.Value.DetailsUrl ?? DriversPageUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LoggingService.Warn("SystemInfo", $"NVIDIA driver check failed: {ex.Message}");
            return new DriverCheckResult(DriverCheckOutcome.Failed);
        }
    }

    private async Task<string?> GetProductListAsync(CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(_productListPath);
            if (info.Exists && DateTime.UtcNow - info.LastWriteTimeUtc < ProductListMaxAge)
                return await File.ReadAllTextAsync(_productListPath, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            LoggingService.Warn("SystemInfo", $"Couldn't read the cached NVIDIA product list: {ex.Message}");
        }

        try
        {
            string xml = await HttpClient.GetStringAsync(ProductListUrl, ct).ConfigureAwait(false);
            if (!xml.Contains("<LookupValue", StringComparison.Ordinal))
                return null;
            Directory.CreateDirectory(Path.GetDirectoryName(_productListPath)!);
            await File.WriteAllTextAsync(_productListPath, xml, ct).ConfigureAwait(false);
            return xml;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            LoggingService.Warn("SystemInfo", $"Couldn't fetch NVIDIA's product list: {ex.Message}");
            // An old copy is still better than nothing: the IDs of a card don't change.
            return File.Exists(_productListPath) ? await File.ReadAllTextAsync(_productListPath, ct).ConfigureAwait(false) : null;
        }
    }

    private static readonly Regex ProductEntry = new(
        @"<LookupValue\s+ParentID=""(?<psid>\d+)""\s*>\s*<Name>(?<name>[^<]+)</Name>\s*<Value>(?<pfid>\d+)</Value>",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// The series and product IDs for a GPU, matched by name. NVIDIA's list names cards both with
    /// and without the "NVIDIA " prefix ("NVIDIA GeForce RTX 5080", "GeForce RTX 4090"), and
    /// Windows' name usually has it, so both are compared without it.
    /// </summary>
    internal static (int SeriesId, int ProductId)? FindProduct(string productListXml, string gpuName)
    {
        string wanted = Normalize(gpuName);
        foreach (Match m in ProductEntry.Matches(productListXml))
        {
            if (Normalize(System.Net.WebUtility.HtmlDecode(m.Groups["name"].Value)) == wanted)
                return (int.Parse(m.Groups["psid"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["pfid"].Value, CultureInfo.InvariantCulture));
        }
        return null;
    }

    private static string Normalize(string name)
    {
        string n = Regex.Replace(name.Replace("(TM)", "", StringComparison.OrdinalIgnoreCase), @"\s+", " ").Trim();
        if (n.StartsWith("NVIDIA ", StringComparison.OrdinalIgnoreCase)) n = n[7..];
        return n.ToUpperInvariant();
    }

    /// <summary>The newest driver's version, date and page from the lookup reply; null when the
    /// reply says it found none.</summary>
    internal static (string Version, string? ReleaseDate, string? DetailsUrl)? ParseLatestDriver(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("IDS", out var ids) || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() == 0)
            return null;
        if (!ids[0].TryGetProperty("downloadInfo", out var info))
            return null;
        if (!info.TryGetProperty("Success", out var ok) || ok.GetString() != "1")
            return null;
        string? version = info.TryGetProperty("Version", out var v) ? v.GetString() : null;
        if (string.IsNullOrWhiteSpace(version))
            return null;
        string? date = info.TryGetProperty("ReleaseDateTime", out var d) ? d.GetString() : null;
        string? details = info.TryGetProperty("DetailsURL", out var u) ? u.GetString() : null;
        if (details != null && !details.StartsWith("https://www.nvidia.com/", StringComparison.OrdinalIgnoreCase))
            details = null;
        return (version.Trim(), date, details);
    }

    /// <summary>Compares "617.14"-style versions numerically; unparseable parts count as 0.</summary>
    internal static int CompareVersions(string a, string b)
    {
        int[] Parts(string s) => s.Split('.').Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0).ToArray();
        var x = Parts(a);
        var y = Parts(b);
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            int c = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
            if (c != 0) return c;
        }
        return 0;
    }
}
