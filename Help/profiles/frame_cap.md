# Frame Cap Just Under Your Refresh Rate (profile)

## What it changes

Caps the frame rate a little under the primary display's refresh rate while you play, then removes the cap when the last game exits. For a G-SYNC or FreeSync monitor.

## Why it helps

- Variable refresh only works while the frame rate stays under the refresh rate. When a game runs past it, the display falls back to V-Sync, which adds input lag, or to tearing.
- A cap just underneath keeps every frame inside the variable refresh range, and frame times come out more even. Testing by Blur Busters and NVIDIA found it's the lowest-latency way to run G-SYNC.
- The cap is the formula NVIDIA Reflex uses for its own automatic cap: refresh rate minus refresh rate squared over 3600. That gives 59 fps at 60 Hz, 138 at 144 Hz, 157 at 165 Hz, 224 at 240 Hz and 324 at 360 Hz.

## Trade-offs

- On a screen without variable refresh, a cap below the refresh rate makes frames judder. Only turn this on with a G-SYNC or FreeSync monitor.
- You give up the frames above the cap. Some competitive players prefer every frame, even with tearing.
- It uses the primary display's refresh rate. If you play on a second display with a different refresh rate, set the cap yourself instead.
- NVIDIA GPUs only, and opt-in.

## Details

- Sets the NVIDIA Control Panel's Max Frame Rate (setting 0x10835002) on the driver's Global profile when the first game launches, and removes it when the last one exits.
- If you already have a Max Frame Rate set, TrayTrigger leaves it alone and sets nothing. A game with its own frame limit can still cap lower.
- If the cap is changed elsewhere during the session, TrayTrigger leaves that change alone.
- The cap it set is kept in the crash-recovery record, so it's removed on the next start if TrayTrigger closes during a game.
