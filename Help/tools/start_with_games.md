# Starting and closing tools with your games

Edit Tool asks what to do with the tool when you launch a game: nothing (the default), start it, or close it. Choose "Start it" and TrayTrigger starts the tool just before any game it launches, so a program like MSI Afterburner or SimHub no longer has to start with Windows. Choose "Close it" and TrayTrigger closes the tool before the game instead, and can open it again after: Discord, OneDrive, a browser, whatever you'd rather not have using memory and the graphics card while you play.

## Starting

- It works for games you launch from the library, a hotkey or the tray menu.
- A tool that's already running, however it was opened, is left as it is.
- For a tool with launch arguments, "already running" means a copy started with the same arguments. A tool that runs a script or .jar through javaw.exe, python.exe or AutoHotkey therefore starts even when other programs of that kind are open. The other side of it: an app you gave an option like -m is started again if you opened it yourself without that option, so use the app's own "start minimized" setting instead.
- The tool starts after the game's pre-launch script and before the game. If Windows asks for permission to run it, the game waits for your answer and the launch popup says "Starting (the tool) first". If you decline, the tool stays closed and the game still starts.
- Tick "Wait before starting the game" for a tool that needs a moment to get ready, and set how many seconds, from 1 to 60. Afterburner's on-screen display, for example, can end up in the middle of the screen when the game opens before it's ready. The game only waits when TrayTrigger has just started the tool, not when it was already running, and with several tools it waits for the longest one.
- A tool that starts with games shows the Library's controller icon, in blue, on its card and is listed on the Start with Games tab; one that closes for them shows a red power symbol and is on the Close with Games tab. Each icon says which when you point at it.

## Closing it again

- Tick "Close it when the game exits" as well to close the tool once your last game launched from TrayTrigger has closed. A game you started outside TrayTrigger, from Steam for example, isn't waited for.
- Tools started for a game launched from a web or launcher link are left open. TrayTrigger can't see when it exits, so they're yours to close.
- Only the copy TrayTrigger started is closed. One you opened yourself is left running.
- Programs the tool starts in the background as it starts up, within its first 30 seconds, are closed with it: Afterburner's RTSS in the tray, for example. One with a window of its own, like a browser the tool opened, is left alone, and so is anything you open from the tool later.
- The tool is asked to close, as its own close button would, and ended if it's still running five seconds later.
- What was started for a game is listed on that game's Played row in Activity & History, as "MSI Afterburner started, closes after your last game" until then and "closed after" once it has; a tool that didn't start or wouldn't close is a problem row there, and the line says so too.
- A program that runs as administrator, like MSI Afterburner, can only be closed with your permission, so Windows asks once when the game exits, and the launch popup shows which tool it's for. If you say no, the tool stays open and TrayTrigger doesn't ask again.

## Closing a tool for your games

- Choose "Close it, every running copy" and every running copy of the tool is closed just before any game TrayTrigger launches, whoever opened it. Stutter Check, on the System page, names the apps worth closing on your PC: an overlay app, or a browser or Discord holding video memory.
- The tool is asked to close as its own close button would, and ended if it's still running five seconds later. The game waits meanwhile, and the launch popup says "Closing Discord first". An app whose close button only hides it to the tray, as Discord's does, is ended that way, so the game starts a few seconds later than it would otherwise, and a browser ended that way may offer to restore its tabs next time.
- A program running as administrator can only be closed with your permission, so Windows asks before the game starts. If you say no, the tool stays open and the game still starts.
- Tick "Open it again when the game exits" as well and the tool is started again, the way the Tools page would start it, once your last game launched from TrayTrigger has closed. If you opened it yourself in the meantime, it's left as it is. Tools closed for a game launched from a web or launcher link aren't opened again: TrayTrigger can't see when that game exits.
- Discord, Slack, GitHub Desktop and other apps installed the same way start through an Update.exe in their folder, which is what their Start menu shortcut points at. TrayTrigger follows that to the app itself, so dragging the shortcut onto the Tools page is enough.
- Closing an RGB or hardware monitoring suite (Armoury Crate, iCUE, NZXT CAM, Razer Synapse, MSI Center) rarely helps, because the part that reads your sensors runs as a Windows service and keeps going. Turn off its monitoring features instead.
- What was closed for a game is listed on that game's Played row in Activity & History, as "Discord closed, opens again after your last game" until then and "opened again after" once it has; a tool that wouldn't close, or couldn't be opened again, is a problem row there, and the line says so too.
- To close an app for some games only, or to do something the box can't, set Example-CloseBackgroundApps.ps1 as the game's pre-launch script instead. The Scripts topic has the details.

## What isn't covered

- Only programs can start with games or close for them. For a script, set it as the game's pre-launch script instead. A Store app can't be told apart when it's already running, so starting it again would bring it in front of your game.
- A game started outside TrayTrigger, from Steam for example, doesn't start your tools.
- If TrayTrigger closes while a game is running, the tools it started are left running, and the ones it closed for the game aren't opened again.
- A tool that starts through a small launcher, which opens the real program and exits, can't be followed: TrayTrigger only knows the launcher, so it may start it again at each launch, and can't close what it opened or close it for a game. Point the tool at the program itself. The Update.exe that Discord, Slack and similar apps start through is the one exception, and is followed.
- With Tools turned off in Settings, no tool starts with your games or closes for them.
