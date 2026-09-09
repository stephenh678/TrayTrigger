# How the Game Scanner works

"Scan for Games" looks in a list of Scan Locations for installed games it doesn't already know about, and prompts you to pick which ones to add. Everything it needs lives in this one card in Settings.

## Scan Locations

- Steam library folders are added and kept up to date here automatically, as long as "Enable Steam Library Integration" is on. Click "Refresh Steam Locations" to re-detect them on demand (e.g. after installing Steam to a new drive).
- Add any other folder yourself with "+ Add a Scan Location" - a folder like "D:\Games" that holds many game subfolders. Every scan location is treated as a library root: each of its immediate subfolders is checked for its own game, not just the folder itself.
- Uncheck a location to skip it on the next scan without removing it, or click Remove to drop it entirely. Removing an auto-managed Steam location isn't permanent - it reappears the next time Steam locations sync, unless you also turn off Steam integration.

## Running a scan

- Click "Scan for Games" (Library page or here in Settings) for a single-click scan: it runs immediately, with no extra prompts.
- If it finds one or more new games, the install picker opens so you can choose which to add - even a single result goes through that picker, since a scan can just as easily turn up several at once.
- If nothing new turns up, or no scan locations are configured, you just get a status message - no dialog.
- Turn on "Automatically scan for new games on startup" to run this quietly every time TrayTrigger opens. It stays quiet unless it actually finds something new.

## Ignoring a false positive

Folder scanning uses heuristics, so it can occasionally offer something that isn't really a game (a bundled tool, an installer, a benchmark). Click "Ignore" next to a candidate in the install picker to permanently exclude that exact file from every future scan. Ignored items are listed under "Ignored Games" here, where you can remove one to let it be detected again.

> Steam games can be ignored too, by Steam AppId rather than file path - useful for a Steam "app" that isn't really a game (a soundtrack, an SDK, a demo).

## Add Folder vs. Scan Locations

The Library page's "Add Folder" button is a separate, one-off way to add games from a folder you don't want to track permanently. If it turns out to hold multiple games, you'll be offered a checkbox to also remember that folder as a Scan Location - so future scans pick up new installs there too, without a separate trip to Settings.
