<#
.SYNOPSIS
  Checks that TrayTrigger's pages and dialogs fit at other text sizes and fonts.

.DESCRIPTION
  Runs the app's own layout audit (--test-layout-audit) once for each text size and font, and lists what does not fit at
  each that did fit at the baseline (100% in the first font). A Debug build is needed. Close TrayTrigger first: a running
  instance holds the files the audit reads and writes.

  .\tools\Audit-Layout.ps1 -Out C:\temp\tt-audit
  .\tools\Audit-Layout.ps1 -Out C:\temp\tt-audit -Scales 130,150 -Fonts 'Segoe UI','Verdana'
  .\tools\Audit-Layout.ps1 -Out C:\temp\tt-audit -Theme Light

  Each run writes audit-<size>-<font>.txt (every issue it found, page by page) and the whole run writes summary.txt.
  TRIM is text that shows an ellipsis because it does not fit, CUT is text that is simply not drawn, CLIP is an element
  the layout cut down to its slot. Some of each are by design, which is why only what is NEW against the baseline is listed.
  Exits 1 when anything is new, so it can gate a change.
#>
param(
    [Parameter(Mandatory)][string]$Out,
    [int[]]$Scales = @(90, 100, 130, 150),
    [string[]]$Fonts = @('Segoe UI', 'Verdana', 'Cambria', 'Consolas'),
    [string]$Theme,
    [string]$Exe = (Join-Path $PSScriptRoot '..\bin\Debug\net10.0-windows\win-x64\TrayTrigger.exe'),
    [int]$TimeoutSeconds = 120
)

if (-not (Test-Path $Exe)) { throw "Build the Debug configuration first; not found: $Exe" }
if (Get-Process TrayTrigger -ErrorAction SilentlyContinue) { throw 'Close TrayTrigger first.' }
New-Item -ItemType Directory -Force $Out | Out-Null

$baselineScale = 100
$baselineFont = $Fonts[0]
# The baseline is always run, first, whether or not it is in the lists.
$combos = @(, @($baselineScale, $baselineFont))
foreach ($font in $Fonts) { foreach ($scale in $Scales) { if (-not ($scale -eq $baselineScale -and $font -eq $baselineFont)) { $combos += , @($scale, $font) } } }

# "## Page Library: 36 texts, 2 issues" starts a page; "  - ..." lines under it are its issues. The key leaves out the
# numbers after " | " so the same issue is the same key at every size.
function Read-Issues([string]$File) {
    $issues = [ordered]@{}
    $page = ''
    # The report is UTF-8 without a mark; Windows PowerShell 5.1 would read it as ANSI and spoil the quotes and ellipses in the text.
    foreach ($line in Get-Content -Encoding UTF8 $File) {
        if ($line -match '^## (.+?): \d+ texts') { $page = $Matches[1]; continue }
        if ($line -match '^  - (.+)$') {
            $text = $Matches[1]
            $key = $page + ' :: ' + ($text -split ' \| ')[0]
            $issues[$key] = $text
        }
    }
    return $issues
}

$q = [char]34
$results = @()
$baseline = $null
foreach ($combo in $combos) {
    $scale = $combo[0]; $font = $combo[1]
    $file = Join-Path $Out ("audit-{0}-{1}.txt" -f $scale, ($font -replace '[^A-Za-z0-9]', ''))
    Remove-Item $file -ErrorAction SilentlyContinue
    $themeArgs = if ($Theme) { "--theme $q$Theme$q " } else { '' }
    $argText = "$themeArgs--text-scale $scale --font $q$font$q --test-layout-audit $q$file$q"
    $p = Start-Process -FilePath $Exe -ArgumentList $argText -PassThru
    if (-not $p.WaitForExit($TimeoutSeconds * 1000)) { $p.Kill(); $p.WaitForExit() }
    if (-not (Test-Path $file)) {
        "{0,4}%  {1,-14} NO REPORT" -f $scale, $font
        # Everything is compared with the baseline, so without it there is nothing to say about the rest.
        if ($null -eq $baseline) { throw "The baseline run ($baselineScale%, $baselineFont) wrote no report; see crash.log in %LocalAppData%\TrayTrigger." }
        $results += [pscustomobject]@{ Scale = $scale; Font = $font; Total = -1; New = @(); NoReport = $true }
        continue
    }
    $issues = Read-Issues $file
    if ($null -eq $baseline) { $baseline = $issues }
    $newKeys = @($issues.Keys | Where-Object { -not $baseline.Contains($_) })
    $results += [pscustomobject]@{ Scale = $scale; Font = $font; Total = $issues.Count; New = $newKeys; Detail = $issues }
    "{0,4}%  {1,-14} {2,3} issues, {3,3} new" -f $scale, $font, $issues.Count, $newKeys.Count
}

$summary = New-Object System.Collections.Generic.List[string]
$summary.Add("Layout audit: baseline is $baselineScale% in $baselineFont")
$summary.Add('')
foreach ($r in $results) {
    if ($r.NoReport) { $summary.Add(("{0}%  {1}: NO REPORT" -f $r.Scale, $r.Font)); continue }
    $summary.Add(("{0}%  {1}: {2} issues, {3} new" -f $r.Scale, $r.Font, $r.Total, @($r.New).Count))
    foreach ($key in $r.New) { $summary.Add('    ' + $key + ' | ' + (($r.Detail[$key] -split ' \| ', 2)[1])) }
}
$summaryFile = Join-Path $Out 'summary.txt'
$summary | Set-Content -Encoding UTF8 $summaryFile
''
"Summary written to $summaryFile"
$anyNew = @($results | Where-Object { @($_.New).Count -gt 0 -or $_.NoReport }).Count -gt 0
if ($anyNew) { exit 1 }
