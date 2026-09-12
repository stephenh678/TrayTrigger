<#
.SYNOPSIS
    Generates the GitHub wiki from the in-app help pages under Help/.

.DESCRIPTION
    Help/<section>/<name>.md is the single source of truth for both the app's "Learn more"
    dialogs and the wiki. This script writes one wiki page per help topic, plus Home.md,
    _Sidebar.md and _Footer.md, into a clone of the wiki repository. Wiki edits made on
    github.com are overwritten on the next sync, and every page says so in its footer.

    Section order and labels mirror HelpContentService.SectionOrder.

.PARAMETER OutDir
    A clone of https://github.com/stephenh678/TrayTrigger.wiki.git (or any folder). Existing
    *.md files that no longer correspond to a help topic are removed, so a renamed topic does
    not leave a stale page behind.

.EXAMPLE
    git clone https://github.com/stephenh678/TrayTrigger.wiki.git ..\TrayTrigger.wiki
    .\tools\Export-Wiki.ps1 -OutDir ..\TrayTrigger.wiki
    git -C ..\TrayTrigger.wiki add -A; git -C ..\TrayTrigger.wiki commit -m "Sync from Help/"; git -C ..\TrayTrigger.wiki push
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $OutDir,
    [string] $Repo = 'stephenh678/TrayTrigger',
    [string] $Branch = 'main'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$helpDir = Join-Path $root 'Help'
