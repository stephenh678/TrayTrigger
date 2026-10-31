# Security Policy

TrayTrigger can modify Windows registry settings, create/manage a power plan, and optionally add a Microsoft Defender exclusion. This document explains what that access is used for, what it isn't used for, and how to report a problem.

## Supported Versions

Only the latest published [release](https://github.com/stephenh678/TrayTrigger/releases) is supported. Please update before reporting an issue.

## Reporting a Vulnerability

Please report security issues privately using [GitHub Security Advisories](https://github.com/stephenh678/TrayTrigger/security/advisories/new) rather than a public issue. If that isn't available to you, open a regular issue without exploit details and ask to be contacted privately.

You can expect an initial response within a few days. This is a solo-maintained project, so please be patient with turnaround time.

## Does TrayTrigger collect telemetry or personal data?

No. TrayTrigger does not collect analytics, usage data, or any personal information, and nothing about your library, hardware, or activity is transmitted anywhere, apart from your GPU's model when you ask NVIDIA for a newer driver (below).

The app's only outbound network calls are:
- **Steam's public APIs** — to fetch metadata (name, description, release date, Metacritic score) for games you've added.
- **SteamGridDB** — only if you enter your own API key in Settings, to fetch box art/poster images, and the wide hero banner behind a game's page, for games you've added. The key is stored encrypted (DPAPI) and only sent to SteamGridDB.
- **RAWG (rawg.io)** — only if you enable it in Settings with your own API key, to fetch developer, publisher, synopsis and ratings for games that aren't on Steam. The key is stored encrypted (DPAPI) and only sent to RAWG.
- **NVIDIA's driver service** — only when you press **Check for Newer Driver** on an NVIDIA GPU in System > Hardware Specs, to ask which Game Ready driver is newest for it. The request names your GPU model and nothing else, and TrayTrigger never makes it on its own. NVIDIA's product list is kept for a week so the lookup doesn't fetch it each time. The newest driver it finds is kept in your settings so the notice survives a restart; it is not sent anywhere.
- **GitHub Releases** — to check whether a newer version of TrayTrigger is available. Update checks can be turned off in Settings.

No game library contents, hardware telemetry, or usage data are sent as part of any of these calls.

## Administrator access and elevation

TrayTrigger does **not** run elevated by default. Some individual tweaks write to `HKEY_LOCAL_MACHINE` or manage Microsoft Defender exclusions via the supported `Add-MpPreference`/`Remove-MpPreference` PowerShell cmdlets, and Windows will prompt for a UAC elevation only when one of those specific tweaks is applied. Tweaks that only touch the current user's settings (`HKEY_CURRENT_USER`) never require elevation.

One change cannot be made by Windows' own tools: the NVIDIA driver only lets an administrator change its Resizable BAR setting (Aggressive performance profile). For that, TrayTrigger starts **itself** elevated, once, under the same UAC prompt as the registry changes (`--elevated-apply`, then it exits), so that prompt names TrayTrigger and shows an unknown publisher: releases aren't code-signed. The elevated run does only what is written in `Services/ElevatedHelper.cs`: it accepts only that one NVIDIA setting with the values off, on, delete or restore-default, and a `.reg` file named the way TrayTrigger names its own that holds nothing but the two Windows scheduler values a profile writes (System Responsiveness, and the Games task's Scheduling Category) with the values Windows defines. It reads that file once, checks every line, refuses the whole file if any line is anything else, and applies exactly what it read, so the file's name and place decide nothing; the file stays locked against changes while it is read. It takes no command from anywhere else.

## Are changes reversible?

Yes. Every tweak TrayTrigger applies — whether a global tweak from Settings or a per-game performance profile — records the prior state before changing anything and restores that exact prior state automatically (on toggle-off, or the moment a game with a session profile exits). If TrayTrigger or your PC crashes mid-session, the next launch detects the incomplete session and restores your pre-game state. What needs administrator rights (the Aggressive profile's ADMIN tweaks and a Defender exclusion) isn't restored without asking: TrayTrigger says what is still changed and offers "Put back now" in its own dialog, at start and before it closes, and the Windows prompt follows your click. Activity & History keeps a reminder with Restore Previous meanwhile.

You can also enable **"Create a System Restore point before applying tweaks"** in Settings (on by default) as an additional safety net before any bulk Apply Preset or Restore Previous Settings.

## Microsoft Defender exclusion

The optional per-game Defender exclusion (part of the Aggressive performance profile) is:
- **Off by default.** You must explicitly enable it in Settings.
- **Scoped to one game's executable**, not a folder or drive-wide exclusion.
- **Session-scoped and reversible** — removed automatically when the game closes, unless that exact path was already excluded before TrayTrigger touched it (in which case TrayTrigger leaves it alone rather than removing something it didn't add).

## Scripts and hotkeys

The optional pre-launch/post-exit script feature (disabled by default; must be enabled in Settings) runs a `.bat`, `.cmd`, `.ps1`, or `.exe` file **that you explicitly attach to a specific game**. TrayTrigger does not download, generate, or execute any script on your behalf — you are always the one supplying the script and its contents are entirely your responsibility.

## Verifying a release

Release binaries are built only by the public GitHub Actions workflow in [`.github/workflows/release.yml`](.github/workflows/release.yml) on GitHub-hosted runners; nothing is built on a developer machine. Each release publishes `TrayTrigger-v*-Setup.exe`, the portable `.zip`, a `SHA256SUMS.txt` covering both, and a signature of that file, `SHA256SUMS.txt.sig`. The installer itself is not code-signed (no publisher certificate), which is why Windows SmartScreen may warn on first run.

From 1.4.5, `SHA256SUMS.txt` is signed by the release workflow. The in-app updater checks that signature against public keys built into TrayTrigger and refuses an update without a valid one, so replacing both the installer and its checksum on a release is not enough to get an update installed. Just before running the installer it checks the file again against the hash it was verified with, and it will not run one it has no verified hash for.

To check it yourself, download `SHA256SUMS.txt`, `SHA256SUMS.txt.sig` and [`release-signing-key.pub.pem`](release-signing-key.pub.pem), then run (OpenSSL ships with Git for Windows):
```powershell
openssl dgst -sha256 -verify release-signing-key.pub.pem -signature SHA256SUMS.txt.sig SHA256SUMS.txt
```
`Verified OK` means the checksums came from the release workflow. A release signed with the offline backup key verifies against [`release-signing-backup-key.pub.pem`](release-signing-backup-key.pub.pem) instead.
