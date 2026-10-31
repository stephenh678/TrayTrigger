using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.ViewModels;

/// <summary>The most recent time the game was played, as Activity &amp; History wrote it: the date, how long, and what TrayTrigger did and put back.</summary>
public sealed record LastSessionInfo(string Title, IReadOnlyList<string> Lines);

/// <summary>What Game Details' Overview says beyond the store listing: where the game lives and how big it is, its links, the last session, and the header's chips.</summary>
public partial class GameDetailsViewModel
{
    private readonly ActivityService? _activity;
    private readonly Action? _openActivityAction;
    private readonly CancellationTokenSource _backgroundWork = new();

    // --- Developer and publisher, as one row when they are the same company ---

    private bool SamePublisher => HasDevelopers && HasPublishers
        && string.Equals(Developers.Trim(), Publishers.Trim(), StringComparison.OrdinalIgnoreCase);

    public string DeveloperLabel => SamePublisher ? "Made by" : "Developer";
    public bool ShowPublisherRow => HasPublishers && !SamePublisher;

    /// <summary>"Your setup" shows only on a page that can open Settings, where its links lead somewhere.</summary>
    public bool HasSetupCards => _editHost != null;

    // --- Size on disk, counted on this PC from the game's own folder ---

    private string _installSize = string.Empty;

    /// <summary>"74.2 GB". Empty until counted, and for a game with no local folder or too many files to be one game.</summary>
    public string InstallSize
    {
        get => _installSize;
        private set
        {
            if (SetProperty(ref _installSize, value))
                OnPropertyChanged(nameof(HasInstallSize));
        }
    }

    public bool HasInstallSize => InstallSize.Length > 0;

    /// <summary>Counts off the UI thread. A page closed first stops the count.</summary>
    public async Task LoadInstallSizeAsync()
    {
        try
        {
            var game = Game;
            var ct = _backgroundWork.Token;
            long? bytes = await Task.Run(() => InstallSizeService.GameRoot(game) is { } root ? InstallSizeService.MeasureCached(root, ct) : null, ct).ConfigureAwait(true);
            InstallSize = bytes is > 0 ? InstallSizeService.Format(bytes.Value) : string.Empty;
        }
        catch (OperationCanceledException)
        {
            // The page was closed before the count finished: there is nobody to show it to.
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("GameDetails", $"Couldn't read the size of '{Game.Name}': {ex.Message}");
        }
    }

    /// <summary>Called when the page closes: stops anything still being counted or fetched for it.</summary>
    public void StopBackgroundWork()
    {
        try { _backgroundWork.Cancel(); }
        catch (ObjectDisposedException)
        {
            // Already stopped.
        }
    }

    // --- Links: where to read more, whichever source is showing ---

    private ICommand? _openSteamDb;
    private ICommand? _openWebsite;
    private ICommand? _openActivity;

    private string? SteamDbUrl => !string.IsNullOrWhiteSpace(Game.SteamAppId) && UrlProtocolHelper.IsValidSteamAppId(Game.SteamAppId.Trim())
        ? $"https://steamdb.info/app/{Game.SteamAppId.Trim()}/"
        : null;

    public bool HasSteamDbPage => SteamDbUrl is not null;
    public ICommand OpenSteamDbCommand => _openSteamDb ??= new RelayCommand(() => ExecuteOpenUrl(SteamDbUrl), () => HasSteamDbPage);

    /// <summary>The developer's own site, as Steam lists it. Steam's listing only, so it follows the Steam | RAWG switch.</summary>
    public string? WebsiteUrl => IsRawgActive ? null : _details?.Website;
    public bool HasWebsite => !string.IsNullOrWhiteSpace(WebsiteUrl);
    public ICommand OpenWebsiteCommand => _openWebsite ??= new RelayCommand(() => ExecuteOpenUrl(WebsiteUrl), () => HasWebsite);

    /// <summary>The row of links shows only when there is at least one to put in it.</summary>
    public bool ShowLinks => ShowSteamStoreButton || HasPcGamingWikiPage || HasSteamDbPage || HasWebsite;

