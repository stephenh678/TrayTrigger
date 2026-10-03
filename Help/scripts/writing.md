# Writing your own script

A script doesn't have to be long. This batch file is a complete pre-launch script that closes Discord:

- @echo off
- taskkill /im Discord.exe /f

Start from a copy: "New script..." next to a script box in Edit Game copies a blank template (_Blank.ps1 or _Blank.bat) to a name you choose and opens it for editing. The templates already read every value below and branch on the phase. Pre-Launch and Post-Exit Scripts covers the options, the examples and the Test button.

## What a script is given

Every script works as-is; nothing here is required. If one script should behave differently per game, TrayTrigger passes these details, which the script is free to ignore.

- Arguments, in order: phase (prelaunch or postexit), game name, game exe path, game ID, playtime in minutes. In a batch file those are %1 to %5; in PowerShell, $args[0] to $args[4]. Use %~2 to strip the quotes around a name with spaces.
- Playtime is empty on pre-launch, so the position never shifts between phases, and is the minute count on post-exit, for every tracked launch type including Steam.
- Environment variables: TRAYTRIGGER_PHASE, TRAYTRIGGER_GAME_NAME, TRAYTRIGGER_GAME_ID, TRAYTRIGGER_GAME_EXE, and after exit TRAYTRIGGER_PLAYTIME_MINUTES.

> Environment variables are not set when running as Administrator, because Windows cannot pass a custom environment through a UAC launch. Elevated scripts should read the arguments instead; the game ID and playtime are passed as arguments 4 and 5 for exactly this reason.

## Script Arguments

The Script Arguments box in Edit Game is free text appended after the five built-in arguments, for both scripts. It is how one generic script serves many games: the script reads a save folder or a profile name from there instead of having it edited in.

- They start at %6 in a batch file and at $args[5] in PowerShell. In a PowerShell param block, declare the built-in five and then a [Parameter(ValueFromRemainingArguments)] array for the rest.
- For .bat and .cmd the text is passed exactly as typed, and cmd.exe parses it. Quote a value that contains spaces. An unbalanced double quote will break cmd's parsing of the whole command line, so keep quotes paired.
- For .ps1, .exe and .com the text is split into separate arguments with the usual Windows rules: spaces separate, double quotes group, a backslash before a quote escapes it. "C:\My Saves" arrives as one argument with no quotes.
- Test passes them too, so the result dialog shows exactly what the script will see.

## Passing something from before to after

The pre-launch run and the post-exit run are separate. To carry something from one to the other, such as which apps were closed, write a small note file in your TEMP folder named after the game ID, then read and delete it after the game. Every bundled example that puts something back works this way.

## Exit codes

Exit code 0 means success. A non-zero exit code only stops the game from starting when "Cancel the launch if the pre-launch script fails" is ticked; otherwise it is logged and the game starts.

## Batch and PowerShell quirks

> For .bat and .cmd scripts, percent signs are removed from the game name, executable path and game ID arguments (%2, %3, %4) because cmd.exe would otherwise expand something like "%TEMP%" before your script sees it. The exact values are always available in TRAYTRIGGER_GAME_NAME, TRAYTRIGGER_GAME_EXE and TRAYTRIGGER_GAME_ID. A .bat or .cmd script whose own path contains a % sign isn't run, for the same reason.

> For .ps1 scripts, leading dashes are removed from the game name argument, because PowerShell would read "-Name" as a parameter rather than a value. The exact name is in TRAYTRIGGER_GAME_NAME.

> Never put the playtime argument directly before a redirect in a batch file. cmd reads `echo %~5> log.txt` as a handle redirect when playtime is a single digit and writes nothing. Add a space or brackets: `echo [%~5] > log.txt`.

## Sharing a script

The community catalog is at https://github.com/stephenh678/TrayTrigger-Scripts. To have a script ready for it: start with the same header the examples use, use only the five values and Script Arguments, make no downloads or network calls, close only programs the user named or your script started, put back whatever you change, and test both phases.
