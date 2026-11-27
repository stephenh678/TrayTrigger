using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace TrayTrigger.ViewModels;

/// <summary>
/// The Details List's column widths, shared by its header and every row, resized as in File Explorer: dragging the
/// handle on a column's right edge moves that edge, and the columns after it move with it. Game Title, Category,
/// Profile, Playtime, Last Played and Size can be resized; the icon and Actions columns are fixed, and Actions stays
/// at the right end with any spare room before it.
/// Until the first drag Game Title fills the row, as it always has (<see cref="TitleMaxWidth"/> is infinite). The
/// first drag fixes it at its width then, so every edge after it can follow the mouse. The title column is still a
/// star column capped at that width, so a window too narrow for every width shrinks the title (to
/// <see cref="TitleMinWidth"/>) before Actions could go off the edge.
/// Saved in <c>AppSettings.DetailsColumnWidths</c> (LibraryViewModel attaches the save); a column with no saved width,
/// or one outside its limits, is at its default. One instance, so the XAML can bind the header's and the rows'
/// ColumnDefinitions, which are outside the visual tree, through <c>{x:Static}</c>.
/// </summary>
public sealed class DetailsListColumns : ViewModelBase
{
    public const string Title = "Title";
    public const string Category = "Category";
    public const string Profile = "Profile";
    public const string Playtime = "Playtime";
    public const string LastPlayed = "LastPlayed";
    public const string Size = "Size";

    /// <summary>Each fixed column's default and smallest width. The smallest still shows its header at 130% text.</summary>
    internal static readonly IReadOnlyDictionary<string, (double Default, double Min)> Limits = new Dictionary<string, (double, double)>
    {
        [Category] = (135, 80),
        [Profile] = (90, 80),
        [Playtime] = (110, 85),
        [LastPlayed] = (125, 100),
        [Size] = (90, 60),
    };

    public const double MaxWidth = 400;
    public const double TitleMinWidth = 150;
    public const double TitleMaxDragWidth = 2000;

    /// <summary>The one the XAML binds. After <see cref="Limits"/>: static fields start in the order they are written.</summary>
    public static DetailsListColumns Instance { get; } = new();

    private readonly Dictionary<string, double> _widths = Limits.ToDictionary(p => p.Key, p => p.Value.Default);
    private double _titleWidth = double.PositiveInfinity;
    private Action<Dictionary<string, double>>? _save;

    /// <summary>Game Title's width once set by a drag; infinite while it fills the row.</summary>
    public double TitleMaxWidth => _titleWidth;
    public bool IsTitleFilling => double.IsPositiveInfinity(_titleWidth);
    public GridLength CategoryWidth => new(Width(Category));
    public double ProfileWidth => Width(Profile);
    public GridLength PlaytimeWidth => new(Width(Playtime));
    public GridLength LastPlayedWidth => new(Width(LastPlayed));
    public GridLength SizeWidth => new(Width(Size));

    public double Width(string column) => column == Title ? _titleWidth : _widths.TryGetValue(column, out double w) ? w : 0;

    /// <summary>Takes the saved widths (any missing or out of range is the default) and how to save a change.</summary>
    public void Attach(IReadOnlyDictionary<string, double>? saved, Action<Dictionary<string, double>> save)
    {
        _save = save;
        foreach (var (column, limits) in Limits)
            _widths[column] = saved != null && saved.TryGetValue(column, out double w) && w >= limits.Min && w <= MaxWidth ? w : limits.Default;
        _titleWidth = saved != null && saved.TryGetValue(Title, out double t) && t >= TitleMinWidth && t <= TitleMaxDragWidth ? t : double.PositiveInfinity;
        NotifyAll();
    }

    /// <summary>The resizable columns from left to right; a drag only ever takes room from the ones after its column.</summary>
    internal static readonly string[] Order = [Title, Category, Profile, Playtime, LastPlayed, Size];

    /// <summary>
    /// A drag on <paramref name="column"/>'s handle moved the mouse <paramref name="change"/> from that column's right
    /// edge, and the edge follows it: narrowing gives the room to the spare space before Actions (the columns after
    /// it slide left); widening takes the spare space first (they slide right), then width from the columns after it,
    /// the nearest first, each down to its minimum, never from a column before it. <paramref name="titleShown"/> and
    /// <paramref name="spare"/> are Game Title's and the spare space's widths on screen now; <paramref name="isShown"/>
    /// says whether a column is on screen (Profile leaves a narrow window). The first drag fixes Game Title at its
    /// width. Not saved until <see cref="Save"/>, so a drag writes settings once.
    /// </summary>
    public void Resize(string column, double change, double titleShown, double spare, Func<string, bool> isShown)
    {
        if (IsTitleFilling && titleShown > 0)
        {
            _titleWidth = Math.Round(titleShown);
            OnPropertyChanged(nameof(TitleMaxWidth));
        }
        // Game Title's width on screen, not its cap: a narrow window can show less than the cap.
        double current = column == Title ? Math.Min(_titleWidth, Math.Round(titleShown)) : Width(column);
        double min = column == Title ? TitleMinWidth : Limits.TryGetValue(column, out var l) ? l.Min : 0;
        double max = column == Title ? TitleMaxDragWidth : MaxWidth;
        if (min == 0) return;

        double target = Math.Clamp(Math.Round(current + change), min, max);
        double grow = target - current;
        if (grow > 0)
        {
            double needed = grow - Math.Max(0, Math.Floor(spare));
            foreach (string after in Order.SkipWhile(c => c != column).Skip(1).Where(isShown))
            {
                if (needed <= 0) break;
                double give = Math.Min(needed, _widths[after] - Limits[after].Min);
                if (give <= 0) continue;
                _widths[after] -= give;
                needed -= give;
                Notify(after);
            }
            if (needed > 0) target -= needed;
        }
        if (target == current && column != Title) return;
        if (column == Title)
        {
            _titleWidth = target;
            OnPropertyChanged(nameof(TitleMaxWidth));
        }
        else
        {
            _widths[column] = target;
            Notify(column);
        }
    }

    /// <summary>Puts one column, or every column when <paramref name="column"/> is null, back to its default, and saves.
    /// Game Title's default is filling the row again.</summary>
    public void Reset(string? column = null)
    {
        foreach (var (key, limits) in Limits)
            if (column == null || column == key) _widths[key] = limits.Default;
        if (column == null || column == Title) _titleWidth = double.PositiveInfinity;
        NotifyAll();
        Save();
    }

    public void Save() => _save?.Invoke(Saved());

    /// <summary>Only the widths moved from their default, so a later default change still reaches the rest.</summary>
    internal Dictionary<string, double> Saved()
    {
        var saved = _widths.Where(p => Math.Abs(p.Value - Limits[p.Key].Default) >= 0.5).ToDictionary(p => p.Key, p => p.Value);
        if (!IsTitleFilling) saved[Title] = _titleWidth;
        return saved;
    }

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
        OnPropertyChanged(nameof(TitleMaxWidth));
        foreach (string column in Limits.Keys) Notify(column);
    }
}
