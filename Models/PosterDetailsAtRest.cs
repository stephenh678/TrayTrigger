namespace TrayTrigger.Models;

/// <summary>
/// With <see cref="AppSettings.PosterDetailsOnHover"/> on, which of a poster card's details stay on
/// the card while it's at rest. True keeps that detail on screen all the time; false holds it back
/// until the card is pointed at, focused or right-clicked. Settings › Library &amp; Art lists them
/// alphabetically.
/// <para>
/// The defaults are the 1.4.7 look: only PLAYING stays, because a game that's running shouldn't
/// need a hover to say so. The not-installed dimming isn't listed - it's how the art is drawn, not
/// a detail over it, and always stays.
/// </para>
/// </summary>
public class PosterDetailsAtRest
{
    /// <summary>The launcher's logo in the top-left corner (Steam, GOG, EA, Epic, ...).</summary>
    public bool LauncherLogo { get; set; }
    public bool Category { get; set; }
    /// <summary>Only on a game hidden from the library, shown while hidden games are listed.</summary>
    public bool HiddenTag { get; set; }
    public bool PlayingTag { get; set; } = true;
    /// <summary>A favourite's star. A game that isn't a favourite shows its empty star on hover only.</summary>
    public bool FavoriteStar { get; set; }
    /// <summary>NOT INSTALLED or MISSING, under the top row.</summary>
    public bool NotInstalledTag { get; set; }
    public bool Title { get; set; }
    public bool Playtime { get; set; }
    public bool LastPlayed { get; set; }
}
