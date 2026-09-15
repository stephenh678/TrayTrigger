# How Tools work

Tools is an optional page for the programs you use alongside your games: DLSS Swapper, Vortex, MSI Afterburner, Discord, a controller mapper. It's off by default. Turn it on with "Enable Tools" under Settings > General, and a Tools button appears in the sidebar under Library.

A tool is just a saved shortcut. Nothing is scanned, looked up online or fetched, and none of the game features apply: no Performance Profile, no scripts, no playtime and no launcher detection.

## Adding a tool

- Drop a program (.exe), a script (.bat, .cmd, .ps1), a shortcut (.lnk), or an app dragged from Browse Apps anywhere on the Tools page, or use Add Tool. You can add several at once.
- Only programs, scripts and Store apps are accepted. Web links and shortcuts that point at anything else are refused, and so are files on a network drive.
- The name comes from the file name, without " - Shortcut". Rename it from its right-click menu or Edit Tool.
- A shortcut brings its arguments, working folder and its "Run as administrator" setting with it.
- Adding a program with the same arguments as a tool you already have asks first. The same program with different arguments is a different tool.
- Adding a tool while a category tab is selected puts it in that category.
- Dropping a file on the Tools page never adds it to your game library.

## Store apps

- Apps from the Microsoft Store, like Xbox or Windows Terminal, can be tools too. Click Browse Apps next to Add Tool (it opens Windows' list of every app in the Start menu, shell:AppsFolder), then drag the app onto the Tools page. A desktop shortcut made by dragging an app out of that folder works too.
- The name is the app's name as Windows shows it (or the shortcut's file name).
- A Store app is started the way its Start menu entry starts it. If it's already open, most apps come to the front; apps that allow several windows, like Windows Terminal, open another one.
- Edit Tool shows the app's ID instead of a program path. Store apps have no working folder or file location and can't run as administrator, so those options aren't shown for them, and a batch Run as Administrator leaves them out.
- Launch arguments are passed on, though most Store apps ignore them.
- If the app is uninstalled, the tool shows MISSING. Install it again from the Microsoft Store, or remove the tool.
- A desktop app dragged from shell:AppsFolder is added as the program its Start menu entry starts, like any other program.
- Games aren't tools. A Game Pass or Microsoft Store game (or its .exe), or a Steam game's shortcut, dropped here isn't added: Scan for Games puts it in your Library instead.

## Scripts

- A .bat or .cmd script runs in Command Prompt, and a .ps1 script in Windows PowerShell. TrayTrigger always starts them that way, never with whatever program opens those files on your PC.
- PowerShell scripts run with the execution policy bypassed, as game scripts do. A script policy set by your organization still applies.
- Launch arguments go to the script. For .bat and .cmd they reach Command Prompt exactly as typed.
- The script's window shows while it runs and closes when it finishes. Put pause at the end of the script to keep it open.
- Tick "Hide Window" in Edit Tool for a script that just does something quietly. What it prints goes to TrayTrigger's log, except for a script that also runs as administrator. A hidden script that waits for input (pause, Read-Host) keeps running unseen until you end it in Task Manager.
- Launching a script that's still running doesn't start it again; you get a "still running" notice instead.
- Run as Administrator works for scripts too.
- A .bat or .cmd script whose path contains a % sign isn't accepted, because Command Prompt would change the path.

## Categories, favorites and sorting

- Tools have their own categories, separate from game categories. Set one with Change Category... or in Edit Tool; the picker suggests the categories your tools already use.
- The tabs are All Tools, Favorites, then each category A to Z. A category tab disappears when its last tool leaves it.
- Click the star on a tool to make it a favorite.
- Sort the page A to Z, Z to A, or favorites first. Search matches a tool's name, category, program path or Store app ID.
- Three views: Large Icons, Small Icons and List. The page remembers the view, sort and tab you last used.

## Changing several tools at once

- Ctrl+click or Shift+click tools to select several, or press Ctrl+A to select every tool on the page.
- Click anywhere that isn't a tool, or press Esc, to clear the selection. Changing the search or the category tab clears it too, so a tool you can no longer see is never changed by mistake.
- Right-click any selected tool for the batch menu: add or remove them from Favorites, change their category, or turn Run as Administrator on or off for all of them.
- Remove from Tools (or the Delete key) removes every selected tool after one confirmation.
- The favorite and Run as Administrator items add or tick all of them, unless every selected tool already has it, in which case they remove or untick it for all.

## Launching

- Double-click a tool, press Enter (or Ctrl+Enter, as for a game) on it, use its hotkey, or pick it from the tray menu.
- A tool gets the same launch notice and launch popup as a game, and starts after the same short delay. The popup settings under Settings > General cover games and tools together.
- If the tool is already running, its window comes to the front instead of a second copy starting. A tool that only lives in the system tray has no window to show, so you get a short "already running" notice instead.
- For a tool with arguments, "already running" means the copy TrayTrigger started for that tool. Two tools that share a program, like Command Prompt running two different scripts, each start their own copy.
- Pressing a tool's hotkey twice in a row doesn't start it twice.
- If the program has moved or been uninstalled, the tool shows MISSING and launching it offers to locate the program.

## Run as administrator

- Tick "Run as administrator" in Edit Tool for a program that needs admin rights but doesn't ask for them itself.
- Windows shows its own permission prompt each time. TrayTrigger never answers or skips it.
- Programs that always need admin, like MSI Afterburner, get that prompt even with the box unticked.
- If you decline the prompt, the tool just stays closed. Nothing else is shown.

## Hotkeys

- Give a tool a global hotkey in Edit Tool. It's recorded the same way as a game's, and a combination a game, the window hotkey or another app already uses is refused.
- While Tools is turned off, tool hotkeys don't work, but they stay reserved so a game can't take one in the meantime.

## In the tray menu

- Tick "Show Tools in tray menu" under Settings > Tray Menu to add a Tools submenu after your games. It's off by default, and the option only appears while Tools is on.
- The submenu is one flat list of every tool, with its own sort order setting. Categories and favorites aren't shown there.

## Turning Tools off

Unticking "Enable Tools" hides the Tools page and the tray submenu and turns off tool hotkeys. Your tools are kept in tools.json next to your game library, so turning it back on brings every tool back as it was. Resetting settings to defaults turns Tools off but never deletes your tools.

> Removing a tool only removes TrayTrigger's shortcut to it. The program itself is never touched.
