<#
  Name:             Wallpaper Engine Pause
  Description:      Pauses and mutes Wallpaper Engine while you play, then resumes it.
  Author:           TrayTrigger
  Version:          1.0
  Phase:            both
  Needs admin:      no
  Dependencies:     Wallpaper Engine
  Script Arguments: none

  WHY
    An animated wallpaper keeps using your GPU behind the game. Wallpaper
    Engine can pause itself for fullscreen apps, but many games run in
    borderless windowed mode and don't trigger that. This script pauses it
    for every game, whatever the window mode.

  SET IT UP
    1. In Edit Game, choose this file as the pre-launch script and tick
       "Use the same script for pre-launch and post-exit". To do it for
       every game, set it as both default scripts in Settings > Launch &
       Performance instead.
    2. Press Test next to each box to try it.

  HOW IT WORKS
    prelaunch  If Wallpaper Engine is running, sends it the "pause" and
               "mute" commands and leaves a note in your TEMP folder saying
               it did.
    postexit   If that note exists, sends "play" and "unmute" and deletes the
               note. If Wallpaper Engine wasn't running before the game,
               nothing happens after it either.

  MAKE IT YOURS
    Wallpaper Engine's command line can do more than pause. Swap "pause" for
    "stop" to unload the wallpaper completely while you play. Its help pages
    list every command under "Command line controls".
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

# Also mute Wallpaper Engine's sound while it is paused.
$AlsoMute = $true

# The process names Wallpaper Engine runs as.
$ProcessNames = @('wallpaper32', 'wallpaper64')

# ------------------------------------------------------------------------------

# The note that tells the post-exit run "the pre-launch run paused it". It is
# named after the game ID, so two games running at once don't mix up notes.
$note = Join-Path $env:TEMP "TrayTrigger-WallpaperEnginePause-$GameId.txt"

# Sends one command to Wallpaper Engine. Starting its exe again with -control
# hands the command to the copy that is already running, then exits.
function Send-WallpaperCommand([string]$Exe, [string]$Command) {
    Start-Process -FilePath $Exe -ArgumentList '-control', $Command -WindowStyle Hidden
    # A short pause so two commands in a row arrive in order.
    Start-Sleep -Milliseconds 500
}

switch ($Phase) {
    'prelaunch' {
        # Find the running Wallpaper Engine and the full path of its exe. The
        # path is empty for a copy running as Administrator, which a normal
        # script can't control anyway, so that counts as not running.
        $running = Get-Process -Name $ProcessNames -ErrorAction SilentlyContinue |
            Where-Object { $_.Path } |
            Select-Object -First 1
        if (-not $running) {
            Write-Output 'Wallpaper Engine is not running. Nothing to pause.'
            exit 0
        }

        Send-WallpaperCommand $running.Path 'pause'
        if ($AlsoMute) { Send-WallpaperCommand $running.Path 'mute' }

        # Remember which exe to talk to after the game.
        Set-Content -LiteralPath $note -Value $running.Path
        Write-Output "Paused Wallpaper Engine for $GameName."
    }

    'postexit' {
        if (-not (Test-Path -LiteralPath $note)) {
            Write-Output 'This script did not pause Wallpaper Engine. Nothing to resume.'
            exit 0
        }
        $exe = Get-Content -LiteralPath $note -TotalCount 1
        Remove-Item -LiteralPath $note -Force

        if (-not (Get-Process -Name $ProcessNames -ErrorAction SilentlyContinue)) {
            Write-Output 'Wallpaper Engine was closed during the game. Nothing to resume.'
            exit 0
        }

        Send-WallpaperCommand $exe 'play'
        if ($AlsoMute) { Send-WallpaperCommand $exe 'unmute' }
        Write-Output 'Resumed Wallpaper Engine.'
    }

    default {
        Write-Output "Unknown phase '$Phase'."
        exit 1
    }
}

exit 0
