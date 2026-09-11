<#
  Name:             Quiet Mode
  Description:      Closes the background apps you name before a game and reopens them after.
  Author:           TrayTrigger
  Version:          1.0
  Phase:            both
  Needs admin:      no
  Dependencies:     none
  Script Arguments: process names to close, separated by spaces

  WHY
    Cloud sync, chat and launcher apps keep working in the background:
    uploading, downloading, indexing, popping up notifications. Closing them
    for the length of a game frees CPU, disk and bandwidth. This script brings
    back exactly the ones it closed when you are done.

  SET IT UP
    1. Find the process names in Task Manager > Details, without ".exe".
       Common ones:
         OneDrive            OneDrive
         Microsoft Teams     ms-teams
         Dropbox             Dropbox
         Google Drive        GoogleDriveFS
         Adobe Creative      "Creative Cloud"
         Spotify             Spotify
         Discord             Discord   (you drop out of voice chat)
    2. In Edit Game, choose this file as the pre-launch script, tick "Use the
       same script for pre-launch and post-exit", and type the names into
       Script Arguments, for example:
         OneDrive ms-teams Dropbox
       Put a name that contains spaces in double quotes.
    3. Press Test next to the pre-launch box: the apps close. Press Test next
       to the post-exit box: they come back.

  HOW IT WORKS
    prelaunch  For each name that is running, remembers the program's full
               path and asks its window to close. Anything still running a
               few seconds later is ended. The paths go into a note in your
               TEMP folder.
    postexit   Starts each remembered program again, unless it is already
               running, and deletes the note.

  GOOD TO KNOW
    Apps that live only in the tray have no window to ask, so they are ended
    straight away. That is fine for sync and chat apps. Don't list an editor
    or anything else that could be holding unsaved work.
    A program running as Administrator can't be closed from a normal script,
    so it is skipped.
    Restarted apps start the way they do from their exe, which may open their
    window instead of starting in the tray.

  MAKE IT YOURS
    Many apps have their own clean way to quit. OneDrive, for example, quits
    properly with "OneDrive.exe /shutdown". Add a special case in the
    prelaunch section for the apps you care about.
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

# How long apps get to close on their own before they are ended.
$GraceSeconds = 5

# Never closed, even when listed: closing these would break Windows, TrayTrigger
# or the game's own launch.
$NeverClose = @('explorer', 'dwm', 'TrayTrigger', 'steam', 'steamwebhelper',
    'EpicGamesLauncher', 'GalaxyClient', 'EADesktop', 'upc', 'XboxPcApp')

# ------------------------------------------------------------------------------

# The note that carries the closed programs from the pre-launch run to the
# post-exit run. Named after the game ID, so games don't mix up notes.
$note = Join-Path $env:TEMP "TrayTrigger-QuietMode-$GameId.txt"

switch ($Phase) {
    'prelaunch' {
        if (-not $ScriptArgs) {
            Write-Output 'No apps named. Put process names in Edit Game > Script Arguments, for example: OneDrive ms-teams Dropbox'
            exit 0
        }

        # Work out what to close first, so the waiting below happens only once.
        $targets = @()
        foreach ($arg in $ScriptArgs) {
            # Accept "OneDrive" and "OneDrive.exe" alike.
            $name = $arg -replace '\.exe$', ''
            if (-not $name) { continue }

            if ($NeverClose -contains $name) {
                Write-Output "Skipping ${name}: it is on the never-close list."
                continue
            }

            $processes = @(Get-Process -Name $name -ErrorAction SilentlyContinue)
            if ($processes.Count -eq 0) {
                Write-Output "$name is not running."
                continue
            }

            # The full path is what we need to start it again later. It is empty
            # for a program running as Administrator, which we can't close anyway.
            $path = ($processes | Where-Object { $_.Path } | Select-Object -First 1).Path
            if (-not $path) {
                Write-Output "Skipping ${name}: it is running as Administrator."
                continue
            }

            $targets += [pscustomobject]@{ Name = $name; Path = $path; Processes = $processes }
        }

        if ($targets.Count -eq 0) { exit 0 }

        # Ask nicely first. CloseMainWindow is the same as clicking the window's X,
        # and returns false for an app with no window.
        $asked = $false
        foreach ($target in $targets) {
            foreach ($process in $target.Processes) {
                if ($process.CloseMainWindow()) { $asked = $true }
            }
        }
        if ($asked) {
            $targets.Processes | Wait-Process -Timeout $GraceSeconds -ErrorAction SilentlyContinue
        }

        # End whatever is still running, then remember what was closed.
        foreach ($target in $targets) {
            $left = @($target.Processes | Where-Object { -not $_.HasExited })
            if ($left.Count -gt 0) {
                $left | Stop-Process -Force -ErrorAction SilentlyContinue
                Write-Output "Ended $($target.Name)."
            }
            else {
                Write-Output "Closed $($target.Name)."
            }
        }
        Set-Content -LiteralPath $note -Value $targets.Path
    }

    'postexit' {
        if (-not (Test-Path -LiteralPath $note)) {
            Write-Output 'This script did not close anything. Nothing to reopen.'
            exit 0
        }
        $paths = @(Get-Content -LiteralPath $note)
        Remove-Item -LiteralPath $note -Force

        foreach ($path in $paths) {
            $name = [IO.Path]::GetFileNameWithoutExtension($path)

            if (Get-Process -Name $name -ErrorAction SilentlyContinue) {
                Write-Output "$name is already running again."
                continue
            }
            # An app can update itself into a new folder while you play.
            if (-not (Test-Path -LiteralPath $path)) {
                Write-Output "Can't reopen ${name}: $path no longer exists."
                continue
            }

            Start-Process -FilePath $path -WorkingDirectory (Split-Path -Parent $path)
            Write-Output "Reopened $name."
        }
    }

    default {
        Write-Output "Unknown phase '$Phase'."
        exit 1
    }
}

exit 0
