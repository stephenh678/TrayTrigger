# Optimizations for Windowed Games

## What it changes

Enables Windows' "Optimizations for windowed games". Borderless and windowed DirectX 10 and 11 games are presented through the modern flip model instead of the older blit path.

## Why it helps

- Flip-model presentation gives windowed games the same low input latency as exclusive fullscreen.
- You keep instant alt-tab with no black screen or monitor mode reset.
- Enables Auto HDR and variable refresh rate in windowed games where supported.

## Trade-offs

- Windows 11 only. On Windows 10 the setting has no effect.
- A small number of games misbehave with it. Windows lets you exclude individual games in the same Settings page.
- No restart and no administrator rights needed.

## Details

- Adds SwapEffectUpgradeEnable=1 to DirectXUserGlobalSettings under HKEY_CURRENT_USER\Software\Microsoft\DirectX\UserGpuPreferences.
- Same setting as Windows Settings, Display, Graphics, Default graphics settings.
