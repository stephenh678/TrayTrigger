# The game right-click menu

Right-clicking a game card opens its menu. Right-clicking one of two or more selected cards opens the batch menu instead - see Selecting several games at once.

## Layout

The menu is grouped, and the groups are the same in the single-game and batch menus so that what you learn in one applies to the other.

1. **Doing something now** - Play, Game Details, and, while the game is running, End Session and Force Close Game.
2. **Marking it** - Add to Favorites, Hide, Open Containing Folder, and Locate Executable if the file is missing.
3. **Quick settings** - Performance Profile, CPU Cores, and Launch Options, each a cascading submenu.
4. **Editing it** - Edit Game Properties, Rename, the Change submenu, and Refresh Poster & Metadata.
5. **Steam** - store page, library, and Verify Game Files, for games with a Steam App ID.
6. **Remove from Library.**

## Quick settings

These write the same fields Edit Game Properties writes. They exist so that flipping one does not cost a dialog.

- **Performance Profile** - Off, Optimized, or Aggressive. A game that is playing right now keeps its current session; the new tier applies from its next launch.
- **CPU Cores** - All Cores, which is the Windows default, or Performance Cores Only. The submenu only appears on a hybrid CPU, the kind with separate performance and efficiency cores, because pinning does nothing on any other kind. Edit Game Properties still offers it everywhere, with a note, so a library moved to a hybrid machine can be set up in advance.
- **Launch Options** - Run as Administrator, and Close Launcher After Game Exits. The second only appears for games that launch through a platform client, since a plain local executable has no launcher behind it.

A check mark shows the current value. In the batch menu it shows only when every selected game agrees; when they differ, nothing is checked.

## Change

Match, Category, Icon, Poster Artwork, and Steam App ID moved into one Change submenu. Rename stayed at the top level because it is used far more often than the rest.

## The batch menu

The batch menu carries the same quick settings, applied to the whole selection. Favorite, Hide, Run as Administrator, and Close Launcher are all-or-nothing: a mixed selection turns every game on, and only a selection where all of them are already on turns them off. The label says which way the next click goes.