if (-not (Test-Path $helpDir)) { throw "Help folder not found at $helpDir" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# Same order and labels as HelpContentService.SectionOrder.
$sectionOrder = @(
    @{ Key = 'tweaks';          Label = 'Performance Tweaks' },
    @{ Key = 'profiles';        Label = 'Performance Profiles' },
    @{ Key = 'scanner';         Label = 'Game Scanner' },
    @{ Key = 'traymenu';        Label = 'Tray Menu' },
    @{ Key = 'scripts';         Label = 'Game Scripts' },
    @{ Key = 'library';         Label = 'Library & Artwork' },
    @{ Key = 'updates';         Label = 'Updates' },
    @{ Key = 'troubleshooting'; Label = 'Troubleshooting' }
)

# A wiki page's URL is its file name; GitHub turns hyphens back into spaces for display.
function ConvertTo-PageName([string] $title) {
    $t = $title -replace '["`]', ''                 # quotes never survive a URL cleanly
    $t = $t -replace '[/\\:*?<>|]', ' '             # not allowed in file names
    $t = $t -replace ' - ', ' '                     # a spaced dash would become three hyphens in the URL
    $t = $t -replace '\s+', ' '
    return $t.Trim() -replace ' ', '-'
}

$utf8 = [System.Text.UTF8Encoding]::new($false)
function Write-Page([string] $name, [string] $body) {
    [System.IO.File]::WriteAllText((Join-Path $OutDir "$name.md"), $body, $utf8)
}

# Collect topics.
$topics = @()
foreach ($file in Get-ChildItem $helpDir -Recurse -Filter *.md | Sort-Object FullName) {
    $rel = $file.FullName.Substring($helpDir.Length + 1) -replace '\\', '/'
    $section = ($rel -split '/')[0]
    $lines = Get-Content $file.FullName -Encoding UTF8
    $title = ($lines | Where-Object { $_ -match '^# ' } | Select-Object -First 1) -replace '^# ', ''
    if (-not $title) { $title = [System.IO.Path]::GetFileNameWithoutExtension($file.Name) }
    $topics += [pscustomobject]@{
        Section  = $section
        Source   = "Help/$rel"
        Title    = $title
        Page     = ConvertTo-PageName $title
        IsOverview = $file.BaseName -eq 'overview'
        # The wiki shows the page name as its title, so the "# Title" line would appear twice.
        Body     = (($lines | Where-Object { $_ -notmatch '^# ' }) -join "`n").Trim()
        Note     = "This page is the same text as the app's Learn more button"
    }
}

# One page per bundled example script, from the script's own header block. The header is the
# documentation the user sees in the scripts folder, so the wiki page is generated from it
# rather than written twice.
$scriptsDir = Join-Path $root 'Scripts\Library'
foreach ($file in Get-ChildItem $scriptsDir -Filter 'Example-*.ps1' | Sort-Object Name) {
    $lines = Get-Content $file.FullName -Encoding UTF8
    $end = [Array]::IndexOf($lines, ($lines | Where-Object { $_ -match '^#>' } | Select-Object -First 1))
    if ($lines[0] -notmatch '^<#' -or $end -lt 1) { continue }
    $header = $lines[1..($end - 1)]
    $fields = [ordered]@{}; $sections = [ordered]@{}; $current = $null
    foreach ($l in $header) {
        if ($l -match '^  ([A-Z][A-Z ]+)$') { $current = $Matches[1].Trim(); $sections[$current] = New-Object System.Collections.Generic.List[string]; continue }
        if ($null -eq $current -and $l -match '^  ([A-Za-z ]+):\s+(.*)$') { $fields[$Matches[1]] = $Matches[2].Trim(); continue }
        if ($null -ne $current) { $sections[$current].Add(($l -replace '^    ', '')) }
    }
    $name = if ($fields['Name']) { $fields['Name'] } else { $file.BaseName -replace '^Example-', '' }
    $title = "Script: $name"
    $body = New-Object System.Collections.Generic.List[string]
    if ($fields['Description']) { $body.Add($fields['Description']); $body.Add('') }
    $body.Add('| | |'); $body.Add('|---|---|')
    foreach ($k in 'Phase', 'Needs admin', 'Dependencies', 'Script Arguments', 'Version') {
        if ($fields[$k]) { $body.Add("| **$k** | $($fields[$k]) |") }
    }
    $body.Add("| **File** | ``$($file.Name)`` in ``%AppData%\TrayTrigger\Scripts`` |")
    $body.Add('')
    foreach ($s in $sections.Keys) {
        $text = ($sections[$s] -join "`n").Trim()
        if (-not $text) { continue }
        $body.Add("## $((Get-Culture).TextInfo.ToTitleCase($s.ToLowerInvariant()))")
        $body.Add('')
        $body.Add('```text'); $body.Add($text); $body.Add('```')
        $body.Add('')
    }
    $body.Add('## Full source')
    $body.Add('')
    $body.Add("<details><summary>$($file.Name) ($($lines.Count) lines)</summary>")
    $body.Add('')
    $body.Add('```powershell'); $body.Add(($lines -join "`n").TrimEnd()); $body.Add('```')
    $body.Add('')
    $body.Add('</details>')
    $topics += [pscustomobject]@{
        Section  = 'scripts'
        Source   = "Scripts/Library/$($file.Name)"
        Title    = $title
        Page     = ConvertTo-PageName $title
        IsOverview = $false
        Body     = ($body -join "`n")
        Note     = "The text above is the script's own header comment"
    }
}

$dupes = $topics | Group-Object Page | Where-Object Count -gt 1
if ($dupes) { throw "Two help topics map to the same wiki page name: $($dupes.Name -join ', ')" }

# Group by section in display order, overview first then alphabetical (as the app's index does).
$groups = @()
$known = $sectionOrder | ForEach-Object { $_.Key }
$extra = $topics.Section | Sort-Object -Unique | Where-Object { $_ -notin $known }
foreach ($s in ($sectionOrder + ($extra | ForEach-Object { @{ Key = $_; Label = (Get-Culture).TextInfo.ToTitleCase($_) } }))) {
    $items = $topics | Where-Object Section -eq $s.Key | Sort-Object @{ e = { -not $_.IsOverview } }, @{ e = { $_.Title -replace '"', '' } }
    if ($items) { $groups += [pscustomobject]@{ Label = $s.Label; Key = $s.Key; Items = @($items) } }
}

# Remove pages that no longer have a topic (keeps renames clean).
$keep = @('Home', '_Sidebar', '_Footer') + ($topics | ForEach-Object { $_.Page })
Get-ChildItem $OutDir -Filter *.md | Where-Object { $_.BaseName -notin $keep } | Remove-Item -Force

# One page per topic.
foreach ($t in $topics) {
    $src = "https://github.com/$Repo/blob/$Branch/$($t.Source)"
    $group = $groups | Where-Object Key -eq $t.Section
    $body = @(
        $t.Body
        ''
        '---'
        "_Part of **$($group.Label)**. $($t.Note), generated from [``$($t.Source)``]($src). To fix something, edit that file (or open an issue); wiki edits are overwritten on the next sync._"
        ''
    ) -join "`n"
    Write-Page $t.Page $body
}

# Home.
$homePage = New-Object System.Collections.Generic.List[string]
$homePage.Add('# TrayTrigger Wiki')
$homePage.Add('')
$homePage.Add('Per-game Windows tuning and launch automation for PC gamers. Performance profiles, your own pre-launch and post-exit scripts, launch arguments, and launchers that close themselves. Everything reverts when the game exits. Steam, GOG, Epic, EA, Ubisoft, Xbox. Lives in your tray.')
$homePage.Add('')
$homePage.Add("**[Download the latest release](https://github.com/$Repo/releases/latest)** | [README](https://github.com/$Repo#readme) | [FAQ](https://github.com/$Repo#faq) | [Changelog](https://github.com/$Repo/blob/$Branch/CHANGELOG.md) | [Discussions](https://github.com/$Repo/discussions) | [Report a bug](https://github.com/$Repo/issues/new/choose)")
$homePage.Add('')
$homePage.Add('This wiki is the reference for every setting, tweak and profile option in the app. Each page is the same text the app shows when you click **Learn more**, so what you read here is exactly what the app does.')
$homePage.Add('')
foreach ($g in $groups) {
    $homePage.Add("## $($g.Label)")
    $homePage.Add('')
    foreach ($t in $g.Items) { $homePage.Add("- [$($t.Title)]($($t.Page))") }
    $homePage.Add('')
}
$homePage.Add('---')
$homePage.Add("_Generated from the [``Help/``](https://github.com/$Repo/tree/$Branch/Help) folder by [``tools/Export-Wiki.ps1``](https://github.com/$Repo/blob/$Branch/tools/Export-Wiki.ps1). Edit the source files, not the wiki._")
$homePage.Add('')
Write-Page 'Home' ($homePage -join "`n")

# Sidebar: compact, sections collapsed except their overview.
$side = New-Object System.Collections.Generic.List[string]
$side.Add('**[Home](Home)**')
$side.Add('')
foreach ($g in $groups) {
    $side.Add('<details>')
    $side.Add("<summary><b>$($g.Label)</b></summary>")
    $side.Add('')
    foreach ($t in $g.Items) { $side.Add("- [$($t.Title)]($($t.Page))") }
    $side.Add('')
    $side.Add('</details>')
    $side.Add('')
}
$side.Add("[Download](https://github.com/$Repo/releases/latest) | [Issues](https://github.com/$Repo/issues) | [Discussions](https://github.com/$Repo/discussions)")
$side.Add('')
Write-Page '_Sidebar' ($side -join "`n")

# Footer.
Write-Page '_Footer' (@(
    "[TrayTrigger](https://github.com/$Repo) | [Download](https://github.com/$Repo/releases/latest) | [Report a bug](https://github.com/$Repo/issues/new/choose) | [Security policy](https://github.com/$Repo/blob/$Branch/SECURITY.md) | Free and open source (MIT)"
    ''
) -join "`n")

Write-Host "Wrote $($topics.Count) topic pages + Home, _Sidebar, _Footer to $OutDir"
