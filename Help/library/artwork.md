# How artwork is found

A game has three pictures, and each has its own card on Edit Game's **Artwork** tab (Game Details > Settings > Artwork):

- **Hero banner**: the wide picture behind the game's name on Game Details. About 1920 by 620.
- **Poster**: the vertical picture on library cards. Tall, 2 by 3.
- **Icon**: the small square one in the tray menu and the list view.

Each card says whether the picture is **Automatic** (found for the game) or **Custom** (a file you chose). **Choose image...** makes it Custom; so does dropping a picture file on the card. **Use automatic** puts the automatic one back, and **Find again** (Automatic pictures only) looks for it again. The Hero banner card marks the part Game Details crops. The icon can also be an exe, which gives its icon.

A poster or icon you chose yourself before 1.7.6 shows as Automatic, because nothing marked it as yours then. It stays as it is until you press **Use automatic** or **Find again**, or run Refresh All Game Posters, which replace it with the automatic picture. Choose the picture again and it is marked Custom from then on.

## Hero banners

The hero banner is Steam's own wide hero banner for the game's App ID, from the same Steam servers as its poster. If Steam has none and you have given TrayTrigger a SteamGridDB key, SteamGridDB's hero banner for that App ID is used, and for a game with no Steam listing one is searched for by name (a title that doesn't closely match is refused). A game with no hero banner shows its poster, blurred, behind its name. A game nothing was found for is not asked about again for a week, unless you have since added a SteamGridDB key; **Find again** asks at once. A hero banner you choose is kept with your posters and goes with a backup.

## Posters: sources, in order

- A custom cover you chose in Edit Game. Always wins.
- Steam's official vertical poster for the game's linked App ID. This is what most games end up with.
- SteamGridDB community art, if you enabled it and added an API key. Useful for new, indie, or unreleased games that Steam has no poster for yet, and for non-Steam games. When RAWG is enabled, its official title for the game is searched first, which finds art the scanner's folder name often misses.
- The icon extracted from the exe, shown on a generated background if nothing better exists.

## Linking a game to Steam

- Games added by the Steam scanner are linked automatically.
- For everything else, TrayTrigger searches the Steam store by name when the game is added, if online title search is on.
- To fix a wrong match, open the game's details and use the Steam menu on the About card > Change match..., or open Edit Game > Identity & Library > Online match and click Change match... to search the Steam store and pick the right listing. Or click **Enter an App ID**, type the one from the store page URL, and click Fetch by ID. Tick what a new match may replace (Title, Genre, Poster) before you pick it; Undo puts back what it changed until you save.

## Show details on hover

Turn it on with the eye button beside the view buttons above the library, or in Settings > Library & Art. In Poster Grid and Extra Large, each card then shows only its artwork. Point at a card, Tab to it, or right-click it, and the art zooms in while its badges, title, playtime and Play button appear. The card itself stays the same size.

- Choose what stays on every card under the checkbox in Settings > Library & Art: the launcher logo, category, HIDDEN, PLAYING and NOT INSTALLED (or MISSING) tags, a favorite's star, the title, playtime and last played date. Anything ticked stays on screen, and the rest waits for the pointer. Out of the box only the PLAYING tag stays.
- A game that isn't installed always stays dimmed, and the Play button always waits until you point at the card.
- A game with no poster keeps its icon and name, since there's no art to recognize it by.
- Favorite a game from its right-click menu. In this mode the star on the card isn't a Tab stop, because it only shows while the card has focus.
- With Windows' Animation effects turned off, the art doesn't zoom and the details appear without fading.

## Refresh All Game Posters

Re-checks every game, including ones that already have a cover. Use it after a game you added before release has launched on Steam. Games with no Steam listing and no poster are searched on SteamGridDB by name. A new SteamGridDB key runs it for you as soon as the key checks out.

## Where art is stored

Downloaded posters and extracted icons are cached under the local app data folder shown in Settings > Diagnostics & Storage; hero banners are kept in a Heroes folder beside them. Clearing the cache forces everything to download again. Posters and hero banners you chose are kept with the covers and included in a backup.

> SteamGridDB needs a free API key from steamgriddb.com; see SteamGridDB and RAWG keys for getting one. The key is stored encrypted in your settings file and only sent to SteamGridDB.
