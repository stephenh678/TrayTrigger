# Security Policy

TrayTrigger can modify Windows registry settings, create/manage a power plan, and optionally add a Microsoft Defender exclusion. This document explains what that access is used for, what it isn't used for, and how to report a problem.

## Supported Versions

Only the latest published [release](https://github.com/stephenh678/TrayTrigger/releases) is supported. Please update before reporting an issue.

## Reporting a Vulnerability

Please report security issues privately using [GitHub Security Advisories](https://github.com/stephenh678/TrayTrigger/security/advisories/new) rather than a public issue. If that isn't available to you, open a regular issue without exploit details and ask to be contacted privately.

You can expect an initial response within a few days. This is a solo-maintained project, so please be patient with turnaround time.

## Does TrayTrigger collect telemetry or personal data?

No. TrayTrigger does not collect analytics, usage data, or any personal information, and nothing about your library, hardware, or activity is transmitted anywhere.

The app's only outbound network calls are:
- **Steam's public APIs** — to fetch metadata (name, description, release date, Metacritic score) for games you've added.
- **SteamGridDB** — to fetch box art/poster images for games you've added.
- **GitHub Releases** — to check whether a newer version of TrayTrigger is available.

No game library contents, hardware telemetry, or usage data are sent as part of any of these calls.

## Administrator access and elevation

TrayTrigger does **not** run elevated by default. Some individual tweaks write to `HKEY_LOCAL_MACHINE` or manage Microsoft Defender exclusions via the supported `Add-MpPreference`/`Remove-MpPreference` PowerShell cmdlets, and Windows will prompt for a UAC elevation only when one of those specific tweaks is applied. Tweaks that only touch the current user's settings (`HKEY_CURRENT_USER`) never require elevation.

## Are changes reversible?

Yes. Every tweak TrayTrigger applies — whether a global tweak from Settings or a per-game performance profile — records the prior state before changing anything and restores that exact prior state automatically (on toggle-off, or the moment a game with a session profile exits). If TrayTrigger or your PC crashes mid-session, the next launch detects the incomplete session and restores your pre-game state.

You can also enable **"Create a System Restore point before applying tweaks"** in Settings (on by default) as an additional safety net before any bulk Apply Preset or Reset Defaults.

## Microsoft Defender exclusion

The optional per-game Defender exclusion (part of the Aggressive performance profile) is:
- **Off by default.** You must explicitly enable it in Settings.
- **Scoped to one game's executable**, not a folder or drive-wide exclusion.
- **Session-scoped and reversible** — removed automatically when the game closes, unless that exact path was already excluded before TrayTrigger touched it (in which case TrayTrigger leaves it alone rather than removing something it didn't add).

## Scripts and hotkeys

The optional pre-launch/post-exit script feature (disabled by default; must be enabled in Settings) runs a `.bat`, `.cmd`, `.ps1`, or `.exe` file **that you explicitly attach to a specific game**. TrayTrigger does not download, generate, or execute any script on your behalf — you are always the one supplying the script and its contents are entirely your responsibility.
