# Where game info comes from

The Game Details window (click a game card) is the page for one game. It opens on a header with the game's hero banner behind its name (Steam's wide hero banner art, or SteamGridDB's when you have given TrayTrigger a key and Steam has none; a blurred poster until it arrives or if there is none), its store and genres on one quiet line (with a small tag for how Steam's players rated it, such as 91% positive, and a Playing tag while the game runs), how long you have played, when you last played and its performance profile, with a **Play** button, the favorite star and a **...** menu (the store pages, and Remove from library). Under it are two tabs, **Overview** and **Settings**.

- **Overview** is what the game is and what TrayTrigger does for it; see The Overview below.
- **Settings** is where you edit the game: its artwork, title, category, launch options, profile, display and scripts. Everything under "Edit Game" in this help is on this tab. Right-click a game and choose Edit Game Properties, or press Settings, to open it. **Save Changes** writes your edits and keeps the page open; **Cancel** throws them away and starts again from what is saved. Closing the window, Play, or Remove while edits are still open asks before it throws them away.

## The Overview

On the left: the synopsis, the three newest Steam news posts (with a PATCH tag on patch notes and a link to the rest), and last the PC system requirements, with buttons for the minimum and the recommended.

On the right, in this order:

- **Details** is the developer and publisher (one row when they are the same company), release date, the drive the game is installed on and **Size on disk**, then the scores, the play modes and the links: the store page, PCGamingWiki, SteamDB and the developer's website, each only when the game has one. Size on disk is counted on this PC from the game's own folder when the page opens, and left out when the folder cannot be found or is too big to be one game; nothing is sent anywhere. The links open in your browser.
- **Your setup** is one card with a row for each area TrayTrigger changes for the game: Performance (always), DLSS while TrayTrigger holds an override, and Display and sound, Scripts and Launch when something is set for the game. Each row says what it is doing now and has a link to the settings that change it. Anything not listed is at its default: an area left alone has no row, and its tab on Settings is still there. The rows read the game as it is saved.
- **Last session** is the game's most recent session as Activity & History wrote it, kept to three lines (when it ran, the profile, and that everything was put back). **See this game in Activity** closes the page and opens Activity & History with the game's name in the search box, so the list shows just this game. It is left out for a game with no session on record.

The text on the Overview can come from one of two sources. Which one, and how fresh it is, are on the About card: its **Steam** or **RAWG** menu switches the source (RAWG only with a key), refreshes the info now, changes which listing the game is matched to, and opens this help.

## Steam

Used for any game linked to a Steam App ID. Steam is the richer source: it alone has the review summary, PC requirements and patch notes. Games from GOG, EA, Epic, Ubisoft and Battle.net are often on Steam too, so they get Steam info even though they launch through their own client.

## RAWG

RAWG (rawg.io) is a community games database that covers titles Steam doesn't list: Game Pass exclusives, Epic exclusives, Battle.net games such as Hearthstone and StarCraft II, Roblox, Fortnite, console ports and many indies. When enabled, it supplies developer, publisher, release date, synopsis, genres, play modes, Metacritic, an ESRB rating, RAWG's own community rating, and an "Open on RAWG" button to the game's rawg.io page.

- Paste a free API key into the RAWG box in Settings > Library & Art, or in the Welcome when TrayTrigger first opens. Pasting it switches "Use RAWG for non-Steam game info" on. See SteamGridDB and RAWG keys.
- Until RAWG is enabled, the details window has no source switch and every game shows Steam.

## The Steam | RAWG switch

With RAWG enabled, a Steam | RAWG switch sits above the "About the Game" section of the details window.

- Games with no Steam App ID open on RAWG automatically. Games with one open on Steam.
- Click either side to switch. The choice is remembered for that game and used the next time you open its details.
- The window title, text fields, play-mode chips and score badges follow the selected source, and so does the store button: "Steam Store" while Steam is selected, "Open on RAWG" (the game's rawg.io page) while RAWG is selected. Steam-only sections (review summary, PC requirements, news) hide while RAWG is selected.
- Cover art and sorting never change with the switch.

## PCGamingWiki

The **PCGamingWiki** link in the Details card (and the ... menu) opens the game's page on pcgamingwiki.com, where the PC fixes are: ultrawide and FOV support, frame cap and save game location. A Steam game opens its page directly; any other game opens the wiki's search for its name, which lands on the page when the title matches. It shows for every game and does not follow the Steam | RAWG switch. The button itself downloads nothing: the link opens in your browser.

## The anti-cheat tag

After you launch a game, its details window shows an **Anti-cheat: Name** tag beside the play-mode tags when TrayTrigger saw an anti-cheat service run with it (Easy Anti-Cheat, BattlEye, Riot Vanguard and the like), and the game's row in Activity & History says the same. The tag is there so you know which games not to expect tools like Suspend to work with. It comes from the game's last launch, so a game you have not launched since this was added has none yet.

## What RAWG adds to the library

- Category. When "Automatically categorize games from store genres" is on, a game Steam cannot categorize gets RAWG's main genre instead: one with no Steam listing, one whose Steam page has been delisted, or one whose Steam page lists no genre. This only fills a game that is still Uncategorized; a category you set yourself is never overwritten. A new RAWG key runs this pass over the library as soon as it checks out.
- Background enrichment. Games with no Steam App ID are matched to RAWG in the background, the same pass that matches Steam games, so categories fill in without opening each game. Right-click > Refresh metadata re-fetches a game's RAWG entry.
- Poster art. RAWG's official title is used as the SteamGridDB search term for games with no Steam poster, which finds art the scanner's folder name often misses.
- Ambient art. While RAWG is selected, its screenshot is the blurred backdrop behind the poster, and stands in for a game that has no poster at all. It is never saved as the poster.

## Matching and Change match

Both sources are searched by the game's name, and the result whose title is closest to that name is kept, subject to the confidence threshold in Settings. The match is remembered so later opens skip the search.

- The window title shows which Steam listing or RAWG entry the game matched. Point at "Change match" to see the Steam App ID it used.
- If it matched the wrong game, or nothing was found, click "Change match", search by title, and pick the right entry. The pick is remembered for the game.
- Changing the Steam match sets the game's Steam App ID, so its title details, genre and poster art follow the new listing. This is the same as Change match... in Edit Game's Online match card. How the game launches does not change.
- Changing the RAWG match updates the RAWG text and, if the game is still Uncategorized, its category.

## How fresh the info is

Both sources are saved to disk beside the poster cache, so Game Details opens instantly and works offline. The line above "About the Game" shows when the copy on screen was last fetched, with a Refresh link that fetches it again right away.

- Settings > Library & Art > "Refresh game info" sets how old a saved copy may get before the window quietly fetches a new one in the background. The default is every 3 days. "Every time details open" is the old behaviour; "Never" leaves it to you.
- Steam news, review counts and RAWG ratings change over time, so a longer interval means fewer requests and slightly older numbers. Text, genres and art rarely change.
- If a background refresh fails (offline, quota, bad key) the saved copy stays on screen.
- Right-click > Refresh metadata and Change match always fetch fresh info.
- "Clear Cached Game Info" under the setting forgets every game's saved details. Posters are kept.
- Removing a game from the library also deletes its saved details, poster and icon once the ten-second undo has passed. Anything another game in the library still uses, such as a second entry for the same Steam game, is kept.

> RAWG needs a free API key from rawg.io/apidocs. The key is stored encrypted in your settings file and only sent to RAWG. RAWG's terms require the "Data from RAWG" link shown under the synopsis whenever their data is on screen.
