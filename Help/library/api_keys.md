# SteamGridDB and RAWG keys

TrayTrigger gets posters and game info from Steam with no setup. Two free sites fill in what Steam doesn't have, and each needs a key from a free account. Both are optional.

- **SteamGridDB** (steamgriddb.com) has community poster art: for new releases and indies Steam has no poster for yet, and for games that aren't on Steam at all.
- **RAWG** (rawg.io) has developer, publisher, release date, synopsis and genre for games that aren't on Steam, such as Game Pass and Epic exclusives. Its title for a game also helps SteamGridDB find the right poster.

## Getting the keys

The first time TrayTrigger opens, the Welcome asks for both keys before you add any games. They're also in Settings > Library & Art. Either place works the same way.

1. Click **Get Free Key** beside the site's key box. Its key page opens in your browser.
2. Sign in, or create a free account. On SteamGridDB the key is on the API page of your preferences. On RAWG, click Get API Key; if it asks what the key is for, a short answer such as "TrayTrigger" does.
3. Copy the key, a long run of letters and numbers, and paste it into the box.

Pasting a key switches that source on. Spaces or line breaks copied with it are removed.

## Is the key working?

TrayTrigger checks a key with the site a moment after you paste it, and again whenever you click **Check**. The line under the box says what the site answered:

- **Key works.** You're set.
- **The site didn't accept this key.** It was cut short, has an extra character, or belongs to a different site. Copy it again from the key page.
- **Couldn't reach the site.** You're offline, or the site is busy. The key is kept; click Check again later.

## Adding a key later

If you add a key after games are already in your library, they're caught up as soon as the key checks out:

- **SteamGridDB** runs Refresh All Game Posters: games linked to Steam are checked for better art, and games with no poster are searched by name.
- **RAWG** matches the games Steam doesn't list. With "Automatically categorize games from store genres" on, an Uncategorized game also gets RAWG's genre as its category.

## Privacy

Each key is stored encrypted in your settings file and only sent to its own site. Resetting settings to defaults keeps the keys, still switched on. A backup restored under the same Windows account keeps them too; on a new PC they can't be read, because Windows encrypted them for the old one, so paste them in again.
