# Pre-Launch and Post-Exit Scripts

Attach your own script or program to a game and TrayTrigger runs it just before the game starts and again after it exits. Use it for anything TrayTrigger does not do itself: closing Discord, switching audio output, setting an RGB profile, remapping a controller, starting a recorder.

## Supported file types

- .bat and .cmd, run through cmd.exe.
- .ps1, run through PowerShell with the execution policy bypassed, so a locked-down policy will not block it.
- .exe and .com, run directly.
- Anything else, such as .py or .ahk, is refused. Wrap it in a one-line .bat instead.

## When they run

- Pre-launch runs after the Performance Profile is applied and before the game starts, for every launch type.
- Post-exit runs after the Performance Profile is restored, so your script sees the machine back in its normal state. It needs an exit signal, which exists for direct .exe launches and Steam games. Links from other launchers such as Epic or GOG have no exit signal, so post-exit is skipped for them.
- If TrayTrigger closes while a game with a post-exit script is still running, the script runs then rather than never.

## Options

- Wait for the pre-launch script holds the game launch until the script finishes, capped at 30 seconds so a stuck script cannot block you. Turn it off for long-running scripts.
- Run scripts hidden suppresses the console window.
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

- Arguments, in order: phase (prelaunch or postexit), game name, game exe path. In a batch file those are %1, %2, and %3. Use %~2 to strip the quotes around a name with spaces.
- Environment variables: TRAYTRIGGER_PHASE, TRAYTRIGGER_GAME_NAME, TRAYTRIGGER_GAME_ID, TRAYTRIGGER_GAME_EXE, and after exit TRAYTRIGGER_PLAYTIME_MINUTES.

> Environment variables are not set when running as Administrator, because Windows cannot pass a custom environment through a UAC launch. Elevated scripts should read the arguments instead.

## Troubleshooting

- Every script run is written to the TrayTrigger log under the GameScript category: the path, the options, and any failure such as file not found or a cancelled UAC prompt.
- The script's own output is not captured. Redirect it inside the script if you need a record.
