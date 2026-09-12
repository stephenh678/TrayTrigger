# Filtering the library

The filter button sits beside the sort box in the library toolbar, under the standard filter icon. It narrows what the list shows without changing anything about your games.

## How it relates to the tabs and the search box

- The category tabs are your own labels for your games, and stay the primary cut. The filter works inside whichever tab is selected.
- The search box is a text match on title, category, and path.
- All three apply together. On the RPG tab, with "steam" typed and the Steam filter on, you see RPG games from Steam whose title, category, or path contains "steam".

## What you can filter on

**Launcher** - Steam, Epic Games, GOG, EA, Ubisoft Connect, Xbox, and "Not from a launcher" for anything you added by hand. Only launchers you actually have games from are listed, so there is never a tick box that can only return nothing.

**Performance Profile** - Off, Optimized, or Aggressive. Useful for finding the games you have not set a tier on yet.

**Status**
- Never played / Played at least once - what is still in the backlog.
- Favorite - the same set as the Favorites tab, available here so you can combine it with the others.
- Executable missing - everything that needs relocating after a drive letter change or a reinstall.
- Has a hotkey - what your keyboard shortcuts are currently bound to.
- Has launch scripts - which games run a pre-launch or post-exit script.
- Runs as administrator - which games raise a UAC prompt when you start them.

## How the ticks combine

- Within one group, ticks are "or". Steam and Epic ticked together shows games from either.
- Across groups they are "and". Steam plus Never played shows Steam games you have not played.
- A group with nothing ticked places no restriction. Ticking every box in a group is the same as ticking none.

## The count badge

Filters stay set between sessions, the same as the category tab and the sort order. Unlike those two, the filter is invisible once the flyout closes, so the button carries a badge showing how many filters are on. It disappears when none are. If a filter hides everything, the library says so and offers a Clear filters button rather than showing the empty-library message.

Clear in the flyout, or Clear filters in that message, removes every tick at once.

## Details

- Filtering never changes your games - it only changes what is drawn. Selecting games and then changing the filter clears the selection, so an action can never reach a game you can no longer see.
- If you tick a launcher and later remove every game from it, the tick is dropped rather than left as a filter with no box on screen to untick.
- Settings > Reset to Defaults leaves your filters alone, the same as it leaves the selected tab and sort order alone.
