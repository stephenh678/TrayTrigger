# Pre-Launch and Post-Exit Scripts

Attach your own script or program to a game and TrayTrigger runs it just before the game starts and again after it exits. Use it for anything TrayTrigger does not do itself: closing Discord, switching audio output, setting an RGB profile, remapping a controller, starting a recorder.

This page covers using scripts. Writing your own script covers the arguments a script receives and the batch and PowerShell details.

## Supported file types

- .bat and .cmd, run through cmd.exe.
- .ps1, run through PowerShell with the execution policy bypassed, so a locked-down policy will not block it.
- .exe and .com, run directly.
- Anything else, such as .py or .ahk, is refused. Wrap it in a one-line .bat instead.

## When they run

- Pre-launch runs after the Performance Profile is applied and before the game starts, for every launch type.
- Post-exit runs after the Performance Profile is restored, so your script sees the machine back in its normal state. It needs an exit signal, which exists for:
  - direct .exe launches (the process itself);
  - Steam games (Steam's own "running" flag for the game);
  - GOG, EA, Epic, Ubisoft, Xbox and Battle.net games launched through their client or directly (TrayTrigger watches the game's install folder for its real process, since the client-registered exe is often only a stub).
- A bare launcher link, meaning a dropped .url or protocol shortcut with no platform ID behind it, has no exit signal. Pre-launch still runs; post-exit is skipped.
- If TrayTrigger closes while a game with a post-exit script is still running, the script runs then rather than never.

## Options

- Wait for the pre-launch script holds the game launch until the script finishes. The timeout box next to it sets how long to wait (1 to 600 seconds, 10 by default), so a stuck script cannot block you forever. Turn waiting off for a script that intentionally keeps running.
- Cancel the launch if the pre-launch script fails treats the script as a precondition: a non-zero exit code, a timeout, or a script that will not start cancels the launch and rolls the Performance Profile back. Turning this on also turns on waiting. Off by default; a failing script is logged and the game launches anyway.
- Run scripts hidden suppresses the console window. A hidden script's output is captured into the TrayTrigger log instead (see Troubleshooting).
- Run scripts as Administrator elevates through UAC, so expect a prompt on every launch.
- Script Arguments is free text handed to both scripts, such as the apps to close or the programs to start. It is how one script serves many games.

## Example scripts

These examples ship with TrayTrigger. Each is one file that handles both phases: in Edit Game, choose it as the pre-launch script and tick "Use the same script for pre-launch and post-exit". Each opens with a comment block covering why you'd want it, how to set it up and what to change.

- Example-CloseBackgroundApps.ps1 asks background apps to close before the game and reopens the ones that closed after it. To close an app before every game, the Tools page does the same without a script: add the app as a tool and choose "Close it" under "When I launch a game" in Edit Tool. The script is for closing an app for some games only, or for apps the box can't follow. Type "recommended" in Script Arguments to close its recommended list (OneDrive, Dropbox and Google Drive), name the apps yourself, such as OneDrive Spotify, or both. An app that ignores the request is left running; add "force", anywhere in Script Arguments, to end it instead. With Script Arguments empty it does nothing. RGB and hardware monitoring software (Armoury Crate, iCUE, NZXT CAM, Razer Synapse, MSI Center) is a common cause of stutter, but closing it rarely helps, because the part that reads your sensors runs as a Windows service: turn off its monitoring features instead.
- Example-StartCompanionApps.ps1 starts the programs whose paths are in Script Arguments, such as SimHub or TrackIR, for the games you choose it for, and asks only the ones it started to close after the game. A program you already had open is left alone, and one that won't close is left running unless you add "force". For a program you want with every game, use Tools instead: choose "Start it" under "When I launch a game" in Edit Tool. Tools can also close a program that runs as Administrator, which a script can't.

## The scripts folder

TrayTrigger keeps a scripts folder at %AppData%\TrayTrigger\Scripts. Open it from the Settings card or from the scripts card in Edit Game. The Browse buttons start there. When game scripts are enabled, TrayTrigger puts the examples there with a README.txt, which also covers writing your own.

These files are TrayTrigger's copy, not yours. Whenever TrayTrigger starts, or you open the folder from it, each one is compared with the copy inside this version and replaced if it differs, which is how a corrected example reaches you. An edit you make to one is therefore undone at the next start. Your version is kept beside it with ".previous" on the end, but it is no longer the file your game runs.

Work on your own file: "New script..." next to each path box creates one from a blank template under a name you choose, fills the path, and opens it for editing. A file TrayTrigger did not put there is never touched. Settings that differ from one PC to the next - where a program is installed, which apps to close - belong in Script Arguments rather than in the file.

"Open in editor" next to a path box opens that script in Notepad or whatever you have associated with editing that type; it never runs it.

## Default scripts for every game

Settings > Launch & Performance can hold a default pre-launch script and a default post-exit script. They run for any game that has no script of its own for that phase, so one "close Discord, restart it afterwards" pair covers the whole library without touching each game.

- Resolution is per phase. A game with its own pre-launch script but no post-exit script runs its own pre-launch and the default post-exit.
- Any game can opt out of both defaults with "Don't run the default scripts for this game" in Edit Game.
- The defaults have their own wait, timeout, cancel-on-failure, hidden, and Administrator options. Those apply whenever a default script runs; a game's own options apply only to its own scripts.
- The defaults have their own Default Script Arguments, used for every game whose Script Arguments in Edit Game are empty. A game with Script Arguments of its own passes those to the defaults instead, so one game can differ. Type "recommended" there to run Close Background Apps for every game. Default Script Arguments are never passed to a game's own script.
- "Run the default scripts" is off by default and sits under the "Enable game scripts" switch, so nothing runs while either is off. Unticking it pauses the defaults without clearing their paths or options.
- Edit Game shows which defaults apply to the game you are editing and why, and the defaults have their own Test buttons in Settings that run them with placeholder game values and the Default Script Arguments.

## Testing a script

Each script field in Edit Game has a Test button. It runs that script right now, the way TrayTrigger will at launch, and shows the exit code and everything the script printed.

- The test uses the values currently typed in Edit Game (name, exe, script path, Script Arguments), not the last saved ones, so you can test before you save.
- Pre-launch tests pass the phase prelaunch; post-exit tests pass postexit with a playtime of 0.
- The test always runs hidden and without elevation so its output can be captured, and it is stopped after 30 seconds. A real run is never stopped.
- If the game is set to run scripts as Administrator or visibly, the result dialog says so: the real run will differ in exactly that way.
- The Test button works even while "Enable game scripts" is off. Clicking it is an explicit request to run the script once.

## Sharing scripts

To share a script you wrote, post it in Show and tell in TrayTrigger's Discussions on GitHub: https://github.com/stephenh678/TrayTrigger/discussions/7. Scripts posted there are not reviewed by TrayTrigger. A script runs as you, so read one before you attach it.

## Troubleshooting

- Every script run is written to the TrayTrigger log under the GameScript category: the path, the options, the exit code, and any failure such as file not found or a cancelled UAC prompt.
- When a script runs hidden (and not as Administrator), everything it prints to stdout and stderr is captured into the same log, prefixed with the game name and phase. Turn on verbose logging in Settings to see stdout; stderr lines are always logged as warnings.
- A script that runs visibly keeps its output in its own console window and nothing is captured.
