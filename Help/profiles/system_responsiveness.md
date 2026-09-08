# System Responsiveness (profile)

## What it changes

Lowers the share of CPU time Windows reserves for low-priority background work, from the default 20 percent to 10 percent, while the game runs.

## Why it helps

- The Multimedia Class Scheduler Service keeps a slice of CPU back for background tasks so they are never starved. During a game that reserve is time the game could be using.
- Microsoft's documentation states that values below 10 are clamped back up to 20, so 10 is the lowest reserve Windows actually honours. TrayTrigger uses that value rather than the 0 that many guides suggest.

## Trade-offs

- Background tasks get less CPU during the session. Downloads, indexing, and similar work may run slower until the game exits.
- Effect depends on how much else is competing for the CPU. On an idle system it changes nothing.

> Requires administrator rights the first time it is applied. Aggressive tier only.

## Details

- Sets SystemResponsiveness=10 under HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile, and restores the previous value on exit.
