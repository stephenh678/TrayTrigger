# How the Game Scanner works

"Scan for Games" looks for installed games it doesn't already know about - from every launcher integration you've enabled, plus any extra Scan Locations you've added - and prompts you to pick which ones to add.

## Launcher Integrations

Each "Enable X Library Integration" toggle controls whether a scan looks for that platform's games. Games are found from the launcher's own records (Steam's manifests, GOG/Epic/Ubisoft's registry entries, EA's install manifests, Windows' Gaming Services records for Xbox / PC Game Pass, Blizzard's install records for Battle.net), so no folder setup is needed.

Battle.net games launch through Battle.net, which signs the game in. If Battle.net is closed, TrayTrigger starts it and keeps asking until the game opens. Each game's launch code is read from Battle.net's own data and saved with the game, so no list has to be kept up to date. If the code can't be found (for example, just after Battle.net's cache was cleared), TrayTrigger opens Battle.net's Play tab and tells you to click Play; performance profiles and scripts still apply.

- **Steam** is the one platform with several possible library folders. They're detected automatically and listed under the Steam toggle; untick one to skip that library on the next scan, or click "Refresh" to re-detect them (e.g. after adding a library to a new drive in Steam).
- **EA** can only find games installed to EA's own default install folders. A game moved to a custom location won't be found - see the note under EA's toggle for details.

Turning an integration off only stops *new* games from that platform being detected. Games already in your library stay put and keep launching through their launcher - so when you turn a toggle off, TrayTrigger asks whether you'd also like to remove that platform's games from the library. Either way, installed game files are never deleted.

Dropping a game folder or exe onto the Library, or using "Add Folder", also recognizes launcher-owned installs regardless of these toggles: a folder inside a Steam or GOG install is imported as that platform's game, not as a plain local exe.

## Keeping the launcher minimized

"Keep game launchers minimized when launching a game" (Settings > General > Window & Tray Icon, on by default) asks each launcher that supports it to start quietly, so only the game opens. It works whether or not that launcher's integration toggle is on, because games already in your library keep launching through their launcher either way.

- Steam: when Steam isn't running, it starts minimized to the tray and launches the game in the same step. An open Steam is left as it is; its normal launch doesn't bring its window up.
- Epic Games Launcher: the launch asks Epic for a silent start.
- GOG Galaxy: when Galaxy isn't running, it's started in the background with the game, the same way Playnite starts it. When Galaxy is already open, the game's own exe is started instead, so Galaxy isn't involved.
- The EA app and Ubisoft Connect have no way to start quietly, so their windows are left as they are.
- Xbox games start through Windows without opening the Xbox app. Battle.net is left alone: its launch keeps re-sending until Battle.net has signed in, and its fallback opens Battle.net's Play tab for you.

## Scan Locations

- Add any folder yourself with "+ Add a Scan Location" - a folder like "D:\Games" that holds many game subfolders. Every scan location is treated as a library root: each of its immediate subfolders is checked for its own game, not just the folder itself.
- Uncheck a location to skip it on the next scan without removing it, or click Remove to drop it entirely. A toast at the bottom offers Undo for ten seconds.

## Running a scan

- Click "Scan for Games" (Library page or here in Settings) for a single-click scan: it runs immediately, with no extra prompts.
- If it finds one or more new games, the New Games Found picker opens so you can choose which to add - even a single result goes through that picker, since a scan can just as easily turn up several at once.
- If nothing new turns up, or no integrations or scan locations are configured, you just get a status message - no dialog.
- Turn on "Automatically scan for new games on startup" to run this quietly every time TrayTrigger opens. It stays quiet unless it actually finds something new.

## Ignoring a false positive

Folder scanning uses heuristics, so it can occasionally offer something that isn't really a game (a bundled tool, an installer, a benchmark). Click "Ignore" next to a candidate in the New Games Found picker to permanently exclude that exact file from every future scan. Ignored items are listed under "Ignored Games" here, where you can remove one to let it be detected again (Undo on the toast puts it back).

> Steam, GOG, EA, Epic, Ubisoft, Xbox, and Battle.net games can be ignored too, by their own platform ID rather than file path - useful for something registered as a "game" that isn't really one (a soundtrack, an SDK, a demo).

## Ignoring a whole folder

A scan location often holds folders that aren't games: Utilities, Mods, an emulator's bios folder, a benchmark. Ignoring their exes one at a time means one entry per file, redone whenever a tool renames itself. "+ Ignore a Folder" under Ignored Games keeps the scanner out of that folder and everything under it, whatever it contains later. It applies to Scan for Games, Add Folder, and a folder dropped on the window. A scan location itself can't be ignored; untick or remove it instead.

## Add Folder vs. Scan Locations

The Library page's "Add Folder" button is a separate, one-off way to add games from a folder you don't want to track permanently. If it turns out to hold multiple games, you'll be offered a checkbox to also remember that folder as a Scan Location - so future scans pick up new installs there too, without a separate trip to Settings.
