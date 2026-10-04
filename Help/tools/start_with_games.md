# Starting tools with your games

Tick "Start when I launch a game" in Edit Tool and TrayTrigger starts the tool just before any game it launches, so a program like MSI Afterburner or SimHub no longer has to start with Windows.

## Starting

- It works for games you launch from the library, a hotkey or the tray menu.
- A tool that's already running, however it was opened, is left as it is.
- For a tool with launch arguments, "already running" means a copy started with the same arguments. A tool that runs a script or .jar through javaw.exe, python.exe or AutoHotkey therefore starts even when other programs of that kind are open. The other side of it: an app you gave an option like -m is started again if you opened it yourself without that option, so use the app's own "start minimized" setting instead.
- The tool starts after the game's pre-launch script and before the game. If Windows asks for permission to run it, the game waits for your answer and the launch popup says "Starting (the tool) first". If you decline, the tool stays closed and the game still starts.
- Tick "Wait before starting the game" for a tool that needs a moment to get ready, and set how many seconds, from 1 to 60. Afterburner's on-screen display, for example, can end up in the middle of the screen when the game opens before it's ready. The game only waits when TrayTrigger has just started the tool, not when it was already running, and with several tools it waits for the longest one.
- A tool that starts with games shows the Library's controller icon on its card, and is listed on the With Games tab.

## Closing it again

- Tick "Close it when the game exits" as well to close the tool once your last game launched from TrayTrigger has closed. A game you started outside TrayTrigger, from Steam for example, isn't waited for.
- Tools started for a game launched from a web or launcher link are left open. TrayTrigger can't see when it exits, so they're yours to close.
- Only the copy TrayTrigger started is closed. One you opened yourself is left running.
- Programs the tool starts in the background as it starts up, within its first 30 seconds, are closed with it: Afterburner's RTSS in the tray, for example. One with a window of its own, like a browser the tool opened, is left alone, and so is anything you open from the tool later.
- The tool is asked to close, as its own close button would, and ended if it's still running five seconds later.
- A program that runs as administrator, like MSI Afterburner, can only be closed with your permission, so Windows asks once when the game exits, and the launch popup shows which tool it's for. If you say no, the tool stays open and TrayTrigger doesn't ask again.

## What isn't covered

- Only programs can start with games. For a script, set it as the game's pre-launch script instead. A Store app can't be told apart when it's already running, so starting it again would bring it in front of your game.
- A game started outside TrayTrigger, from Steam for example, doesn't start your tools.
- If TrayTrigger closes while a game is running, the tools it started are left running.
- A tool that starts through a small launcher, which opens the real program and exits, can't be followed: TrayTrigger only knows the launcher, so it may start it again at each launch and can't close what it opened. Point the tool at the program itself.
- With Tools turned off in Settings, no tool starts with your games.
