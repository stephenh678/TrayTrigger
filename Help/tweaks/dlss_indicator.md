# NVIDIA DLSS Indicator

## What it changes

Turns on NVIDIA's own on-screen overlay for DLSS. While it is on, every game that uses DLSS draws a few lines of text in a corner of the screen: the DLSS version in use, the preset letter, and the render and output resolution.

## Why it helps

- It is the one place the active preset letter is shown. Nothing else reports it.
- It is the quickest way to confirm that a DLSS Override set in Edit Game is really in effect: the version on screen should be the newer one from your GeForce driver, not the one inside the game.

## Trade-offs

- For testing, not for leaving on. It draws over the game, and it shows in every DLSS game on this PC, not just one, until you turn it off here.
- Needs administrator rights, because the setting lives in a part of the registry shared by all users. No restart is needed; start the game after switching it on.
- Opt-in, and never part of the recommended preset.
- NVIDIA only. On a PC without a GeForce driver that supports DLSS the toggle is unavailable.

## Details

- Writes ShowDlssIndicator=0x400 (1024) under HKEY_LOCAL_MACHINE\SOFTWARE\NVIDIA Corporation\Global\NGXCore. NVIDIA's own registry file sets 1, which only draws in developer builds; 1024 is the value that allows a retail game to draw it.
- Restore Previous puts back whatever was there before. If the value did not exist, it is removed rather than set to 0.
