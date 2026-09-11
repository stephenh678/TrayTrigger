<#
  Name:             Companion Apps
  Description:      Starts the tools a game needs when it launches and closes them when it exits.
  Author:           TrayTrigger
  Version:          1.0
  Phase:            both
  Needs admin:      no
  Dependencies:     the programs you list
  Script Arguments: full paths of the programs to start, each in double quotes

  WHY
    Sim racers start SimHub and Crew Chief, flight simmers start TrackIR,
    PlayStation controller owners start DS4Windows. Starting them by hand
    every time is tedious, and forgetting to close them afterwards leaves
    them running all day.

  SET IT UP
    1. Find each program's full path: right-click its shortcut, choose Open
       file location, and copy the path from Explorer's address bar.
    2. In Edit Game, choose this file as the pre-launch script, tick "Use the
       same script for pre-launch and post-exit", and put the paths in Script
       Arguments, each in double quotes:
         "C:\Program Files (x86)\SimHub\SimHubWPF.exe" "C:\Program Files (x86)\Britton IT Ltd\CrewChiefV4\CrewChiefV4.exe"
       A program that needs extra options, such as a profile or "start
       minimized", goes in the $Companions list below instead.
    3. Press Test next to the pre-launch box: the programs start. Press Test
       next to the post-exit box: they close.

  HOW IT WORKS
    prelaunch  Starts each program that isn't already running and notes the
               time and its path in your TEMP folder. A program you had
               already opened yourself is left alone.
    postexit   Closes only what this script started: every process from those
               paths that started after the note was written. It asks each
               window to close, then ends anything still running a few
               seconds later.

  GOOD TO KNOW
    Tools that sit in the tray have no window to ask, so they are ended
    straight away. Most companion tools save their settings as you change
    them, so that is usually fine.
    A program that starts through a separate launcher exe, which then starts
    the real program, can't be followed. List the real program's exe instead.

  MAKE IT YOURS
    Copy this file once per kind of game, for example "Sim Racing Apps.ps1"
    and "Flight Sim Apps.ps1", each with its own $Companions list. Then the
    Script Arguments box stays empty.
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

# Programs to start for every game that uses this file, with optional
# arguments. Paths from Script Arguments are added to this list. Remove the #
# in front of a line to use it.
$Companions = @(
    # @{ Path = 'C:\Program Files (x86)\NaturalPoint\TrackIR5\TrackIR5.exe'; Arguments = '' }
    # @{ Path = 'C:\Tools\DS4Windows\DS4Windows.exe'; Arguments = '-m' }
)

# How the programs' windows open: Normal, Minimized or Hidden.
$WindowStyle = 'Minimized'

# How long programs get to close on their own before they are ended.
$GraceSeconds = 5

# ------------------------------------------------------------------------------

# The note that carries what was started from the pre-launch run to the
# post-exit run. Named after the game ID, so games don't mix up notes.
$note = Join-Path $env:TEMP "TrayTrigger-CompanionApps-$GameId.txt"

switch ($Phase) {
    'prelaunch' {
        $list = @($Companions)
        foreach ($arg in $ScriptArgs) {
            if ($arg) { $list += @{ Path = $arg; Arguments = '' } }
        }
        if ($list.Count -eq 0) {
            Write-Output 'No programs listed. Put their paths in Edit Game > Script Arguments, or in the $Companions list in this file.'
            exit 0
        }

        $started = @()
        foreach ($companion in $list) {
            # %ProgramFiles% and similar shortcuts are allowed in paths.
            $path = [Environment]::ExpandEnvironmentVariables($companion.Path)
            $name = [IO.Path]::GetFileNameWithoutExtension($path)

            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                Write-Output "Not found: $path"
                continue
            }

            # Already running from that path? Then you opened it yourself, and
            # this script won't close it later either.
            $already = Get-Process -Name $name -ErrorAction SilentlyContinue |
                Where-Object { $_.Path -eq $path }
            if ($already) {
                Write-Output "$name is already running. Leaving it alone."
                continue
            }

            # Note the time just before starting, to recognise the process later.
            $startedAt = Get-Date

            $startArgs = @{
                FilePath         = $path
                # Many programs look for their own files next to the exe.
                WorkingDirectory = Split-Path -Parent $path
                WindowStyle      = $WindowStyle
            }
            if ($companion.Arguments) { $startArgs.ArgumentList = $companion.Arguments }
            Start-Process @startArgs

            Write-Output "Started $name."
            $started += '{0}|{1}' -f $startedAt.ToString('o'), $path
        }

        if ($started.Count -gt 0) {
            Set-Content -LiteralPath $note -Value $started
        }
    }

    'postexit' {
        if (-not (Test-Path -LiteralPath $note)) {
            Write-Output 'This script did not start anything. Nothing to close.'
            exit 0
        }
        $lines = @(Get-Content -LiteralPath $note)
        Remove-Item -LiteralPath $note -Force

        # Find the processes this script started: same exe path, started after
        # the time in the note. Anything else from that exe is yours, not ours.
        $targets = @()
        foreach ($line in $lines) {
            $time, $path = $line -split '\|', 2
            $startedAt = [datetime]::Parse($time, $null, [Globalization.DateTimeStyles]::RoundtripKind)
            $name = [IO.Path]::GetFileNameWithoutExtension($path)

            $mine = @(Get-Process -Name $name -ErrorAction SilentlyContinue | Where-Object {
                try { $_.Path -eq $path -and $_.StartTime -ge $startedAt.AddSeconds(-1) } catch { $false }
            })
            if ($mine.Count -eq 0) {
                Write-Output "$name has already closed."
                continue
            }
            $targets += [pscustomobject]@{ Name = $name; Processes = $mine }
        }

        if ($targets.Count -eq 0) { exit 0 }

        # Ask nicely first. CloseMainWindow is the same as clicking the window's X,
        # and returns false for a program with no window.
        $asked = $false
        foreach ($target in $targets) {
            foreach ($process in $target.Processes) {
                if ($process.CloseMainWindow()) { $asked = $true }
            }
        }
        if ($asked) {
            $targets.Processes | Wait-Process -Timeout $GraceSeconds -ErrorAction SilentlyContinue
        }

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
    }

    default {
        Write-Output "Unknown phase '$Phase'."
        exit 1
    }
}

exit 0
