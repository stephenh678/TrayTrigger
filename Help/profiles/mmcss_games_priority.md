# MMCSS Games Task Priority (profile)

## What it changes

Raises the Multimedia Class Scheduler's built-in "Games" task from its default Medium scheduling category to High while the game runs.

## Why it helps

- Game engines can register their time-critical threads with MMCSS under the Games category. Windows then gives those threads priority CPU access.
- Raising the category from Medium to High is the same sanctioned mechanism with more headroom. It is scheduling tuning, not a frame-rate boost, and the effect depends on what else is contending for the CPU.

## Trade-offs

- Only engines that use the MMCSS Games task benefit. Many do, but not all.
- Other values under the same key, such as SFIO Priority, are documented by Microsoft as unused. TrayTrigger leaves them alone even though many optimiser tools change them.

> Requires administrator rights the first time it is applied. Aggressive tier only.

## Details

- Sets Scheduling Category=High under HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games, and restores the previous value on exit.
