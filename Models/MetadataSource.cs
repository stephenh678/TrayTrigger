namespace TrayTrigger.Models;

/// <summary>
/// Which source the Game Details window shows text metadata (developer, publisher, release date,
/// synopsis, genres) from. A per-game view preference - it never changes the library category,
/// cover art or sorting. <see cref="Auto"/> means "decide by availability": Steam when the game
/// has a Steam App ID (Steam is richer there - it alone has the review summary and patch notes),
/// otherwise RAWG. Once the user flips the toggle, the explicit choice is remembered.
/// </summary>
public enum MetadataSource
{
    Auto,
    Steam,
    Rawg
}
