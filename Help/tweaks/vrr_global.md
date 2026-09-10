# Variable Refresh Rate for Windowed Games

## What it changes

Lets Windows 11 engage G-SYNC, FreeSync, or Adaptive-Sync for windowed and borderless DirectX 11 games, not only exclusive fullscreen ones.

## Why it helps

- Most modern games run borderless. Without this, a borderless DX11 game on a VRR monitor can fall back to a fixed refresh rate, bringing tearing or judder back, unless the GPU driver forces windowed VRR on its own.
- Costs nothing when the display is not VRR-capable, which is why it is part of the recommended set.

## Trade-offs

- Windows 11 only. On Windows 10 the setting has no effect.
- A small number of games misbehave with windowed VRR; Windows lets you exclude them per game on the same settings page.
- No restart and no administrator rights needed.

## Details

- Adds VRROptimizeEnable=1 to DirectXUserGlobalSettings under HKEY_CURRENT_USER\Software\Microsoft\DirectX\UserGpuPreferences. Revert removes the token.
- Same as Windows Settings, Display, Graphics, Default graphics settings, Variable refresh rate.
