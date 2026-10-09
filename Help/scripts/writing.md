# Writing your own script

A script doesn't have to be long. This one line is a complete PowerShell pre-launch script that closes Discord:

- Stop-Process -Name Discord -ErrorAction SilentlyContinue

Start from a template: "New script..." next to a script box in Edit Game creates a PowerShell script from a blank template and opens it for editing. The template already reads every value below and branches on the phase, and the script is yours: TrayTrigger never changes it. README.txt in the scripts folder has the same reference as this page. Pre-Launch and Post-Exit Scripts covers the options, the examples and the Test button.

## What a script is given

Every script works as-is; nothing here is required. If one script should behave differently per game, TrayTrigger passes these details, which the script is free to ignore.

- Arguments, in order: phase (prelaunch or postexit), game name, game exe path, game ID, playtime in minutes. In PowerShell those are $args[0] to $args[4], or the names in a param block, as the template declares them.
- Playtime is empty on pre-launch, so the position never shifts between phases, and is the minute count on post-exit, for every tracked launch type including Steam.
- Environment variables: TRAYTRIGGER_PHASE, TRAYTRIGGER_GAME_NAME, TRAYTRIGGER_GAME_ID, TRAYTRIGGER_GAME_EXE, and after exit TRAYTRIGGER_PLAYTIME_MINUTES.

> Environment variables are not set when running as Administrator, because Windows cannot pass a custom environment through a UAC launch. Elevated scripts should read the arguments instead; the game ID and playtime are passed as arguments 4 and 5 for exactly this reason.

## Telling the player what you did

A line your script prints that starts with TT: is for the player, not the log. TrayTrigger puts it on the game's Played row in Activity & History, after the script's own "ran", and shows it in the launch popup while the game is still being prepared. Everything else the script prints goes to the log only.

- Write-Output "TT: closed OneDrive and Discord". Short, in plain words, as a sentence fragment: it follows "pre-launch close-apps.ps1 ran · ".
- Up to five TT: lines per run go on the row, each cut at 200 characters, and a repeated line is kept once. Say what you did, not every step.
- When a script fails (a non-zero exit code), its last TT: line explains the failure on the problem row and, if the launch was cancelled, in the launch popup: "TT: Afterburner isn't installed at the path in Script Arguments" beats "exit code 1".
- Only a script that runs hidden and not as Administrator is heard: those are the runs whose output TrayTrigger captures. The bundled examples and the New script... template already do this.

## Script Arguments

The Script Arguments box in Edit Game is free text appended after the five built-in arguments, for both scripts. It is how one generic script serves many games: the script reads a profile name or a list of apps from there instead of having it edited in.

- They start at $args[5]. In a param block, declare the built-in five and then a [Parameter(ValueFromRemainingArguments)] array for the rest, as the template does.
- The text is split into separate arguments with the usual Windows rules: spaces separate, double quotes group, a backslash before a quote escapes it. "C:\My Tools" arrives as one argument with no quotes.
- Test passes them too, so the result dialog shows exactly what the script will see.

## Passing something from before to after

The pre-launch run and the post-exit run are separate. To carry something from one to the other, such as which apps were closed, write a small note file in your TEMP folder named after the game ID, then read and delete it after the game. Every bundled example that puts something back works this way.

## Exit codes

Exit code 0 means success. A non-zero exit code only stops the game from starting when "Cancel the launch if the pre-launch script fails" is ticked; otherwise it is logged and the game starts.

## The game name

> Leading dashes are removed from the game name argument, because PowerShell would read "-Name" as a parameter rather than a value. The exact name is in TRAYTRIGGER_GAME_NAME.

## Getting and sharing scripts

Ready-made scripts live in the TrayTrigger-Scripts catalogue on GitHub: https://github.com/stephenh678/TrayTrigger-Scripts. Each is one PowerShell file with the same header as the bundled examples; download it into the scripts folder and choose it in Edit Game. "Get more scripts" beside the script boxes opens the catalogue.

To share one of yours, open a pull request there, or post it in Show and tell in TrayTrigger's Discussions: https://github.com/stephenh678/TrayTrigger/discussions/7. A script others can trust starts with the same header the examples use, uses only the five values and Script Arguments, makes no downloads or network calls, closes only programs the user named or the script started, puts back whatever it changes, says what it did with TT: lines, and has been tested in both phases.
