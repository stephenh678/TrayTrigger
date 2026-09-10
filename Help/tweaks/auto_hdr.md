# Auto HDR

## What it changes

Turns on Windows 11 Auto HDR, which renders DirectX 11 and 12 games that only output SDR in HDR on an HDR display.

## Why it helps

- Highlights and colour range are expanded at the compositor, the same tone-mapping Xbox Series consoles use, so older games gain HDR without a patch.
- Works per game and can be excluded per game from the same Graphics settings page in Windows.

## Trade-offs

- Purely a visual change. It does not raise frame rates.
- On a display with weak HDR (low peak brightness, no local dimming) it can look washed out. That is why it is opt-in here.
- Only does anything when the display HDR mode is on. Pair it with the Enable HDR option under Performance Profiles to switch HDR on only while a game runs.
- Windows 11 only, and only offered when a connected display reports HDR support.

## Details

- Adds AutoHDREnable=1 to DirectXUserGlobalSettings under HKEY_CURRENT_USER\Software\Microsoft\DirectX\UserGpuPreferences. Revert removes the token.
- Same as Windows Settings, Display, Graphics, Default graphics settings, Auto HDR.
