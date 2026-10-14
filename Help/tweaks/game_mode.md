# Windows Game Mode

## What it changes

Enables Windows Game Mode, which Windows turns on automatically when it detects a game running.

## Why it helps

- Windows Update will not install drivers or send restart notifications while a game is active.
- Background processes get less CPU time, and the game's threads are prioritised on the main cores.
- Microsoft's default and their recommended state for gaming PCs.
- On a dual-CCD Ryzen X3D (7900X3D, 7950X3D, 9950X3D), AMD's chipset driver relies on Windows recognizing the game to place it on the cores with the extra cache. With Game Mode off, that placement can be lost. A game Windows doesn't recognize as a game, such as an emulator or some borderless titles, can miss out too; Game Bar (Win+G) lets you mark it as a game by hand. Single-CCD chips such as the 7800X3D and 9800X3D don't depend on this.

## Trade-offs

- On a small number of older systems Game Mode caused stutter. That was largely fixed in Windows 10 builds after 2019, but if a game stutters with it on, try it off.
- Streaming or recording software may get less CPU time while a game runs.
- No restart and no administrator rights needed.

## Details

- Sets AllowAutoGameMode and AutoGameModeEnabled to 1 under HKEY_CURRENT_USER\Software\Microsoft\GameBar.
- Same setting as Windows Settings, Gaming, Game Mode.