    // --- The header's chips: how Steam's players rated it, and whether it is running ---

    /// <summary>"92% positive", from the reviews Steam counts; the full line ("92% positive (14,280 reviews)") is its tooltip.
    /// A saved copy from before Steam moved its review page still reads "Very Positive · 92%". Empty when Steam gave none, and while RAWG is showing.</summary>
    public string HeroRatingChip
    {
        get
        {
            if (!HasReviewSummary) return string.Empty;
            string text = ReviewSummary!;
            int paren = text.IndexOf(" (", StringComparison.Ordinal);
            string label = paren > 0 ? text[..paren] : text;
            if (label.Contains('%')) return label;
            return _details?.ReviewScorePercent is int percent ? $"{label} · {percent}%" : label;
        }
    }

    public bool HasHeroRatingChip => HeroRatingChip.Length > 0;
    public bool HasHeroStateChip => IsPlaying;

    // --- News: the latest three ---

    /// <summary>Steam's three newest posts; "View All News" opens the rest. A patch note carries its PATCH tag.</summary>
    public IReadOnlyList<SteamNewsItem> VisibleNews => NewsItems.OrderByDescending(n => n.Date).Take(3).ToList();

    // --- Last session ---

    private LastSessionInfo? _lastSession;
    private bool _lastSessionRead;

    /// <summary>The newest "Played" line Activity &amp; History has for this game, with what it says TrayTrigger did and put back.</summary>
    public LastSessionInfo? LastSession
    {
        get
        {
            if (!_lastSessionRead)
            {
                _lastSession = ReadLastSession();
                _lastSessionRead = true;
            }
            return _lastSession;
        }
    }

    public bool HasLastSession => LastSession is not null;

    private LastSessionInfo? ReadLastSession()
    {
        var log = _activity ?? ActivityService.Current;
        if (log == null) return null;

        var row = log.Entries
            .Where(e => string.Equals(e.Subject, Game.Name, StringComparison.OrdinalIgnoreCase)
                && e.Text.StartsWith("Played ", StringComparison.Ordinal))
            .OrderByDescending(e => e.TimeUtc)
            .FirstOrDefault();
        if (row == null) return null;

        // "Played Hades · 48m": what follows the dot is how long.
        int dot = row.Text.LastIndexOf(" · ", StringComparison.Ordinal);
        string length = dot >= 0 ? row.Text[(dot + 3)..] : string.Empty;
        string when = row.TimeUtc.ToLocalTime().ToString("MMM d, yyyy", System.Globalization.CultureInfo.CurrentCulture);
        // The three lines that answer "what did it do, and was it put back": when it ran, which profile, and the put-back.
        // The rest of the record (what each tweak was, what was skipped, the game and tools) is in Activity.
        var lines = (row.Detail ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => LastSessionLabels.Any(label => l.StartsWith(label + ":", StringComparison.Ordinal)))
            .Select(ShortLine)
            .ToList();
        return new LastSessionInfo(length.Length > 0 ? $"{when} · {length}" : when, lines);
    }

    private static readonly string[] LastSessionLabels = ["Played", LaunchRecord.Profile, "Put back"];

    /// <summary>A line of the record as one short line: "Profile: Optimized · changed: ..." is cut at the list of changes, and a long one ends in an ellipsis.</summary>
    private static string ShortLine(string line)
    {
        int changed = line.IndexOf(" · changed", StringComparison.Ordinal);
        if (changed > 0) line = line[..changed];
        const int max = 90;
        if (line.Length <= max) return line;
        int cut = line.LastIndexOf(' ', max - 1);
        return line[..(cut > 40 ? cut : max)].TrimEnd(' ', '·', ',', ';') + "…";
    }

    /// <summary>Closes the page and opens Activity &amp; History, narrowed to this game.</summary>
    public ICommand OpenActivityCommand => _openActivity ??= new RelayCommand(() =>
    {
        if (!CanLeave()) return;
        RequestClose?.Invoke();
        _openActivityAction?.Invoke();
    }, () => _openActivityAction != null);
}
