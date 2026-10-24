# What's new

The short, player-facing notes the "What's new" window shows once after an update: one line per
feature, no internals. The full write-up for each release stays in CHANGELOG.md. Add a
`## x.y.z` section here for every stable release (betas use their stable version's section).

## 1.7.1

- **Activity & History in a backup** - Back Up has a tick, on by default, to include your history, so it comes back after a restore.
- **Automatic backups** - Tick Back up automatically beside Back Up and TrayTrigger backs up every week (or 2 weeks, or month) and keeps the last few. Off until you turn it on.
- **Stutter Check spots a single memory stick** - If your PC has one stick of memory and room for another, Stutter Check now says so and what to do about it.
- **Stutter Check has its own tab** - On the System page, between Hardware Specs and Performance Tweaks. The All tab still shows it.
- **Launch problems are recorded** - A game that didn't launch, or that was started but never seen running, now leaves an entry in Activity & History with the reason.
- **Resizable BAR in Aggressive now works** - It needs administrator rights, so it joins the Aggressive profile's one Windows prompt at the start and end of a game. If you decline the prompt that puts things back, TrayTrigger asks "Put back now?" when you close it or next start it, instead of a Windows prompt out of nowhere.
- **Display and sound per game** - In Edit Game's new Display & Sound tab, pick a resolution, a refresh rate or both, and with two monitors the monitor, whether it becomes the main display, whether the mouse stays on the game's screen, whether the other screens dim, where the sound plays and which microphone is used. It all goes back when the game ends.
- **Edits and removals are recorded** - Activity & History now notes when you change a game's launch settings in Edit Game, with the old and new values, and when you remove a game or undo it.
- **Favorite star at large text sizes** - On poster cards with a long category, the star is no longer pushed off the card, and the PLAYING tag moves below the middle of the poster so it always has room.
- **Update window fix** - At a large text size, the Download & Install button no longer runs off the edge of the update window.
- **A newer NVIDIA driver stays on the card** - Press Check for Newer Driver and a newer one is remembered, with a Get Driver button in Activity & History, until you install it.
- **Browse opens where your path is** - In Edit Game and Edit Tool, the file or folder window starts at the path you already have.
- **Admin games and .bat launchers** - A game that runs as administrator keeps its profile for the whole session, and a game started by a .bat gets its priority and CPU settings on the game itself.
- **Right-click an Activity row** - Copy it, show only that game, open the game or the log, or remove the entry.
- **Fixes** - Steam games stay Steam games, Play pressed twice launches once, and Scan Locations accept a library root.

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
