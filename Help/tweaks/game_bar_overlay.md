# Disable Xbox Game Bar Overlay

## What it changes

Stops the Xbox Game Bar overlay from loading when a game starts, and turns off the Win+G shortcut.

## Why it helps

- The Game Bar runs several background processes and hooks into every game to draw its overlay. That costs roughly 150 MB of memory and can conflict with other overlays such as Steam, Discord, OBS, or RivaTuner.
- If you use another overlay for FPS counters and chat, this one is dead weight.

## Trade-offs

- You lose Game Bar's own widgets: performance counter, audio mixer, Xbox social, and the capture controls.
- Game Mode is a separate setting and is not affected.
- No restart and no administrator rights needed.

## Details

- Sets UseNexusForGameBarEnabled=0 and ShowStartupPanel=0 under HKEY_CURRENT_USER\Software\Microsoft\GameBar.
- Same as Windows Settings, Gaming, Xbox Game Bar.
