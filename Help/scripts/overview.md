# Pre-Launch and Post-Exit Scripts

Attach your own script or program to a game and TrayTrigger runs it just before the game starts and again after it exits. Use it for anything TrayTrigger does not do itself: closing Discord, switching audio output, setting an RGB profile, remapping a controller, starting a recorder.

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
  - GOG, EA, Epic, and Ubisoft games launched through their client or directly (TrayTrigger watches the game's install folder for its real process, since the client-registered exe is often only a stub).
- A bare launcher link, meaning a dropped .url or protocol shortcut with no platform ID behind it, has no exit signal. Pre-launch still runs; post-exit is skipped.
- If TrayTrigger closes while a game with a post-exit script is still running, the script runs then rather than never.

## Options

- Wait for the pre-launch script holds the game launch until the script finishes. The timeout box next to it sets how long to wait (1 to 600 seconds, 30 by default), so a stuck script cannot block you forever. Turn waiting off for a script that intentionally keeps running.
- Cancel the launch if the pre-launch script fails treats the script as a precondition: a non-zero exit code, a timeout, or a script that will not start cancels the launch and rolls the Performance Profile back. Turning this on also turns on waiting. Off by default; a failing script is logged and the game launches anyway.
- Run scripts hidden suppresses the console window. A hidden script's output is captured into the TrayTrigger log instead (see Troubleshooting).
- Run scripts as Administrator elevates through UAC, so expect a prompt on every launch.

## The scripts folder, blank templates and examples

TrayTrigger keeps a scripts folder at %AppData%\TrayTrigger\Scripts. Open it from the Settings card or from the scripts card in Edit Game. The Browse buttons start there, and it is where "New script..." writes. When game scripts are enabled, TrayTrigger puts these files there (missing files only; your edits are never overwritten):

- _Blank.bat and _Blank.ps1: empty templates with the argument contract in comments and a phase branch. "New script..." next to each path box copies one of these to a name you choose, fills the path, and opens it for editing.
- Example-LogSessions.ps1: appends one CSV line per phase. Shows how to read every argument. Touches nothing else.
- Example-ZipFolderAfterExit.ps1: zips a folder named in Script Arguments after the game exits and keeps the newest ten. Does nothing until you give it a folder.
- Example-CloseAndRestartProgram.bat: closes a program named in Script Arguments before the game and restarts it afterwards, only if it was running. Does nothing until you give it a process name.
- README.txt: the same reference as this page, next to the scripts.

Each example is one file that handles both phases, so set the same file as both the pre-launch and the post-exit script. "Open in editor" next to a path box opens that script in Notepad or whatever you have associated with editing that type; it never runs it.

## Default scripts for every game

Settings › Launch & Performance can hold a default pre-launch script and a default post-exit script. They run for any game that has no script of its own for that phase, so one "close Discord, restart it afterwards" pair covers the whole library without touching each game.

- Resolution is per phase. A game with its own pre-launch script but no post-exit script runs its own pre-launch and the default post-exit.
- A game's own script always wins over the default for that phase.
- Any game can opt out of both defaults with "Don't run the default scripts for this game" in Edit Game. It is off for every game, including games added before this option existed, so the defaults apply everywhere the moment you set them.
- The defaults have their own wait, timeout, cancel-on-failure, hidden, and Administrator options. Those apply whenever a default script runs; a game's own options apply only to its own scripts.
- Each game's Script Arguments are passed to the default scripts too. That is how one generic default is parameterised per game.
- The Enable game scripts switch gates the defaults as well: while it is off, nothing runs.
- Edit Game shows which defaults apply to the game you are editing and why, and the defaults have their own Test buttons in Settings that run them with placeholder game values.

## Script arguments

The Script Arguments box in Edit Game is free text appended after the five built-in arguments, for both scripts. It is how one generic script serves many games: the script reads a save folder or a profile name from there instead of having it edited in.

- They start at %6 in a batch file and at $args[5] in PowerShell. In a PowerShell param block, declare the built-in five and then a [Parameter(ValueFromRemainingArguments)] array for the rest.
- For .bat and .cmd the text is passed exactly as typed, and cmd.exe parses it. Quote a value that contains spaces. An unbalanced double quote will break cmd's parsing of the whole command line, so keep quotes paired.
- For .ps1, .exe and .com the text is split into separate arguments with the usual Windows rules: spaces separate, double quotes group, a backslash before a quote escapes it. "C:\My Saves" arrives as one argument with no quotes.
- Test Run passes them too, so the result dialog shows exactly what the script will see.

## Testing a script

Each script field in Edit Game has a Test button. It runs that script right now, the way TrayTrigger will at launch, and shows the exit code and everything the script printed.

- The test uses the values currently typed in Edit Game (name, exe, script path), not the last saved ones, so you can test before you save.
- Pre-launch tests pass the phase prelaunch; post-exit tests pass postexit with a playtime of 0.
- The test always runs hidden and without elevation so its output can be captured, and it is stopped after 30 seconds. A real run is never stopped.
- If the game is set to run scripts as Administrator or visibly, the result dialog says so: the real run will differ in exactly that way.
- The Test button ignores the Settings kill-switch. Clicking it is an explicit request to run the script once.

## Examples

A pre-launch script that closes Discord and starts an RGB profile:

- @echo off
- taskkill /im Discord.exe /f
- start "" "C:\Program Files\OpenRGB\OpenRGB.exe" --profile Gaming

A post-exit script that starts Discord again:

- @echo off
- start "" "%LOCALAPPDATA%\Discord\Update.exe" --processStart Discord.exe

## Optional: reading the game info

Every script works as-is; nothing here is required. If one script should behave differently per game, TrayTrigger passes these details, which the script is free to ignore.

- Arguments, in order: phase (prelaunch or postexit), game name, game exe path, game ID, playtime in minutes. In a batch file those are %1 to %5; in PowerShell, $args[0] to $args[4]. Use %~2 to strip the quotes around a name with spaces. Playtime is empty on pre-launch, so the position never shifts between phases, and is the minute count on post-exit (for every tracked launch type, including Steam).
- Environment variables: TRAYTRIGGER_PHASE, TRAYTRIGGER_GAME_NAME, TRAYTRIGGER_GAME_ID, TRAYTRIGGER_GAME_EXE, and after exit TRAYTRIGGER_PLAYTIME_MINUTES (minutes the game actually ran, for every tracked launch type including Steam).

> Environment variables are not set when running as Administrator, because Windows cannot pass a custom environment through a UAC launch. Elevated scripts should read the arguments instead; the game ID and playtime are passed as arguments 4 and 5 for exactly this reason.

> For .bat/.cmd scripts, percent signs are removed from the game name argument (%2) because cmd.exe would otherwise expand something like "%TEMP%" before your script sees it. The exact name is always available in TRAYTRIGGER_GAME_NAME.

> Batch gotcha: never put the playtime argument directly before a redirect. cmd reads `echo %~5> log.txt` as a handle redirect when playtime is a single digit and writes nothing. Add a space or brackets: `echo [%~5] > log.txt`.

## Troubleshooting

- Every script run is written to the TrayTrigger log under the GameScript category: the path, the options, the exit code, and any failure such as file not found or a cancelled UAC prompt.
- When a script runs hidden (and not as Administrator), everything it prints to stdout and stderr is captured into the same log, prefixed with the game name and phase. Turn on verbose logging in Settings to see stdout; stderr lines are always logged as warnings.
- A script that runs visibly keeps its output in its own console window and nothing is captured.
