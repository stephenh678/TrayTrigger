<#
  Name:             Save Backup
  Description:      Zips a game's save folder before and after you play and keeps the newest ten.
  Author:           TrayTrigger
  Version:          1.0
  Phase:            both
  Needs admin:      no
  Dependencies:     none
  Script Arguments: the save folder in double quotes, then optionally a backup folder

  WHY
    Save files get corrupted: a crash while saving, a bad mod, a cloud sync
    conflict, a patch. A copy from just before you started playing means you
    lose one session at worst, not the whole playthrough.

  SET IT UP
    1. Find the game's save folder. PCGamingWiki lists it for almost every
       game under "Save game data location". Common places:
         %USERPROFILE%\Saved Games
         %USERPROFILE%\Documents\My Games
         %APPDATA%  and  %LOCALAPPDATA%
         %USERPROFILE%\AppData\LocalLow
    2. In Edit Game, choose this file as the pre-launch script, tick "Use the
       same script for pre-launch and post-exit", and put the folder in Script
       Arguments in double quotes. The %...% shortcuts work. For Elden Ring:
         "%APPDATA%\EldenRing"
       To keep the backups somewhere else, such as another drive, add a
       second folder:
         "%APPDATA%\EldenRing" "D:\Game Backups"
    3. Press Test next to either box, then look in
       Documents\TrayTrigger Backups\<game name>.

  HOW IT WORKS
    prelaunch  Zips the save folder into "<game> <date> before.zip", unless
               nothing in it changed since the last backup.
    postexit   Does the same, named with the minutes you played.
    both       Deletes the oldest zips so only the newest ten are kept. The
               save folder itself is only ever read.

  GOOD TO KNOW
    TrayTrigger waits 10 seconds for a pre-launch script by default. Zipping
    a big save folder can take longer; the game then starts anyway while the
    backup finishes. If you'd rather the game waited, raise "Seconds to wait"
    in Edit Game.

  RESTORING A BACKUP
    Close the game. Rename the current save folder so you still have it.
    Each zip contains the save folder itself, so extract the zip into the
    folder that holds it, for example into %APPDATA% for Elden Ring.

  MAKE IT YOURS
    Change $Keep, or set $BackupBeforePlaying to $false to back up only after
    you play.
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

# How many backups to keep per game.
$Keep = 10

# Also back up when the game starts, not only when it exits.
$BackupBeforePlaying = $true

# Where backups go when Script Arguments name no second folder.
$DefaultBackupFolder = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'TrayTrigger Backups'

# ------------------------------------------------------------------------------

if ($Phase -notin 'prelaunch', 'postexit') {
    Write-Output "Unknown phase '$Phase'."
    exit 1
}
if ($Phase -eq 'prelaunch' -and -not $BackupBeforePlaying) {
    Write-Output 'Backing up before playing is turned off in this script.'
    exit 0
}
if (-not $ScriptArgs -or -not $ScriptArgs[0]) {
    Write-Output 'No save folder given. Put it in Edit Game > Script Arguments, for example: "%APPDATA%\EldenRing"'
    exit 0
}

# Windows doesn't expand %...% in Script Arguments for a PowerShell script, so
# the script does it.
$source = [Environment]::ExpandEnvironmentVariables($ScriptArgs[0]).TrimEnd('\')
$backupFolder = if ($ScriptArgs.Count -ge 2 -and $ScriptArgs[1]) {
    [Environment]::ExpandEnvironmentVariables($ScriptArgs[1])
} else {
    $DefaultBackupFolder
}

# Exit code 1 puts the failure in the TrayTrigger log. It only stops the game
# from starting if you ticked "Cancel the launch if the pre-launch script fails".
if (-not (Test-Path -LiteralPath $source -PathType Container)) {
    Write-Output "Save folder not found: $source"
    exit 1
}

# Game names are free text. Replace characters Windows won't allow in a folder
# or file name.
$safeName = ($GameName -replace '[\\/:*?"<>|]', '_').Trim()
if (-not $safeName) { $safeName = 'Game' }

$gameFolder = Join-Path $backupFolder $safeName
New-Item -ItemType Directory -Path $gameFolder -Force | Out-Null

# Skip when no save file changed since the newest backup, so an unchanged save
# doesn't fill the list with identical zips.
$newestZip = Get-ChildItem -LiteralPath $gameFolder -Filter '*.zip' -File |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
$newestSave = Get-ChildItem -LiteralPath $source -Recurse -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($newestZip -and (-not $newestSave -or $newestSave.LastWriteTime -le $newestZip.LastWriteTime)) {
    Write-Output "No changes since the last backup, $($newestZip.Name)."
    exit 0
}

$minutes = if ($Playtime) { [int]$Playtime } else { 0 }
$label = if ($Phase -eq 'prelaunch') { 'before' } else { "after $minutes min" }
$stamp = Get-Date -Format 'yyyy-MM-dd HH-mm-ss'
$zip = Join-Path $gameFolder "$safeName $stamp $label.zip"

try {
    # .NET's zip support is faster than Compress-Archive and handles files
    # over 2 GB. The last argument puts the save folder itself inside the zip.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($source, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
    Write-Output "Backed up $source to $zip"
}
catch {
    # A half-written zip is worse than none.
    Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
    Write-Output "Backup failed: $($_.Exception.Message)"
    exit 1
}

# Keep only the newest $Keep backups for this game.
Get-ChildItem -LiteralPath $gameFolder -Filter '*.zip' -File |
    Sort-Object LastWriteTime -Descending |
    Select-Object -Skip $Keep |
    ForEach-Object {
        Remove-Item -LiteralPath $_.FullName -Force
        Write-Output "Removed old backup $($_.Name)"
    }

exit 0
