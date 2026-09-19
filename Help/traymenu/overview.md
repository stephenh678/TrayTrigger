# How the tray menu works

Right-clicking the TrayTrigger tray icon opens a menu built fresh from your library each time, using these preferences to decide what shows and in what order. Left-clicking the icon shows or hides the window, unless you turn on "Left-click on the tray icon opens the game menu", in which case both clicks open the menu. Double-clicking always opens the window. Pointing at the icon shows what is playing, or, when nothing is, the hotkey that shows or hides the window.

## Icons and size

- Each game shows its own icon. A game with no icon shows its launcher's logo instead (Steam, GOG, Battle.net and so on); only a local game with no icon gets the generic controller.
- "Show game icons in the tray menu" turns the icons off for a text-only menu.
- "Compact tray menu" tightens the rows and shrinks the icons, so a long library fits on screen without scrolling.

## Now Playing

- While a game is tracked (its Performance Profile applied, a post-exit script pending) a Now Playing section appears at the very top with the game and how long it has been running.
- Each entry opens a small submenu: End Session (restore tweaks) puts the profile back and runs the post-exit script without touching the game; Force Close Game kills the game's process first. Anything unsaved in the game is lost on a force close.
- The same two actions are on the game's right-click menu in the library, and in its Game Details, while it shows the PLAYING badge.

## Recent and Favorites

- Recent and Favorites are sections at the top of the menu (not submenus), independent of category grouping - they show even if grouping is off.
- Each has its own maximum count and its own sort order, separate from the rest of the menu.
- Recent is built from games with a recorded last-played time; Favorites from games you've starred in the library. The Favorites section is left out while you have no favorites.

## The rest of the menu

- With category grouping on, every other game is nested under a submenu named for its category, under a Categories heading.
- With grouping off, the remaining games show as one flat list under an All Games heading.
- "Tray menu sort order" controls the order of that flat list (or the order within each category submenu, when grouping is on) - independent of the Recent and Favorites sort orders above.

## When you launch from the tray or a hotkey

- With the window hidden, a game started from the tray menu or its own hotkey shows a small popup near the tray clock: the game's icon and name, and what it's waiting for (Battle.net signing in, for example).
- The popup never takes focus, and clicks pass through it to whatever is underneath. It closes once the game starts, or after 100 seconds if the game never does.
- If the launch fails, the popup shows why and stays until you close it. Its link opens TrayTrigger, or lets you locate a game whose executable is missing.
- Turn it off with "Show a launch popup by the tray icon" under Settings > General > Window & Tray Icon, below the tray icon's left-click option.
- Launches from the open window use the popup too when "Minimize to system tray when launching a game" is on, since the window hides as soon as the game is launched. Otherwise they keep the notice inside the window, unless you tick "Show it on every game launch" under the same setting. A launch error still opens a dialog while the window is in front.

## Tools

- With Tools turned on (Settings > General) and "Show Tools in tray menu" ticked (Settings > Tray Menu), a Tools submenu follows your games. Both are off by default.
- It's one flat list of every tool in "Tools sort order": A to Z, Z to A, or favorites first. Tool categories and favorites don't get sections of their own.
- A tool launched from the tray gets the same launch popup as a game.

## Shortcuts at the bottom

Games Library opens the window on the library, Scan for Games... runs a scan straight away, Settings opens the window on Settings, and Exit TrayTrigger quits the app immediately (unlike the window's Exit button, which asks first).

## What never shows

Hidden games are left out of every part of the tray menu - Now Playing, Recent, Favorites, and the main list - the same as the library grid. Unhide a game (from its right-click menu, or Edit Game) to bring it back everywhere, tray included.
