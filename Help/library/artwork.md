# How artwork is found

Each game card can show a vertical poster instead of a square icon. TrayTrigger tries several sources in order and keeps the first good result.

## Sources, in order

- A custom cover you chose in Edit Game. Always wins.
- Steam's official vertical poster for the game's linked App ID. This is what most games end up with.
- SteamGridDB community art, if you enabled it and added an API key. Useful for new, indie, or unreleased games that Steam has no poster for yet, and for non-Steam games. When RAWG is enabled, its official title for the game is searched first, which finds art the scanner's folder name often misses.
- The icon extracted from the exe, shown on a generated background if nothing better exists.

## Linking a game to Steam

- Games added by the Steam scanner are linked automatically.
- For everything else, TrayTrigger searches the Steam store by name when the game is added, if online title search is on.
- To fix a wrong match, open the game's details and click Change match to search the Steam store and pick the right listing. Or open Edit Game, enter the Steam App ID from the store page URL, and click Fetch by ID.

## Show details on hover

Turn it on with the eye button beside the view buttons above the library, or in Settings › Library & Art. In Poster Grid and Extra Large, each card then shows only its artwork. Point at a card, Tab to it, or right-click it, and the art zooms in while its badges, title, playtime and Play button appear. The card itself stays the same size.

- Choose what stays on every card under the checkbox in Settings › Library & Art: the launcher logo, category, HIDDEN, PLAYING and NOT INSTALLED (or MISSING) tags, a favorite's star, the title, playtime and last played date. Anything ticked stays on screen, and the rest waits for the pointer. Out of the box only the PLAYING tag stays.
- A game that isn't installed always stays dimmed, and the Play button always waits until you point at the card.
- A game with no poster keeps its icon and name, since there's no art to recognize it by.
- Favorite a game from its right-click menu. In this mode the star on the card isn't a Tab stop, because it only shows while the card has focus.
- With Windows' Animation effects turned off, the art doesn't zoom and the details appear without fading.

## Refresh All Game Posters

Re-checks every game, including ones that already have a cover. Use it after a game you added before release has launched on Steam, or after enabling SteamGridDB.

## Where art is stored

Downloaded posters and extracted icons are cached under the local app data folder shown in Settings › Diagnostics & Storage. Clearing the cache forces everything to download again.

> SteamGridDB needs a free API key from steamgriddb.com. The key is stored in your settings file and only sent to SteamGridDB.
