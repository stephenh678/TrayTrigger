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

## Troubleshooting

- Every script run is written to the TrayTrigger log under the GameScript category: the path, the options, the exit code, and any failure such as file not found or a cancelled UAC prompt.
- When a script runs hidden (and not as Administrator), everything it prints to stdout and stderr is captured into the same log, prefixed with the game name and phase. Turn on verbose logging in Settings to see stdout; stderr lines are always logged as warnings.
- A script that runs visibly keeps its output in its own console window and nothing is captured.
