# Windows Visual Effects (Performance Mode)

## What it changes

Turns off two desktop effects the Desktop Window Manager pays for on every frame: the minimize and restore window animation, and window drop shadows.

## Why it helps

- Frees a small amount of compositor GPU time, which matters most on integrated graphics and older GPUs.

## Trade-offs

- On a modern GPU the difference in a game is marginal. This is a visual-polish-for-a-small-gain trade, so it is opt-in.
- Everything else in Performance Options (font smoothing, taskbar animations, transparency) is left alone.
- No restart and no administrator rights needed.

## Details

- Uses SystemParametersInfo (SPI_SETANIMATION and SPI_SETDROPSHADOW), the same calls the Performance Options dialog makes, and marks VisualFXSetting=3 ("Custom") under HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects so that dialog does not later apply its full "best performance" set on top.
- TrayTrigger records what both effects were set to before applying and restores exactly that on revert.
