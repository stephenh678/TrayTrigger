# How titles are detected

When you add a game from an exe, a shortcut, or a folder scan, TrayTrigger has to guess its proper name. These settings control that guess.

## The local guess

- The exe's own metadata often carries a product name. If it does, that is used first.
- Otherwise the name comes from the file name or the parent folder, cleaned up: version numbers, "x64", "Shipping", and similar tokens are stripped, and capitalisation is normalised.
- Prefer executable over folder name decides which of those two wins when they disagree. Folders are often better for games installed by a launcher; exe names are often better for portable or hand-installed games.

## Online title search

- With Search official game titles online on, the local guess is sent to the Steam store search and the best match provides the official title and Steam App ID. That also unlocks posters and store metadata.
- Matching sensitivity sets how close the match must be. Higher is stricter and makes fewer wrong matches but leaves more games unlinked. Lower matches more games but risks a wrong one, especially for short or generic names.
- Only the guessed title is sent. No file paths or other information leave your PC.

## Categories from store genres

With Automatically categorize on, a newly added game that matched a Steam listing is placed in a category named after its primary genre, such as Action or Strategy, instead of Uncategorized. Existing games are not changed.

## Fixing a wrong result

Open Edit Game. Type the correct name, or enter the Steam App ID and click Fetch by ID to pull the official title and art. Fetch name from exe re-runs the local guess.
