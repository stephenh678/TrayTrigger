<#
  Example: zip a folder after the game exits (save-game backup).

  What it does
    After the game closes, compresses the folder you name into a dated .zip
    under Documents\TrayTrigger Backups\<game name>\ and keeps only the newest
    10 archives for that game. On prelaunch it does nothing. It never touches
    the source folder.

  Set it as
    The post-exit script of a game (or both fields; prelaunch is a no-op), or
    the default post-exit script in Settings with a per-game folder in each
    game's Script Arguments.

  Script Arguments
    1. The folder to back up (required). Quote it if it has spaces:
         "C:\Users\me\Saved Games\MyGame"
    2. Optional: where to put the archives. Default:
         Documents\TrayTrigger Backups
    Nothing happens if no folder is given.

  Arguments TrayTrigger passes (see _Blank.ps1 for the full description)
    $Phase $GameName $GameExe $GameId $Playtime, then your Script Arguments.
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

$keep = 10

if ($Phase -ne 'postexit') {
    Write-Output "Nothing to do on $Phase."
    exit 0
}

if (-not $ScriptArgs -or $ScriptArgs.Count -lt 1 -or -not $ScriptArgs[0]) {
    Write-Output 'No folder given. Put the save folder path in Edit Game, Script Arguments, for example "C:\Users\me\Saved Games\MyGame".'
    exit 0
}

$source = $ScriptArgs[0]
$destRoot = if ($ScriptArgs.Count -ge 2 -and $ScriptArgs[1]) {
    $ScriptArgs[1]
} else {
    Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'TrayTrigger Backups'
}
$minutes = if ($Playtime) { [int]$Playtime } else { 0 }

if (-not (Test-Path -LiteralPath $source -PathType Container)) {
    Write-Output "Folder not found: $source"
    exit 1
}

try {
    # A game name is free text; strip anything Windows will not accept in a file name.
    $safeName = ($GameName -replace '[\\/:*?"<>|]', '_').Trim()
    if (-not $safeName) { $safeName = 'Game' }

    $dest = Join-Path $destRoot $safeName
    New-Item -ItemType Directory -Path $dest -Force | Out-Null

    $stamp = Get-Date -Format 'yyyy-MM-dd HH-mm-ss'
    $zip = Join-Path $dest ('{0} {1} ({2} min).zip' -f $safeName, $stamp, $minutes)

    Compress-Archive -LiteralPath $source -DestinationPath $zip -CompressionLevel Optimal -ErrorAction Stop
    Write-Output "Saved $zip"

    # Prune: keep the newest $keep archives for this game.
    Get-ChildItem -LiteralPath $dest -Filter '*.zip' |
        Sort-Object LastWriteTime -Descending |
        Select-Object -Skip $keep |
        ForEach-Object {
            Remove-Item -LiteralPath $_.FullName -Force
            Write-Output "Removed old backup $($_.Name)"
        }

    exit 0
}
catch {
    Write-Output "Backup failed: $($_.Exception.Message)"
    exit 1
}
