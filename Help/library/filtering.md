# Filtering the library

The filter button sits beside the sort box in the library toolbar, under the standard filter icon. It narrows what the list shows without changing anything about your games.

## How it relates to the tabs and the search box

- The category tabs are your own labels for your games, and stay the primary cut. The filter works inside whichever tab is selected.
- The search box is a text match on title, category, and path.
- All three apply together. On the RPG tab, with "steam" typed and the Steam filter on, you see RPG games from Steam whose title, category, or path contains "steam".

## What you can filter on

The flyout is in groups, each under a small blue heading. The options under a heading are what you press. How ticks in a group combine is under "How the ticks combine" below.

**Launcher** - Steam, Epic Games, GOG, EA, Ubisoft Connect, Xbox, Battle.net, and "Not from a launcher" for anything you added by hand. Only launchers you actually have games from are listed, so there is never a tick box that can only return nothing.

**Last played**
- Never played - what is still in the backlog.
- Played in last 30 days.
- Not played in over 6 months - a game you did play once and have left alone since. Tick it with Never played for the whole stale backlog.

**Performance Profile** - Off, Optimized, or Aggressive. Useful for finding the games you have not set a tier on yet.

**TrayTrigger settings** - what TrayTrigger has been told to do for a game.
- Favorite - the same set as the Favorites tab, available here so you can combine it with the others.
- Has a hotkey, Has launch scripts, Runs as administrator.
- DLSS Override on, CPU Cores set, HDR set for this game. DLSS Override is listed on a PC with an NVIDIA driver, CPU Cores where Windows offers a choice, and HDR where a display can do HDR - or when a game already has the setting, so it can still be found.

**Needs attention** - what is unfinished or broken.
- Executable missing - games you added yourself whose file has moved, after a drive letter change or a reinstall.
- Not installed - launcher games their launcher no longer has.
- No poster, Not matched to Steam or RAWG - what a scan left for you to fix. Games with no category already have their own tab, so there is no filter for them.

## The opposite of a setting

The options in TrayTrigger settings are yes/no facts, so each has an opposite. Press one once to tick it (a blue check appears), press it again and its label turns amber and changes its wording to the opposite (the check stays blue) - "DLSS Override on" becomes "DLSS Override off", "Has a hotkey" becomes "No hotkey", "Has launch scripts" becomes "No launch scripts" - and press a third time to clear it. The opposite leaves out the games that match the original: it finds the games without it.

The other groups have no opposite. They are choices between values, so ticking the other values says the same thing.

## How the ticks combine

- Within Launcher, Last played, Performance Profile and Needs attention, ticking two shows games that match either. A game is only from one launcher, so Steam and Epic ticked together shows games from both. Within TrayTrigger settings, ticking two shows only games that match both, so Has a hotkey and Has launch scripts shows games with a hotkey and scripts.
- Across groups they are "and". Steam plus Never played shows Steam games you have not played.
- An opposite in TrayTrigger settings leaves out the games that match the original, so "Has launch scripts" with "No hotkey" shows games with scripts and no hotkey.
- A group with nothing ticked places no restriction. Ticking every box in Launcher, Last played, Performance Profile or Needs attention is the same as ticking none.

## Saved filters

The Saved section at the top of the flyout keeps combinations you use again.

- **Save current...** names the ticks on screen and saves them. Only the ticks are saved: the category tab and the search box are not, so a saved filter works on any tab. Saving under a name you already used asks first, showing what is saved and what you are about to save, and then replaces it.
- Pressing a saved filter puts exactly its ticks, opposites included, on screen, replacing the ones there. It shows a check while the ticks on screen match it. Change a tick and it reads "edited"; **Save current...** under the same name saves the change.
- The x beside a saved filter deletes it, after asking. Your games are not affected.
- A saved launcher that has left your library is skipped when you apply the filter.
- Saved filters are part of Backup & Restore.

## Clearing, and what the filter did

- **Clear**, at the top, resets every group. **clear** beside a group's heading resets just that group, and shows only while something in it is on.
- Under the count at the top, the flyout says what the ticks did: "Showing 31 of 112" is 31 games out of the 112 the current tab and search would show. With nothing ticked it just says how many games are in view.

## The count badge

Filters stay set between sessions, the same as the category tab and the sort order. Unlike those two, the filter is invisible once the flyout closes, so the button carries a badge showing how many filters are on. It disappears when none are. If a filter hides everything, the library says so and offers a Clear filters button rather than showing the empty-library message.

Clear in the flyout, or Clear filters in that message, removes every tick at once. Clear leaves the flyout open, so you can pick a different filter straight away.

## Details

- Filtering never changes your games - it only changes what is drawn. Selecting games and then changing the filter clears the selection, so an action can never reach a game you can no longer see.
- If you tick a launcher and later remove every game from it, the tick is dropped rather than left as a filter with no box on screen to untick.
- Settings > Reset to Defaults leaves your filters alone, the same as it leaves the selected tab and sort order alone.
