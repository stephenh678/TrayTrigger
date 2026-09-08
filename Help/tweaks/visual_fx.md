# Windows Visual Effects

## What it changes

Switches Windows to "Adjust for best performance", which turns off window animations, drop shadows, fade effects, and other desktop eye candy.

## Why it helps

- The Desktop Window Manager does slightly less compositing work, freeing a small amount of GPU time.
- On very old or integrated GPUs the difference can be noticeable on the desktop.

## Trade-offs

- On any modern GPU the gaming impact is close to zero. The desktop just looks plainer.
- This is a "looks versus a tiny gain" decision, which is why it is OPT-IN and left out of Apply Performance Preset.
- No restart and no administrator rights needed.

## Details

- Sets VisualFXSetting=2 under HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects.
- Same setting as System Properties, Advanced, Performance Settings.
