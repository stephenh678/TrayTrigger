<#
  TrayTrigger game script (PowerShell template)

  TrayTrigger runs this file just before a game starts and again after it
  exits, as:

    powershell -NoProfile -ExecutionPolicy Bypass -File <this file> <phase> <name> <exe> <id> <playtime> [script arguments...]

    $Phase       "prelaunch" or "postexit"
    $GameName    as shown in TrayTrigger
    $GameExe     full path of the game's executable
    $GameId      TrayTrigger's stable ID for this game
    $Playtime    minutes played: empty on prelaunch, a number on postexit
    $ScriptArgs  Edit Game, Script Arguments, already split into tokens
                 ("C:\My Saves" arrives as one element, without the quotes)

  When the script is not run as Administrator the same values are also in the
  environment: $env:TRAYTRIGGER_PHASE, TRAYTRIGGER_GAME_NAME, TRAYTRIGGER_GAME_EXE,
  TRAYTRIGGER_GAME_ID and TRAYTRIGGER_PLAYTIME_MINUTES.

  Exit code 0 means success. A non-zero exit code cancels the launch when
  "Cancel the launch if the pre-launch script fails" is ticked. Output is
  captured to the TrayTrigger log when the script runs hidden, and is shown by
  the Test button in Edit Game.
#>
param(
    [string]$Phase,
    [string]$GameName,
    [string]$GameExe,
    [string]$GameId,
    [string]$Playtime,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ScriptArgs
)

# Declared as a string above because it is empty on prelaunch; use $minutes as a number.
$minutes = if ($Playtime) { [int]$Playtime } else { 0 }

switch ($Phase) {
    'prelaunch' {
        # --- Runs just before the game starts ---------------------------------
        Write-Output "Pre-launch for '$GameName'"
    }
    'postexit' {
        # --- Runs after the game exits. $minutes holds the minutes played -----
        Write-Output "Post-exit for '$GameName' after $minutes minute(s)"
    }
    default {
        Write-Output "Unknown phase '$Phase'"
        exit 1
    }
}

exit 0
