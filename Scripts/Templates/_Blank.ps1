<#
  My TrayTrigger script

  TrayTrigger runs this file just before the game starts ($Phase is
  "prelaunch") and again after it exits ($Phase is "postexit"). It gets the
  game's name, exe, ID and minutes played, then whatever you typed in Edit
  Game > Script Arguments, in $ScriptArgs.

  Exit 0 for success. Press Test next to the script box to try it.

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
