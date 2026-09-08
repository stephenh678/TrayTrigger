# Disable Mouse Acceleration

## What it changes

Turns off Windows "Enhance pointer precision", so the cursor moves the same distance for the same physical mouse movement regardless of how fast you move it.

## Why it helps

- With acceleration on, a fast flick moves the crosshair further than a slow one covering the same distance on the pad. Your aim depends on speed, not just distance.
- Competitive shooters such as CS2, Valorant, and Apex assume raw 1:1 input. Consistent movement is what builds muscle memory.
- Most games read raw input and bypass this setting anyway, but desktop apps, launchers, and some older engines do not.

## Trade-offs

- Some people prefer acceleration on the desktop for reaching across large or multiple monitors. Pick one and stick with it.
- No restart and no administrator rights needed.

## Details

- Sets MouseSpeed, MouseThreshold1, and MouseThreshold2 to 0 under HKEY_CURRENT_USER\Control Panel\Mouse.
- Same setting as Windows Settings, Mouse, Additional mouse settings, Pointer Options.
