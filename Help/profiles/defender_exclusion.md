# Windows Defender Exclusion (profile)

## What it changes

Adds the game's exe to Microsoft Defender's real-time scanning exclusions when the game launches, and removes it again when the game exits.

## Why it helps

- Real-time scanning inspects files as the game reads them. During shader compilation and heavy asset streaming that can cause CPU and disk hitches on some games.
- Excluding the exe for the session removes that overhead without leaving a permanent hole in your protection.

## Trade-offs

> This narrows antivirus coverage for that file while the game runs. It is a security decision, not just a performance one, which is why it stays off even under Aggressive until you turn it on.

- Each apply and remove goes through an elevated PowerShell command, so expect a UAC prompt at launch and again at exit.
- Not available for Steam-launched games, because their launch target is a steam:// link rather than an exe path.
- If the exe was already excluded before the session, TrayTrigger leaves that exclusion in place on exit.

## Details

- Uses Microsoft's supported Add-MpPreference and Remove-MpPreference cmdlets. Tamper Protection blocks direct registry changes to the exclusion list, so this is the only reliable route.
- Aggressive tier only. Opt-in.
