<#
  My TrayTrigger script

  TrayTrigger runs this file just before the game starts ($Phase is
  "prelaunch") and again after it exits ($Phase is "postexit"). It gets the
  game's name, exe, ID and minutes played, then whatever you typed in Edit
  Game > Script Arguments, in $ScriptArgs.

  Exit 0 for success. Press Test next to the script box to try it.

  A line that starts with "TT:" is for the player: TrayTrigger puts it on the
  game's Played row in Activity & History, and shows it in the launch popup
  before the game. Everything else you print goes to the log.

  More - environment variables, exit codes, passing a value from the
  "before" run to the "after" run - is in README.txt in the scripts folder.
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
        Write-Output "TT: nothing set up yet"
    }
    'postexit' {
        # --- Runs after the game exits. $minutes holds the minutes played -----
        Write-Output "Post-exit for '$GameName' after $minutes minute(s)"
        Write-Output "TT: nothing to put back yet"
    }
    default {
        Write-Output "Unknown phase '$Phase'"
        exit 1
    }
}

exit 0
