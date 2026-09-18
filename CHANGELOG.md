# Changelog

Notable, user-facing changes per stable release. The release workflow copies the matching
section into the GitHub Release as its **Highlights**, above the full commit list, so keep
each entry short and written for players, not for the git log.

Format: one `## x.y.z` heading per stable version (no `v` prefix), then a few bullets.
Pre-release builds (`-beta.N`, `-rc.N`) do not get their own section; they roll up into the
stable version they lead to.

## 1.4.5

- **Safer elevated actions** — the Defender exclusion, System Restore point and "needs administrator" tweaks now start Windows' own tools by their full path, lock the temporary registry file until Windows has read it, and handle folders with a curly apostrophe in the name (Assassin’s Creed), which used to make the Defender exclusion fail.
- **Stricter update checks** — the downloaded installer must be a TrayTrigger installer as well as carry the same signature, a revoked certificate is refused, and the file is checked again at the moment it is started.
- **Game scripts** — batch scripts no longer have a percent sign in the game's path expanded by Windows, a game whose name starts with a dash no longer breaks a PowerShell script, and a post-exit script run because TrayTrigger is closing is told the real playtime instead of 0.

## 1.4.4

- **Tools, for the programs you use alongside games** — a new optional Tools page for things like DLSS Swapper, Vortex or MSI Afterburner. Turn it on in Settings > General (it's off by default), then drop a program or its shortcut on the page. Tools get their own categories, favorites, batch editing (select several to change their category, favorite or Run as administrator at once), sort order, three views (large icons, small icons, list), hotkeys and the same launch popup as games, with none of the game features: nothing is scanned or looked up, and no profile, scripts or playtime. "Run as administrator" is a checkbox that lets Windows ask for permission as usual, and a shortcut's own setting comes across. An optional Tools submenu in the tray menu has its own sort order, under Settings > Tray Menu. Microsoft Store apps like Xbox work too: click Browse Apps next to Add Tool and drag the app onto the page. Scripts (.bat, .cmd, .ps1) can be tools as well, with an option to run them with no window.
- **Launch notice stays up long enough to read** — pressing Play, a game's hotkey or a tray menu entry now shows the "Launching..." notice or launch popup for 2 seconds before the game starts, up from under a second, so a game that goes fullscreen straight away no longer covers it before you see it.

## 1.4.3

- **Search in Settings, About and System & Performance** — a search box sits beside the tabs: type a word ("hotkey", "HDR", "log") and the tab you're on keeps only the cards that mention it, with the word highlighted and the first match scrolled into view. All searches every section, and the search stays as you switch tabs. Ctrl+F jumps to the box; its x or Esc clears it.
- **A launch popup for hotkey and tray launches** — starting a game from its hotkey or the tray menu while TrayTrigger is hidden used to show nothing until the game appeared. A small card near the tray clock now shows the game, what it's waiting for (Battle.net signing in, for example), and closes once the game starts. It never takes focus and clicks pass through it. If the launch fails, the reason shows there instead of in a dialog nobody could see, with a link to open TrayTrigger. Turn it off, or have it show on every game launch, in Settings > General > Window & Tray Icon.
- **Launchers stay minimized while your game starts** — new "Keep game launchers minimized when launching a game" option in Settings > General > Window & Tray Icon, on by default. Steam starts minimized to the tray when it isn't already running (before, only for games set to close the launcher after exit), GOG Galaxy starts in the background when it isn't already running, and Epic keeps its silent launch. The EA app and Ubisoft Connect have no way to start quietly, so they're left as they are.
- **Games come to the front sooner** — a game started through its launcher (EA app, Ubisoft Connect, GOG Galaxy, Epic, Xbox, Battle.net) could open behind TrayTrigger for several seconds while TrayTrigger confirmed it was the game. It now gets focus as soon as its window appears. With "Minimize to system tray when launching a game" on, TrayTrigger also hides as soon as the game is launched instead of waiting for its window, and the launch popup shows the launch in its place.
- **System & Performance reads more clearly** — tweak groups and profiles have proper headings with a count ("8 tweaks · 6 optimal"), the sections say which settings are always on and which apply per game while it runs, groups are named for what's in them (Display & Input, Network & Background Activity, Security), and the power plan that appears in both is labelled "(always on)" or "(profile)". Every tweak now has one name, the same on the page as on its Learn more page; Windows Gaming Utilities waits for the specs to load, and the "Live Hardware Telemetry" label only shows where the live readings are.
- **TrayTrigger looks like TrayTrigger** — the name is written with "Trigger" in the logo's blue in the sidebar, About and the welcome screen, and the app icon now greets you on an empty library and in the update prompt.
- **Xbox games whose files are gone are no longer imported** — when an Xbox app game's folder is deleted or its drive is removed without uninstalling the game, Windows still lists it as installed. Scan for Games used to import those with no icon, and Play only brought up the Xbox app's "can't be found" error after applying the game's performance profile. They're skipped now, and one already in your library says it's no longer installed as soon as you press Play. Open Containing Folder opens an Xbox game's real folder, and no longer lands in Documents for one whose files are gone.
- **Settings, System and About tabs jump to the top** — picking a tab (or clicking the one you're on) now starts at the top of that section instead of wherever you'd scrolled to. Section headings now use the same names as their tabs.
- **Empty Favorites and empty library say so again** — the Favorites tab with nothing starred, and a brand-new library, showed a blank box instead of "No favorites yet" or the Scan for Games prompt. Broken since 1.3.7.

## 1.4.2

- **Battle.net library integration** — Scan for Games now finds games installed through Battle.net (World of Warcraft, Overwatch, Diablo, Hearthstone, StarCraft, Call of Duty and the rest) on any drive, with their real titles, and launches them through Battle.net. If Battle.net is closed, TrayTrigger starts it and keeps asking until the game opens, so one click works even while Battle.net is still signing in. Each game's launch code is read from Battle.net itself and saved, so there's no list to go out of date.
- **Better game names from folders** — titles written without spaces (BALLxPIT), acronyms run into a word (ACBlackFlag), packaged Unreal games (Gothic 1 Remake, Persona 3 Reload), and folders with an extra trailing word now match the right game.
- **Tray menu** — game icons now show on the first open after startup (they used to appear only after a launch). Rows are tighter, in line with the app's own menus, and a game with no icon shows its launcher's logo instead of a generic controller. New in Settings: turn icons off, a compact layout for a long library, and left-click on the tray icon to open the game menu instead of the window.
- **Hotkeys are recorded, not typed** — click the hotkey box in Edit Game or Settings and press the keys. A combination another game, the window hotkey, or another app already uses is refused on the spot with the reason, instead of being saved and quietly never working. Shortcuts Windows itself needs (Alt+F4, Alt+Tab, Win+anything) are refused too, and a single modifier plus a letter (Ctrl+S) gets a warning first, since it would stop that shortcut working in every app.
- **A diagnostic report worth attaching** — About > Copy Diagnostic Info now reports the install type, Windows build, CPU, GPU and RAM, each launcher's integration and whether its client is installed, library counts by platform, the settings that matter to bugs, applied tweaks, any running session, and the last warnings from the log. API keys are never included. Save Report... writes the same as a file.
- **Settings regrouped by what things do** — General has one Window & Tray Icon card (startup, minimize on launch, the icon, left-click, the show/hide hotkey); the Library tab's Scan for Games card holds the scan button, auto-scan on startup, scan locations and ignored games together; the Tray Menu card is split into what it lists and how it looks.
- **Edit Game reads top to bottom** — four cards instead of six: Identity & Library (title, category, hidden, store link), Launch (which client starts it, then the path, folder, arguments, Run as Administrator and hotkey), Performance, and Scripts. The launcher options that used to sit at the bottom now come first, above the fields they govern.
- **"Always show icon in tray" now takes** — the request is retried while Windows is still registering the icon, the icon is re-added so Windows picks the change up without an Explorer restart, and Settings shows what Windows did with it instead of saying nothing.
- **Right-click menus show their state** — the Performance Profile tier, CPU Cores choice, Run as Administrator and Close Launcher After Game Exits carry a check mark for the current setting, for one game or a selection. The game you right-click stays highlighted while its menu is open.
- **Ignore a folder** — Settings > Game Scanner can now keep a whole folder out of every scan (a Utilities, Mods or emulator folder inside a scan location), and the Ignored Games list is always shown so you can see what's excluded.
- The Steam and Battle.net client folders are no longer offered as games when a scan location contains them, or when one of them is dropped on the window.
- Game names no longer keep ®, ™ or © ("Overwatch®" is now "Overwatch"). Names already in your library are cleaned the next time TrayTrigger starts.
- **EA app is detected again** — the EA app registers its launch handler without quotes around its path, and TrayTrigger only read the quoted form, so it reported the EA app as not installed and launched EA games by their own exe instead of through the client (no EA sign-in, no cloud saves). Both forms are read now.
- Force-closing a Battle.net game that was moved with Battle.net's own Move Install now finds it, and a Battle.net game that has been uninstalled says so instead of quietly doing nothing.
- Titles containing "Steam" or "GOG" ("Steam Tactics", "Full Steam Ahead") are no longer searched with that word removed; it's only dropped as a trailing store tag ("Portal.2.Steam").

## 1.4.1

- **Library filters** — a filter flyout beside the sort box narrows the library by launcher, performance profile, and state (never played, favorite, missing executable, has a hotkey, has scripts, runs elevated). A count badge shows how many filters are active, and an empty result explains itself instead of looking like an empty library.
- **Quick settings in the right-click menu** — CPU Cores, Run as Administrator, and Close Launcher After Game Exits are now one click away for a single game or a whole selection. The single-game menu is grouped into Change and Steam submenus instead of a 16-item flat list.
- **Unmute Speakers While Playing** — new opt-in Optimized tweak that clears a muted playback device on launch and puts the mute back on exit.
- **HDR fixes** — Enable HDR no longer blanks displays that were already in HDR when the game started, and no longer fires at wide-color-gamut displays that have no HDR mode.
- **Poster cards zoom on hover**, Xbox-dashboard style.
- **Beta builds track betas** — a pre-release build now sees newer pre-releases even with the beta toggle off, so testers are never stranded on an old beta reporting "up to date".
- **Fewer disk writes** — launching or exiting a game no longer rewrites settings.json, and a multi-launcher scan commits once instead of once per platform.
- **Per-user install** — the installer now installs for the current user only and clears any old all-users registration; uninstall removes everything the app leaves behind.
- **Better logs** — every line is timestamped, each session is stamped with its build, and two running instances no longer overwrite each other's log.
- **Scanner accuracy** — Ubisoft Connect titles use the folder name the launcher trusts, short names like R6 and P3R keep their digit, VR editions count as their own game, and the Downloads folder is no longer walked for games.
- RAWG metadata is off by default again (like SteamGridDB); enter a key in Settings to turn it on.

## 1.4.0

- **Per-game scripts** — attach your own `.bat`, `.cmd`, `.ps1`, or `.exe` to any game as a pre-launch or post-exit script, with Test Run buttons, per-game arguments, hidden or elevated execution, and a scripts folder that ships with blank templates, five real-world examples, and a README.
- **Default scripts** — set a script once in Settings and it runs for every game that has no script of its own, with a per-game opt-out. Off until you enable it.
- **Change Match** in the context menu — re-pick which Steam or RAWG entry a game matches when the automatic match was wrong.
- **Batch selection count** shown in the context menu when several games are selected.
- A failed decrypt no longer wipes a stored API key.
- Sidebar and title bar polish: a visible edge on the rail, title bar colors derived from the palette.
