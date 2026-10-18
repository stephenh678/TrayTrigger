# Where game info comes from

The Game Details window (click a game card) shows the developer, publisher, release date, synopsis, genres, play modes and scores for a game. That text can come from one of two sources.

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

The **PCGamingWiki** button in the details window opens the game's page on pcgamingwiki.com, where the PC fixes are: ultrawide and FOV support, frame cap and save game location. A Steam game opens its page directly; any other game opens the wiki's search for its name, which lands on the page when the title matches. It shows for every game and does not follow the Steam | RAWG switch. The button itself downloads nothing: the link opens in your browser.

### The PC facts card

Tick **Show PC facts from PCGamingWiki** in Settings > Library & Art and the details window gets a **PC facts from PCGamingWiki** card, under the PC requirements, with what the wiki's tables say about the game:

- **Display** - ultrawide, multi-monitor, 4K, HDR, 120+ FPS, a field of view setting, borderless windowed, ray tracing and upscaling.
- **Controls** - controller support (and whether it is full or partial), PlayStation controllers and key remapping.
- **Audio and accessibility** - subtitles, closed captions, a color blind mode and whether the sound mutes when the game loses focus.
- **Online** - local play, LAN play, online play (with the number of players) and crossplay.
- **Saves** - Steam Cloud, and where the game keeps its saves. The folder is text to read, written with Windows' own names such as %APPDATA%; nothing is opened or checked on your PC.
- **Technical** - the DirectX, Vulkan and OpenGL versions the game uses.

Each line says **Yes**, **No**, **With a fix or mod** (the wiki's "hackable": the game doesn't have it, but a fix or mod adds it), **Limited** or **Always on**. Point at a line to read the wiki's own note on it. A fact the wiki lists as unknown or not applicable is left out, so a short card means the wiki knows little about the game, and a game the wiki has no facts for has no card.

- **How the page is found.** For a Steam game, the page whose infobox lists the game's Steam App ID. Otherwise the page whose title is exactly the game's name. A near match is never used, because another game's facts are worse than none, so a game the wiki names differently may have no card. The card names the page it read ("From the PCGamingWiki page Minecraft: Java Edition") so you can tell which edition you are looking at.
- **What is sent.** When you open a game's details, TrayTrigger asks pcgamingwiki.com about that game, sending its Steam App ID or name and nothing else. The answer is kept on disk for two weeks (three days when the wiki had nothing), so a game is asked about once in that time. When a saved answer is older than that, it still shows while a newer one is fetched, and a short "Loading PC facts" line stands in while there is nothing to show yet. Removing a game from the library forgets its saved answer. Refresh in the details window asks again (if the wiki can't be reached, the card stays as it was), and Clear Cached Game Info under Library & Art forgets every saved answer. If the wiki can't be reached the card is missing and the next open tries again; if it asks TrayTrigger to slow down, lookups pause for ten minutes.
- **It is off by default.** There is no account and no key to set up. Turn it off again in the same place and the card disappears.
- **The facts are PCGamingWiki's**, shared under the Creative Commons Attribution-NonCommercial-ShareAlike 3.0 license, which is why the card links to the page it came from.

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
- Changing the Steam match sets the game's Steam App ID, so its title details, genre and poster art follow the new listing. This is the same as Fetch by ID in Edit Game. How the game launches does not change.
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
