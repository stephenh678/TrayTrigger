# Where game info comes from

The Game Details window (click a game card) shows the developer, publisher, release date, synopsis, genres, play modes and scores for a game. That text can come from one of two sources.

## Steam

Used for any game linked to a Steam App ID. Steam is the richer source: it alone has the review summary, PC requirements and patch notes. Games from GOG, EA, Epic and Ubisoft are usually on Steam too, so they get Steam info even though they launch through their own client.

## RAWG

RAWG (rawg.io) is a community games database that covers titles Steam doesn't list: Game Pass exclusives, Epic exclusives, Roblox, Fortnite, console ports and many indies. When enabled, it supplies developer, publisher, release date, synopsis, genres, play modes, Metacritic, an ESRB rating, RAWG's own community rating, and an "Open on RAWG" button to the game's rawg.io page.

- Turn it on in Settings › Library with "Use RAWG for non-Steam game info" and paste a free API key from rawg.io/apidocs.
- Until RAWG is enabled, the details window has no source switch and every game shows Steam.

## The Steam | RAWG switch

With RAWG enabled, a Steam | RAWG switch sits above the "About the Game" section of the details window.

- Games with no Steam App ID open on RAWG automatically. Games with one open on Steam.
- Click either side to switch. The choice is remembered for that game and used the next time you open its details.
- The window title, text fields, play-mode chips and score badges follow the selected source, and so does the store button: "Steam Store" while Steam is selected, "Open on RAWG" (the game's rawg.io page) while RAWG is selected. Steam-only sections (review summary, PC requirements, news) hide while RAWG is selected.
- Cover art and sorting never change with the switch.

## What RAWG adds to the library

- Category. When "Automatically categorize games from store genres" is on, a game Steam cannot categorize gets RAWG's main genre instead: one with no Steam listing, one whose Steam page has been delisted, or one whose Steam page lists no genre. This only fills a game that is still Uncategorized; a category you set yourself is never overwritten. Enabling RAWG runs this pass over the library straight away.
- Background enrichment. Games with no Steam App ID are matched to RAWG in the background, the same pass that matches Steam games, so categories fill in without opening each game. Right-click › Refresh metadata re-fetches a game's RAWG entry.
- Poster art. RAWG's official title is used as the SteamGridDB search term for games with no Steam poster, which finds art the scanner's folder name often misses.
- Ambient art. While RAWG is selected, its screenshot is the blurred backdrop behind the poster, and stands in for a game that has no poster at all. It is never saved as the poster.

## Matching and Change match

Both sources are searched by the game's name, and the result whose title is closest to that name is kept, subject to the confidence threshold in Settings. The match is remembered so later opens skip the search.

- The line above "About the Game" shows which Steam listing or RAWG entry the game matched.
- If it matched the wrong game, or nothing was found, click "Change match", search by title, and pick the right entry. The pick is remembered for the game.
- Changing the Steam match sets the game's Steam App ID, so its title details, genre and poster art follow the new listing. This is the same as Fetch by ID in Edit Game. How the game launches does not change.
- Changing the RAWG match updates the RAWG text and, if the game is still Uncategorized, its category.

## How fresh the info is

Both sources are saved to disk beside the poster cache, so Game Details opens instantly and works offline. The line above "About the Game" shows when the copy on screen was last fetched, with a Refresh link that fetches it again right away.

- Settings › Library › "Refresh game info" sets how old a saved copy may get before the window quietly fetches a new one in the background. The default is every 3 days. "Every time details open" is the old behaviour; "Never" leaves it to you.
- Steam news, review counts and RAWG ratings change over time, so a longer interval means fewer requests and slightly older numbers. Text, genres and art rarely change.
- If a background refresh fails (offline, quota, bad key) the saved copy stays on screen.
- Right-click › Refresh metadata and Change match always fetch fresh info.
- "Clear Cached Game Info" under the setting forgets every game's saved details. Posters are kept.

> RAWG needs a free API key from rawg.io/apidocs. The key is stored encrypted in your settings file and only sent to RAWG. RAWG's terms require the "Data from RAWG" link shown under the synopsis whenever their data is on screen.
