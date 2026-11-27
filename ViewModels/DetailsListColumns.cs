using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace TrayTrigger.ViewModels;

/// <summary>
/// The Details List's column widths, shared by its header and every row: Category, Profile, Playtime, Last Played
/// and Size, each set by dragging the handle on its header's right edge, as in File Explorer. Game Title fills what
/// is left and never gets narrower than <see cref="TitleMinWidth"/>; the icon and Actions columns are fixed. Saved
/// in <c>AppSettings.DetailsColumnWidths</c> (LibraryViewModel attaches the save); a column with no saved width,
/// or one outside its limits, is at its default.
/// One instance, so the XAML can bind the header's and the rows' ColumnDefinitions, which are outside the visual
/// tree, through <c>{x:Static}</c>.
/// </summary>
public sealed class DetailsListColumns : ViewModelBase
{
    public const string Category = "Category";
    public const string Profile = "Profile";
    public const string Playtime = "Playtime";
    public const string LastPlayed = "LastPlayed";
    public const string Size = "Size";

    /// <summary>Each resizable column's default and smallest width. The smallest still shows its header at 130% text.</summary>
    internal static readonly IReadOnlyDictionary<string, (double Default, double Min)> Limits = new Dictionary<string, (double, double)>
    {
        [Category] = (135, 80),
        [Profile] = (90, 80),
        [Playtime] = (110, 85),
        [LastPlayed] = (125, 100),
        [Size] = (90, 60),
    };

    /// <summary>The one the XAML binds. After <see cref="Limits"/>: static fields start in the order they are written.</summary>
    public static DetailsListColumns Instance { get; } = new();

    public const double MaxWidth = 400;
    /// <summary>Game Title keeps at least this much: a drag that would squeeze it further stops there.</summary>
    public const double TitleMinWidth = 150;

    private readonly Dictionary<string, double> _widths = Limits.ToDictionary(p => p.Key, p => p.Value.Default);
    private Action<Dictionary<string, double>>? _save;

    public GridLength CategoryWidth => new(Width(Category));
    public double ProfileWidth => Width(Profile);
    public GridLength PlaytimeWidth => new(Width(Playtime));
    public GridLength LastPlayedWidth => new(Width(LastPlayed));
    public GridLength SizeWidth => new(Width(Size));

    public double Width(string column) => _widths.TryGetValue(column, out double w) ? w : 0;

    /// <summary>Takes the saved widths (any missing or out of range is the default) and how to save a change.</summary>
    public void Attach(IReadOnlyDictionary<string, double>? saved, Action<Dictionary<string, double>> save)
    {
        _save = save;
        foreach (var (column, limits) in Limits)
            _widths[column] = saved != null && saved.TryGetValue(column, out double w) && w >= limits.Min && w <= MaxWidth ? w : limits.Default;
        NotifyAll();
    }

    /// <summary>
    /// A drag on <paramref name="column"/>'s handle moved it by <paramref name="change"/>. <paramref name="titleWidth"/> is
    /// Game Title's width now: a column only grows by what the title can give above its minimum. Not saved until
    /// <see cref="Save"/>, so a drag writes settings once.
    /// </summary>
    public void Resize(string column, double change, double titleWidth)
    {
        if (!Limits.TryGetValue(column, out var limits)) return;
        double current = Width(column);
        double room = Math.Max(0, titleWidth - TitleMinWidth);
        double width = Math.Clamp(current + Math.Min(change, room), limits.Min, Math.Max(limits.Min, MaxWidth));
        if (Math.Abs(width - current) < 0.5) return;
        _widths[column] = Math.Round(width);
        Notify(column);
    }

    /// <summary>Puts one column, or every column when <paramref name="column"/> is null, back to its default, and saves.</summary>
    public void Reset(string? column = null)
    {
        foreach (var (key, limits) in Limits)
            if (column == null || column == key) _widths[key] = limits.Default;
        NotifyAll();
        Save();
    }

    public void Save() => _save?.Invoke(Saved());

    /// <summary>Only the widths moved from their default, so a later default change still reaches the rest.</summary>
    internal Dictionary<string, double> Saved() =>
        _widths.Where(p => Math.Abs(p.Value - Limits[p.Key].Default) >= 0.5).ToDictionary(p => p.Key, p => p.Value);

    private void Notify(string column) => OnPropertyChanged(column switch
    {
        Category => nameof(CategoryWidth),
        Profile => nameof(ProfileWidth),
        Playtime => nameof(PlaytimeWidth),
        LastPlayed => nameof(LastPlayedWidth),
        _ => nameof(SizeWidth),
    });

    private void NotifyAll()
    {
        foreach (string column in Limits.Keys) Notify(column);
    }
}
