# What's new

The short, player-facing notes the "What's new" window shows once after an update: one line per
feature, no internals. The full write-up for each release stays in CHANGELOG.md. Add a
`## x.y.z` section here for every stable release (betas use their stable version's section).

## 2.0.0

- **Amazon Games** - Scan for Games now finds your Amazon Games and launches them through the Amazon Games app.
- **itch.io** - Scan for Games now finds games installed with the itch app, and starts them without the app needing to be open.
- **Sort by any column** - click a column header in the Details List view to sort by it, and again to reverse it.
- **Reverse any sort** - the arrow beside the sort box, in the Library and on the Tools page, turns the order round.
- **Your PC beside the requirements** - Game Details shows your own parts under each of a game's PC requirements, and warns where your PC falls short.
- **Resizable columns** - drag a column's edge in the Details List view to make it wider or narrower.
- **Arrow keys in the Library** - Move between games with the arrow keys, Home, End, Page Up and Page Down, then Enter for details or Ctrl+Enter to play.
- **Controller in the Library** - Turn it on in Settings > Library & Art to pick and play games with a controller or a handheld's controls.
- **See every game's profile** - the Details List view has a Profile column, and you can sort by it.
- **Sort by size** - Size on Disk in the Library's sort box shows which games take the most room.
- **Repair a game** - Game Details' ... menu opens the game in its launcher, where Verify, Repair and Uninstall are.
- **One line for categories and collections** - Collections now sit beside the category tabs. When they don't both fit, drag the divider between them to give either side more room.
- **Tab arrows** - When the tabs don't fit, arrows at each end scroll them.
- **Easier System, Settings and About pages** - Each opens on its first tab and shows one tab at a time, and cards that group settings have a clear heading. Settings has a new Launchers tab for launchers and scanning.
- **On/off switches** - Each tweak and profile tweak has one switch that shows whether it's on. Preset and opt-in tweaks sit under their own headings.
- **Hardware Specs at a glance** - Live readings sit in tiles at the top, and the specs are two to a row.
- **Restore point option moved** - It is now on the Performance Preset card, beside the buttons it applies to.

## 1.8.1

- **Search moved** - The search button is now at the bottom of the left bar, above Activity. Ctrl+K still works.
- **Tools work after an update** - Programs a tool starts no longer fail on folder links after TrayTrigger updates itself.

## 1.8.0

- **Collections** - Group games your own way (Co-op, Kids) from a game's right-click menu > Change > Collections. A row of collections below the category tabs narrows the Library to one.
- **Tools for a collection** - Edit Tool can start or close a tool only for the games in a collection, such as Discord with your multiplayer games.
- **Show category tabs** - Settings > Library can hide the category row if you have a lot of categories.
- **Launch your last game (Ctrl+Alt+L)** - One shortcut plays the game you played most recently, or brings it forward if it's already running. Change it in Settings > General.
- **Game updates don't confuse it** - If a launcher updates a game as it starts, TrayTrigger now waits for the game itself instead of treating the update as the game and ending early.
- **Play from a shortcut or button** - Right-click a game and choose Create Shortcut, or run `TrayTrigger.exe --play "Game"` from a Stream Deck, Task Scheduler or a streaming host. The game starts through TrayTrigger with its profile.
- **Play Without Changes** - Start a game once with none of TrayTrigger's settings, to see whether a problem is TrayTrigger's. It's in the game's right-click menu, or Shift+click the game in the tray menu.
- **Epic's new launcher** - Scan for Games now finds games installed by the new Epic Games Launcher as well as the classic one.
- **Rockstar Games Launcher** - Scan for Games now finds and launches games installed with the Rockstar Games Launcher.
- **Rockstar games on Epic** - Games like Red Dead Redemption 2 that start through another launcher now get their settings on the game itself.
- **Last played stays right** - A game's "Today" or "Yesterday" now updates when the day changes, even if TrayTrigger has been left running in the tray overnight.

## 1.7.7

- **Search everything (Ctrl+K)** - One search for games, tools and settings from any page; pick a result and TrayTrigger takes you there.
- **Find any setting** - The search on Settings, About and System lists matches from every tab and outlines the one you pick.
- **A filter button on Tools** - The same filter as the Library, with saved filters, now on Tools too.
- **Every hotkey in one place** - About > Shortcuts lists each game's and tool's hotkey and opens it to change.
- **Polished all over** - Buttons and menus use the same words everywhere (Put Back for anything TrayTrigger changed), Edit Game only offers Discard Changes when there's something to discard, and pages are tidier and line up the same way.

## 1.7.6

- **Hero banners** - Click a game and its page opens on a big hero banner with the game's name, a Play button, and your playtime, last played and profile. Banners and fresh game info load in as you open each game, so the first visit may take a sec. Overview is the game; Settings is Edit Game right there in the same window. Save keeps you on the page, and Cancel puts everything back how it was.

- **A cleaner Overview** - Game details up top (with size on disk and quick links to the store page, PCGamingWiki and SteamDB), then Your setup and what TrayTrigger did last session and put back. The Steam rating sits right on the hero banner, and the system requirements sit at the bottom.

- **Your art, your call** - The new Artwork tab has the hero banner, poster and icon, each Automatic or Custom: pick a picture or just drop one on the card. Online match replaces Look Up and tells you what a new match will change.

## 1.7.5

- **Display and sound per game** - In Edit Game's new Display & Sound tab, pick a resolution, refresh rate, monitor, sound output or microphone for a game. TrayTrigger sets them when it starts and puts them back when it ends, even after a crash.
- **Backups** - Back Up can include your Activity & History, and Back up automatically (off until you turn it on) keeps the last few for you.
- **A fuller Activity & History** - It records games that didn't launch, edits you make in Edit Game with the old and new values, and removals and undos. Right-click a row to copy it, show only that game, or remove it.
- **Fixes** - Steam games stay Steam games, Play pressed on a running game just brings it forward, a game running as administrator or started by a .bat keeps its profile, Esc on the "Some settings are still changed" prompt stays open, and closing TrayTrigger no longer ends in an error window.
- **Security and reliability** - The updater only runs an installer it has verified, links built from saved game data are checked before they open, and a number of smaller problems that could leave a setting changed or a tool hanging were fixed.

## 1.7.0

- **A new theming engine** - You asked for more control over how TrayTrigger looks, so themes are now far more than a color swap. A theme sets the shape and roundness of tags, buttons, cards and posters, and its own text size and font, on top of its colors. Edit theme has a Corners card, a live preview that slides out beside it and Randomize for a new starting point, and Import theme... adds a theme file made elsewhere. The new Abyssal theme shows what's possible.
- **Text size and font** - Make all of TrayTrigger's text larger or smaller and pick any font you have installed, in Settings > Appearance. A theme can have its own too.
- **Restore Settings Now** - Done tweaking mid-game? Right-click a running game, in the library or the tray, and put Windows back as it was while the game keeps running.
- **Auto HDR per game** - Turn Auto HDR on or off for a single game in Edit Game, or opt in through the Optimized profile.
- **Smarter Stutter Check** - It now spots a missing or tiny paging file, and a stopped AMD V-Cache optimizer on the 7900X3D, 7950X3D and 9950X3D.
- **And more** - An anti-cheat tag in Game Details, a message and a `crash.log` if TrayTrigger can't start, dialogs that remember their size, and no more dropdowns changing as you scroll past them.

## 1.6.2

- **Introducing Themes** - TrayTrigger can now look the way you like. Pick Dark, Light, Green, Amber or Purple, or let Auto follow Windows, in Settings > General > Appearance. Want something of your own? Edit theme... lets you choose the colors and a background picture.
- **Find any game faster** - The library filter does a lot more now. Narrow your games by launcher, by when you last played them, by the settings they use, or by what needs attention, like a missing poster. Tick as many as you like, and the top of the flyout tells you how many games are left. Found a view you like? Save current... names it, so it's one press away next time.
- **PCGamingWiki, one click away** - A game's details window has a new PCGamingWiki button. It opens the game's page on the wiki, where you'll find fixes, settings tips and where its saves are kept. If a game isn't on Steam, it searches the wiki for its name.
- **More control over DLSS Override** - DLSS Override keeps your games on NVIDIA's newest DLSS. Now you can choose how it's set up: use NVIDIA's recommended preset, or pick Default to leave the choice to the game. The card's Last run line also tells you which DLSS features each game picked up.
