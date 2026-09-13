# How the tray menu works

Right-clicking the TrayTrigger tray icon opens a menu built fresh from your library each time, using these preferences to decide what shows and in what order. Left-clicking the icon shows or hides the window, unless you turn on "Left-click on the tray icon opens the game menu", in which case both clicks open the menu. Double-clicking always opens the window.

## Icons and size

- Each game shows its own icon. A game with no icon shows its launcher's logo instead (Steam, GOG, Battle.net and so on); only a local game with no icon gets the generic controller.
- "Show game icons in the tray menu" turns the icons off for a text-only menu.
- "Compact tray menu" tightens the rows and shrinks the icons, so a long library fits on screen without scrolling.

## Now Playing

- While a game is tracked (its Performance Profile applied, a post-exit script pending) a Now Playing section appears at the very top with the game and how long it has been running.
- Each entry opens a small submenu: End Session (restore tweaks) puts the profile back and runs the post-exit script without touching the game; Force Close Game kills the game's process first. Anything unsaved in the game is lost on a force close.
- The same two actions are on the game's right-click menu in the library while it shows the PLAYING badge.

## Recent and Favorites

- Recent and Favorites are sections at the top of the menu (not submenus), independent of category grouping - they show even if grouping is off.
- Each has its own maximum count and its own sort order, separate from the rest of the menu.
- Recent is built from games with a recorded last-played time; Favorites from games you've starred in the library. The Favorites section is left out while you have no favorites.

## The rest of the menu

- With category grouping on, every other game is nested under a submenu named for its category, under a Categories heading.
- With grouping off, the remaining games show as one flat list under an All Games heading.
- "Tray menu sort order" controls the order of that flat list (or the order within each category submenu, when grouping is on) - independent of the Recent and Favorites sort orders above.

## Shortcuts at the bottom

Games Library opens the window on the library, Scan for Games... runs a scan straight away, Settings opens the window on Settings, and Exit TrayTrigger quits the app immediately (unlike the window's Exit button, which asks first).

## What never shows

Hidden games are left out of every part of the tray menu - Now Playing, Recent, Favorites, and the main list - the same as the library grid. Unhide a game (from its right-click menu, or Edit Game) to bring it back everywhere, tray included.
