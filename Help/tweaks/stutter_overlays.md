# Overlays: keep one

## Why it matters

Every overlay hooks into the game's rendering to draw over it. One is fine. Two or more fight over the same hook, and the result is the stutter and hitching that fix threads blame first. Pick the overlay you actually look at and turn the rest off.

## Where each switch is

- **Discord**: User Settings > Game Overlay > Enable in-game overlay. Discord's hardware acceleration is a separate switch under Advanced; see the Browser and chat apps page.
- **NVIDIA App**: Settings > Features > In-game overlay. The statistics overlay and Instant Replay turn off with it.
- **RivaTuner (RTSS)**: in RTSS, set "Show On-Screen Display" to Off, or add the game to its profiles with the OSD off. MSI Afterburner's OSD runs through RTSS.
- **Medal**: Settings > Overlay.
- **Overwolf**: Settings > Overlay & Hotkeys, per app.
- **Xbox Game Bar**: TrayTrigger's Disable Xbox Game Bar Overlay tweak turns it off, or Windows Settings > Gaming > Game Bar.
- **Steam**: Settings > In Game > Enable the Steam overlay while in-game. Steam's overlay only exists while a game runs, so Stutter Check doesn't count it; if you keep one overlay, this is a good one to keep.

## Details

Stutter Check finds overlays by their running processes, so an app that is closed before you play won't be listed even if its overlay is on.
