<#
.SYNOPSIS
  Captures TrayTrigger's screens with the app's own --screenshot switches, and compares two captures.

.DESCRIPTION
  A Debug build is needed (the many --screenshot-* switches are Debug-only). Close TrayTrigger first:
  a second instance hands off to the first and the capture never happens.

  Capture:  .\tools\Capture-Baseline.ps1 -Out C:\temp\tt-before
  Compare:  .\tools\Capture-Baseline.ps1 -Compare C:\temp\tt-before,C:\temp\tt-after

  Only a few screens:  .\tools\Capture-Baseline.ps1 -Out C:\temp\tt-x -Only library,edit

  The compare counts pixels that differ between same-named PNGs and exits 1 if any do. For a cleanup
  that must not change how the app looks, every image should say 0. -Tolerance N ignores per-channel
  differences of N or less, for a change that moves colors a few units on purpose.
#>
param(
    [string]$Out,
    [string[]]$Compare,
    [string]$Exe = (Join-Path $PSScriptRoot '..\bin\Debug\net10.0-windows\win-x64\TrayTrigger.exe'),
    [int]$TimeoutSeconds = 45,
    [int]$Tolerance = 0,
    [string[]]$Only
)

# One entry per screen: the switch name, then any arguments it takes before the output file. Switches that
# need a free-form argument (the *-search ones, tooltip, focus, help, edit-tab, poster-details) are left out.
$Screens = @(
    @('library', 'Poster Grid'), @('library', 'Extra Large'), @('library', 'Compact Icons'), @('library', 'Details List'),
    @('select', 'Poster Grid'),
    @('library-all'), @('library-category'), @('collapsed'), @('expanded'), @('sidebar-expanded'), @('list'), @('populated'),
    @('hover'), @('nomatch'), @('icons'), @('card-menu'), @('batch'), @('batch-menu'),
    @('settings'), @('settings-general'), @('settings-library'), @('settings-performance'), @('settings-tray'),
    @('settings-undo'), @('settings-diagnostics'), @('settings-top'),
    @('system-all'), @('system-profiles'), @('system-ready'), @('system-specs'), @('system-tweaks'), @('stutter'),
    @('tools'), @('tool-edit'), @('activity'),
    @('about'), @('about-overview'), @('about-features'), @('about-shortcuts'), @('about-support'), @('about-diagnostics'),
    @('edit'), @('edit-bottom'), @('edit-poster'), @('details'), @('picker'),
    @('confirm'), @('exit-dialog'), @('launch-popup'), @('updater-dialog'), @('welcome'), @('steam'),
    @('tray-menu'), @('tray-menu-grouped'), @('tray-recent'), @('tray-flat')
)

function Compare-Images([string]$A, [string]$B) {
    Add-Type -AssemblyName System.Drawing
    $names = Get-ChildItem $A -Filter *.png | ForEach-Object Name
    $anyDiff = $false
    foreach ($n in $names) {
        $pb = Join-Path $B $n
        if (-not (Test-Path $pb)) { "{0,-34} MISSING in second set" -f $n; $anyDiff = $true; continue }
        $x = New-Object System.Drawing.Bitmap (Join-Path $A $n)
        $y = New-Object System.Drawing.Bitmap $pb
        try {
            if ($x.Width -ne $y.Width -or $x.Height -ne $y.Height) {
                "{0,-34} SIZE {1}x{2} vs {3}x{4}" -f $n, $x.Width, $x.Height, $y.Width, $y.Height; $anyDiff = $true; continue
            }
            $rect = New-Object System.Drawing.Rectangle 0, 0, $x.Width, $x.Height
            $fmt = [System.Drawing.Imaging.PixelFormat]::Format32bppArgb
            $dx = $x.LockBits($rect, 'ReadOnly', $fmt); $dy = $y.LockBits($rect, 'ReadOnly', $fmt)
            $len = $dx.Stride * $x.Height
            $bx = New-Object byte[] $len; $by = New-Object byte[] $len
            [System.Runtime.InteropServices.Marshal]::Copy($dx.Scan0, $bx, 0, $len)
            [System.Runtime.InteropServices.Marshal]::Copy($dy.Scan0, $by, 0, $len)
            $x.UnlockBits($dx); $y.UnlockBits($dy)
            $diff = 0
            for ($i = 0; $i -lt $len; $i += 4) {
                if ([math]::Abs($bx[$i] - $by[$i]) -gt $Tolerance -or [math]::Abs($bx[$i + 1] - $by[$i + 1]) -gt $Tolerance -or [math]::Abs($bx[$i + 2] - $by[$i + 2]) -gt $Tolerance) { $diff++ }
            }
            if ($diff -gt 0) { $anyDiff = $true }
            "{0,-34} {1}" -f $n, $(if ($diff -eq 0) { 'identical' } else { "$diff pixels differ" })
        }
        finally { $x.Dispose(); $y.Dispose() }
    }
    if ($anyDiff) { exit 1 }
}

if ($Compare) {
    $Compare = @($Compare | ForEach-Object { $_ -split ',' })
    if ($Compare.Count -ne 2) { throw 'Give two folders: -Compare <before>,<after>' }
    Compare-Images $Compare[0] $Compare[1]
    return
}

if (-not $Out) { throw 'Give -Out <folder> to capture, or -Compare <before> <after>.' }
if (-not (Test-Path $Exe)) { throw "Build the Debug configuration first; not found: $Exe" }
if (Get-Process TrayTrigger -ErrorAction SilentlyContinue) { throw 'Close TrayTrigger first; a running instance swallows the capture.' }
New-Item -ItemType Directory -Force $Out | Out-Null

$failed = @()
if ($Only) { $Only = @($Only | ForEach-Object { $_ -split ',' }); $Screens = @($Screens | Where-Object { $Only -contains $_[0] }) }
foreach ($screen in $Screens) {
    $name = $screen[0]
    $extra = @($screen | Select-Object -Skip 1)
    $label = (@($name) + $extra) -join ' '
    $file = (@($name) + ($extra | ForEach-Object { $_ -replace ' ', '' })) -join '-'
    $png = Join-Path $Out "$file.png"
    Remove-Item $png -ErrorAction SilentlyContinue
    $q = [char]34
    $argText = (@("--screenshot-$name") + ($extra | ForEach-Object { "$q$_$q" }) + "$q$png$q") -join ' '
    $p = Start-Process -FilePath $Exe -ArgumentList $argText -PassThru
    if (-not $p.WaitForExit($TimeoutSeconds * 1000)) { $p.Kill(); $p.WaitForExit() }
    if (Test-Path $png) { "{0,-34} ok" -f $label } else { "{0,-34} NO IMAGE" -f $label; $failed += $label }
}
''
"Captured $($Screens.Count - $failed.Count) of $($Screens.Count) into $Out"
if ($failed) { "No image for: $($failed -join ', ')" }



