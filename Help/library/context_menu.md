# The game right-click menu

Right-clicking a game card opens its menu. Right-clicking one of two or more selected cards opens the batch menu instead - see Selecting several games at once.

## Layout

The menu is grouped, and the groups are the same in the single-game and batch menus so that what you learn in one applies to the other.

1. **Doing something now** - Play, Game Details, and, while the game is running, Suspend Game (or Resume Game), Close Game and Force Close Game.
2. **Marking it** - Add to Favorites, Hide, Open Containing Folder, and Locate Executable if the file is missing.
3. **Quick settings** - DLSS Override, then Performance Profile, CPU Cores, and Launch Options, each a cascading submenu.
4. **Editing it** - Edit Game Properties, Rename, the Change submenu, and Refresh Poster & Metadata.
5. **Steam** - store page, library, and Verify Game Files, for games with a Steam App ID.
6. **Remove from Library.**

## Quick settings

These write the same fields Edit Game Properties writes. They exist so that flipping one does not cost a dialog.

- **Performance Profile** - Off, Optimized, or Aggressive. A game that is playing right now keeps its current session; the new tier applies from its next launch.
- **DLSS Override** - runs the game on your GeForce driver's DLSS files instead of its own. Ticked when it is on. Only on a PC with an NVIDIA driver; see NVIDIA DLSS Override.
- **CPU Cores** - All Cores, which is the Windows default, or the choices that fit this PC's CPU: Auto, performance cores on an Intel hybrid, the 3D V-Cache or frequency cores on a dual-CCD Ryzen X3D, one CCD on another multi-CCD Ryzen. The submenu is hidden on a CPU with nothing to choose. See CPU Cores.
- **Launch Options** - Run as Administrator, and Close Launcher After Game Exits. The second only appears for games that launch through a platform client, since a plain local executable has no launcher behind it.

A check mark shows the current value. In the batch menu it shows only when every selected game agrees; when they differ, nothing is checked.

## Change

Match, Category, Icon, Poster Artwork, and Steam App ID moved into one Change submenu. Rename stayed at the top level because it is used far more often than the rest.

## The batch menu

The batch menu carries the same quick settings, applied to the whole selection. Favorite, Hide, Run as Administrator, and Close Launcher are all-or-nothing: a mixed selection turns every game on, and only a selection where all of them are already on turns them off. The label says which way the next click goes.
