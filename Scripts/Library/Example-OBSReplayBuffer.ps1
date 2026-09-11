<#
  Name:             OBS Replay Buffer
  Description:      Runs OBS in the tray with the replay buffer on while you play, then closes it.
  Author:           TrayTrigger
  Version:          1.0
  Phase:            both
  Needs admin:      no
  Dependencies:     OBS Studio 30 or newer, with the replay buffer set up
  Script Arguments: optional OBS profile name, in double quotes

  WHY
    With the replay buffer running, OBS keeps the last minute or two of
    gameplay in memory and only saves it when you press a hotkey. You never
    miss the clutch moment, and nothing fills your disk unless you ask.

  SET IT UP
    1. In OBS, open Settings > Output > Replay Buffer, tick "Enable Replay
       Buffer" and choose how many seconds to keep. Then open Settings >
       Hotkeys and set a key for "Save Replay". Close OBS.
    2. In Edit Game, choose this file as the pre-launch script and tick "Use
       the same script for pre-launch and post-exit". To use a different OBS
       profile for this game, put its name in Script Arguments, for example:
         "Competitive 1080p"
    3. Press Test next to the pre-launch box: OBS appears in the tray. Press
       Test next to the post-exit box: it closes.

  HOW IT WORKS
    prelaunch  If OBS is already open, does nothing: you might be streaming
               or recording. Otherwise starts OBS minimized to the tray with
               the replay buffer running, and notes that in your TEMP folder.
    postexit   If this script started OBS, closes it.

  GOOD TO KNOW
    OBS has to be started from its own folder or it can't find its files, so
    the script sets the working folder. Remember that trick when you start
    other programs from a script.
    Replays you saved with the hotkey are already on disk before OBS closes.
    If OBS doesn't close within a few seconds, for example because it is
    asking a question, it is ended. The script starts OBS with its shutdown
    check turned off, so that doesn't cause a safe-mode prompt next time.

  MAKE IT YOURS
    OBS has more startup options: --startrecording, --startstreaming,
    --scene "name" and --collection "name". Add them to $obsArguments below.
    Installed OBS through Steam? Change $ObsExe to the Steam path shown there.
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

# ---- Change these ------------------------------------------------------------

# Where OBS is installed. For the Steam version use:
#   C:\Program Files (x86)\Steam\steamapps\common\OBS Studio\bin\64bit\obs64.exe
$ObsExe = Join-Path $env:ProgramFiles 'obs-studio\bin\64bit\obs64.exe'

# How long OBS gets to close on its own before it is ended.
$GraceSeconds = 10

# ------------------------------------------------------------------------------

# The note that tells the post-exit run which OBS the pre-launch run started.
# Named after the game ID, so games don't mix up notes.
$note = Join-Path $env:TEMP "TrayTrigger-OBSReplayBuffer-$GameId.txt"

switch ($Phase) {
    'prelaunch' {
        # Never touch an OBS you opened yourself.
        if (Get-Process -Name 'obs64' -ErrorAction SilentlyContinue) {
            Write-Output 'OBS is already open. Leaving it alone.'
            exit 0
        }
        if (-not (Test-Path -LiteralPath $ObsExe -PathType Leaf)) {
            Write-Output "OBS was not found at $ObsExe. Change `$ObsExe at the top of this script."
            exit 0
        }

        $obsArguments = @('--minimize-to-tray', '--startreplaybuffer', '--disable-shutdown-check')
        if ($ScriptArgs -and $ScriptArgs[0]) {
            # Windows PowerShell joins these with spaces and adds no quotes, so a
            # profile name with spaces needs its own.
            $obsArguments += '--profile'
            $obsArguments += '"' + $ScriptArgs[0] + '"'
        }

        $obs = Start-Process -FilePath $ObsExe -ArgumentList $obsArguments `
            -WorkingDirectory (Split-Path -Parent $ObsExe) -PassThru

        Set-Content -LiteralPath $note -Value $obs.Id
        Write-Output "Started OBS with the replay buffer for $GameName."
    }

    'postexit' {
        if (-not (Test-Path -LiteralPath $note)) {
            Write-Output 'This script did not start OBS. Leaving OBS alone.'
            exit 0
        }
        $obsId = [int](Get-Content -LiteralPath $note -TotalCount 1)
        Remove-Item -LiteralPath $note -Force

        # Check the ID still belongs to OBS: Windows reuses process IDs.
        $obs = Get-Process -Id $obsId -ErrorAction SilentlyContinue |
            Where-Object { $_.ProcessName -eq 'obs64' }
        if (-not $obs) {
            Write-Output 'OBS has already closed.'
            exit 0
        }

        # OBS in the tray has no visible window, so CloseMainWindow can't reach
        # it. taskkill without /F sends the same polite close request to every
        # window the process has, hidden ones included.
        taskkill.exe /PID $obsId | Out-Null

        if ($obs.WaitForExit($GraceSeconds * 1000)) {
            Write-Output 'Closed OBS.'
        }
        else {
            Stop-Process -Id $obsId -Force -ErrorAction SilentlyContinue
            Write-Output 'OBS did not close in time and was ended.'
        }
    }

    default {
        Write-Output "Unknown phase '$Phase'."
        exit 1
    }
}

exit 0
