<#
  Example: log every session to a CSV file.

  What it does
    Appends one line per phase (a "prelaunch" line when the game starts and a
    "postexit" line with the minutes played when it ends) to a CSV file you can
    open in Excel. Nothing else. It shows how to read every argument
    TrayTrigger passes.

  Set it as
    Both the pre-launch and the post-exit script of a game, or as both
    defaults in Settings to log every game in the library.

  Script Arguments (optional)
    The CSV path. Default: Documents\TrayTrigger\Sessions.csv
    Quote it if it contains spaces:  "C:\My Logs\games.csv"

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

$minutes = if ($Playtime) { [int]$Playtime } else { 0 }

$csv = if ($ScriptArgs -and $ScriptArgs.Count -ge 1 -and $ScriptArgs[0]) {
    $ScriptArgs[0]
} else {
    Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'TrayTrigger\Sessions.csv'
}

try {
    $dir = Split-Path -Parent $csv
    if ($dir -and -not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    if (-not (Test-Path -LiteralPath $csv)) {
        'Timestamp,Phase,Game,Minutes,GameId,Exe' | Set-Content -LiteralPath $csv -Encoding UTF8
    }

    # CSV-quote free text so a comma or quote in a game name cannot break the row.
    function Quote([string]$s) { '"' + ($s -replace '"', '""') + '"' }

    $line = @(
        (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'),
        $Phase,
        (Quote $GameName),
        $minutes,
        $GameId,
        (Quote $GameExe)
    ) -join ','

    Add-Content -LiteralPath $csv -Value $line -Encoding UTF8
    Write-Output "Logged $Phase for '$GameName' ($minutes min) to $csv"
    exit 0
}
catch {
    Write-Output "Could not write to ${csv}: $($_.Exception.Message)"
    exit 1
}
