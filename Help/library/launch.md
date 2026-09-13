# How a game is launched

Edit Game's Launch card has two parts: the route at the top (which client, if any, starts the game), then how the executable is run (from where, with what, and as whom). This is what each field does and, just as important, when it is ignored.

## Which route a game takes

TrayTrigger decides how to start a game from what the entry is, not from the path alone:

- **Steam, GOG, EA, Epic, Ubisoft Connect, Xbox / PC Game Pass, Battle.net** - imported by Scan for Games, or linked to a platform record when you dropped its exe. These start through their own client (or, for Game Pass, through Windows), so the client can sign you in, sync saves, and run its overlay. The executable path is kept for the icon and "Open Containing Folder", but the client is what runs the game.
- **Local** - anything else: an exe, a `.lnk` shortcut, or a `.url` file. Runs directly.
- **Launcher links** - a `.url` whose target is a launcher scheme such as `steam://` or `com.epicgames.launcher://` is opened as a link. Only the schemes TrayTrigger knows are allowed; anything else is refused rather than handed to Windows. A bare link has no exit signal, so it supports a pre-launch script only.

## Launcher options

Shown at the top of the card for a platform game; a Local game has none.

- **Imported from / Linked to** - which platform record the entry is tied to and the id the launcher knows it by. Convert to Local game drops the link and turns the entry into a plain exe launch.
- **Launch this executable directly** - for GOG, EA, Epic, Ubisoft and Steam entries: bypass the client and run the executable below, with the arguments and Run as Administrator from the fields below applied. You lose whatever the client provides (sign-in, cloud saves, overlay). Not offered for Xbox (a GDK exe can't run outside its package) or Battle.net (the game needs the sign-in token only the client passes).
- **Close the launcher after this game exits** - when the session ends, the client (Steam, GOG Galaxy, EA app, Epic, Ubisoft Connect, the Xbox app, or Battle.net) is closed, so it doesn't stay resident with its overlay and background processes. For Steam, TrayTrigger also starts the client minimized to the tray when it has to open it, so only the game shows. "Keep game launchers minimized when launching a game" in Settings > General > Window & Tray Icon does this for every game.
- **Launch through the Steam client** - offered for a Local game that has a Steam App ID. Ticking it makes the entry a Steam game, launched with `steam://rungameid/`, and the options above take over. Leave it off for a standalone game that merely shares a Steam listing.

## Executable / Shortcut Target

The file or link to run for a Local game, and the file the icon comes from for every game. For a platform game, editing it changes nothing about the launch unless you also turn on "launch this executable directly" (above). If the file goes missing the card shows a MISSING badge and the right-click menu offers Locate Executable.

## Working Directory

Two jobs:

1. The folder the game is started in, for a direct launch. Blank means the executable's own folder, which is right for almost every game.
2. **The folder TrayTrigger watches** to know the game is running. Client launches (GOG, EA, Epic, Ubisoft, Battle.net) and prelauncher stubs hand off to a process TrayTrigger never started, so it finds the game by looking for a process running from inside this folder. End Session's Force Close uses the same folder to know what to kill. If a game "never appears" after launching and its profile rolls back after three minutes, this folder is usually the wrong one.

Steam games are tracked through Steam's own running flag instead, and Battle.net games through the folder Battle.net currently records for them, so a game moved with Battle.net's "Move install" is still found.

## Launch Arguments

Passed exactly as typed. Where they go depends on the route:

- **Direct launches** - appended to the command line.
- **Steam** - sent through Steam's launch URL, the same as Steam's own Launch Options.
- **Xbox / PC Game Pass** - passed to the app on activation. Most GDK titles ignore them.
- **GOG Galaxy, EA, Epic, Ubisoft Connect** - the client's launch command can't carry them. Set them in the client, or turn on "launch this executable directly".
- **Battle.net** - can't carry them either, and there is no direct option. Set them in Battle.net's game settings.

## Run as Administrator

Starts a direct launch through UAC, so you get the prompt each time. It sits with the arguments because it has the same reach: it applies only when TrayTrigger starts the exe itself, and has no effect on a launch that goes through a client, since the client is what starts the game. Turn on "launch this executable directly" (above) for a platform game that needs elevation.

## Global Launch Hotkey

Click the box and press the keys. The combination is recorded from what you press, not typed, so it is always spelled the same way and always registers.

- Ctrl, Alt or Shift plus a key, or a function key (F1-F24) on its own. Win+ anything is reserved by Windows.
- A combination another game, the show/hide window hotkey, or another program already uses is refused with the reason.
- Shortcuts Windows needs for itself (Alt+F4, Alt+Tab, Ctrl+Esc and the like) are refused.
- A single modifier plus a letter or digit - Ctrl+S - is warned about, because a global hotkey takes that shortcut away from every app while TrayTrigger runs. Press it again to use it anyway; Ctrl+Alt+S or Ctrl+Shift+S is the usual choice.
- Escape keeps the old value; Backspace, Delete or the × clears it.

Hotkeys stay active while the window is hidden, and a hidden game's hotkey still works.
