# How artwork is found

Each game card can show a vertical poster instead of a square icon. TrayTrigger tries several sources in order and keeps the first good result.

## Sources, in order

- A custom cover you chose in Edit Game. Always wins.
- Steam's official vertical poster for the game's linked App ID. This is what most games end up with.
- SteamGridDB community art, if you enabled it and added an API key. Useful for new, indie, or unreleased games that Steam has no poster for yet, and for non-Steam games.
- The icon extracted from the exe, shown on a generated background if nothing better exists.

## Linking a game to Steam

- Games added by the Steam scanner are linked automatically.
- For everything else, TrayTrigger searches the Steam store by name when the game is added, if online title search is on.
- To fix a wrong match, open Edit Game, enter the Steam App ID from the store page URL, and click Fetch by ID.

## Refresh All Game Posters

Re-checks every game, including ones that already have a cover. Use it after a game you added before release has launched on Steam, or after enabling SteamGridDB.

## Where art is stored

Downloaded posters and extracted icons are cached under the local app data folder shown in Settings under Storage. Clearing the cache forces everything to download again.

> SteamGridDB needs a free API key from steamgriddb.com. The key is stored in your settings file and only sent to SteamGridDB.
