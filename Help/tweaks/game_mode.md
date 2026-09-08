# Windows Game Mode

## What it changes

Enables Windows Game Mode, which Windows turns on automatically when it detects a game running.

## Why it helps

- Windows Update will not install drivers or send restart notifications while a game is active.
- Background processes get less CPU time, and the game's threads are prioritised on the main cores.
- Microsoft's default and their recommended state for gaming PCs.

## Trade-offs

- On a small number of older systems Game Mode caused stutter. That was largely fixed in Windows 10 builds after 2019, but if a game stutters with it on, try it off.
- Streaming or recording software may get less CPU time while a game runs.
- No restart and no administrator rights needed.

## Details

- Sets AllowAutoGameMode and AutoGameModeEnabled to 1 under HKEY_CURRENT_USER\Software\Microsoft\GameBar.
- Same setting as Windows Settings, Gaming, Game Mode.
