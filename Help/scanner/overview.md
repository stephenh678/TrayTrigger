# How the Game Scanner works

"Scan for Games" looks for installed games it doesn't already know about - from every launcher integration you've enabled, plus any extra Scan Locations you've added - and prompts you to pick which ones to add.

## Launcher Integrations

Each "Enable X Library Integration" toggle controls whether a scan looks for that platform's games. Games are found from the launcher's own records (Steam's manifests, GOG/Epic/Ubisoft's registry entries, EA's install manifests), so no folder setup is needed.

- **Steam** is the one platform with several possible library folders. They're detected automatically and listed under the Steam toggle; untick one to skip that library on the next scan, or click "Refresh" to re-detect them (e.g. after adding a library to a new drive in Steam).
- **EA** can only find games installed to EA's own default install folders. A game moved to a custom location won't be found - see the note under EA's toggle for details.

Turning an integration off only stops *new* games from that platform being detected. Games already in your library stay put and keep launching through their launcher - so when you turn a toggle off, TrayTrigger asks whether you'd also like to remove that platform's games from the library. Either way, installed game files are never deleted.

Dropping a game folder or exe onto the Library, or using "Add Folder", also recognizes launcher-owned installs regardless of these toggles: a folder inside a Steam or GOG install is imported as that platform's game, not as a plain local exe.

## Scan Locations

- Add any folder yourself with "+ Add a Scan Location" - a folder like "D:\Games" that holds many game subfolders. Every scan location is treated as a library root: each of its immediate subfolders is checked for its own game, not just the folder itself.
- Uncheck a location to skip it on the next scan without removing it, or click Remove to drop it entirely.

## Running a scan

- Click "Scan for Games" (Library page or here in Settings) for a single-click scan: it runs immediately, with no extra prompts.
- If it finds one or more new games, the New Games Found picker opens so you can choose which to add - even a single result goes through that picker, since a scan can just as easily turn up several at once.
- If nothing new turns up, or no integrations or scan locations are configured, you just get a status message - no dialog.
- Turn on "Automatically scan for new games on startup" to run this quietly every time TrayTrigger opens. It stays quiet unless it actually finds something new.

## Ignoring a false positive

Folder scanning uses heuristics, so it can occasionally offer something that isn't really a game (a bundled tool, an installer, a benchmark). Click "Ignore" next to a candidate in the New Games Found picker to permanently exclude that exact file from every future scan. Ignored items are listed under "Ignored Games" here, where you can remove one to let it be detected again.

> Steam, GOG, EA, Epic, and Ubisoft games can be ignored too, by their own platform ID rather than file path - useful for something registered as a "game" that isn't really one (a soundtrack, an SDK, a demo).

## Add Folder vs. Scan Locations

The Library page's "Add Folder" button is a separate, one-off way to add games from a folder you don't want to track permanently. If it turns out to hold multiple games, you'll be offered a checkbox to also remember that folder as a Scan Location - so future scans pick up new installs there too, without a separate trip to Settings.
