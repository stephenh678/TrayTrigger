using System;
using System.Runtime.CompilerServices;
using TrayTrigger.Models;

namespace TrayTrigger.ViewModels;

/// <summary>
/// Which poster-card details stay on screen at rest with "Show details on hover" on: the
/// Settings › Library &amp; Art list, and what each card reads - Views/PosterDetailFade.cs binds a
/// card element to the property named by its <see cref="Views.PosterDetailPart"/>. Writes straight
/// through to <see cref="AppSettings.PosterDetailsAtRest"/> and saves.
/// </summary>
public sealed class PosterDetailsViewModel : ViewModelBase
{
    private readonly Func<PosterDetailsAtRest> _model;
    private readonly Action _save;

    /// <param name="model">Read each time, not captured: Restore defaults rewrites the settings in place.</param>
    public PosterDetailsViewModel(Func<PosterDetailsAtRest> model, Action save)
    {
        _model = model;
        _save = save;
    }

    private PosterDetailsAtRest M => _model();

    public bool LauncherLogo { get => M.LauncherLogo; set => Set(M.LauncherLogo, value, v => M.LauncherLogo = v); }
    public bool Category { get => M.Category; set => Set(M.Category, value, v => M.Category = v); }
    public bool HiddenTag { get => M.HiddenTag; set => Set(M.HiddenTag, value, v => M.HiddenTag = v); }
    public bool PlayingTag { get => M.PlayingTag; set => Set(M.PlayingTag, value, v => M.PlayingTag = v); }
    public bool FavoriteStar { get => M.FavoriteStar; set => Set(M.FavoriteStar, value, v => M.FavoriteStar = v); }
    public bool NotInstalledTag { get => M.NotInstalledTag; set => Set(M.NotInstalledTag, value, v => M.NotInstalledTag = v); }
    public bool Title { get => M.Title; set => Set(M.Title, value, v => M.Title = v); }
    public bool Playtime { get => M.Playtime; set => Set(M.Playtime, value, v => M.Playtime = v); }
    public bool LastPlayed { get => M.LastPlayed; set => Set(M.LastPlayed, value, v => M.LastPlayed = v); }

    /// <summary>
    /// The dark band behind the top row stays at rest only for the details every card has. The tags
    /// only some games carry (HIDDEN, PLAYING, NOT INSTALLED) have backgrounds of their own, so keeping
    /// one doesn't darken the top of every card for the few that show it.
    /// </summary>
    public bool TopShade => LauncherLogo || Category;

    /// <summary>The gradient under the title and playtime, kept whenever any of them is.</summary>
    public bool BottomShade => Title || Playtime || LastPlayed;

    private void Set(bool current, bool value, Action<bool> write, [CallerMemberName] string? propertyName = null)
    {
        if (current == value) return;
        write(value);
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(TopShade));
        OnPropertyChanged(nameof(BottomShade));
        _save();
    }

    /// <summary>Re-reads everything, after Restore defaults has rewritten the settings underneath.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}
