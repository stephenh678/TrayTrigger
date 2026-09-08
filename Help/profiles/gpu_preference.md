# High-Performance GPU Preference (profile)

## What it changes

Registers the game's exe with Windows as "High performance" under Graphics settings for the duration of the session, so it runs on the discrete GPU rather than an integrated one.

## Why it helps

- On laptops and some desktops with both an integrated and a discrete GPU, Windows or the driver can send an unrecognised game to the integrated one. The result is very low frame rates with no obvious cause.
- This writes the exact per-exe preference that the Windows Settings page writes, so the discrete GPU is chosen without you having to add the game by hand.

## Trade-offs

- No effect on a single-GPU desktop.
- Windows reads this preference when the game creates its graphics device, which is why the profile applies it before the game starts.
- Not available for Steam-launched games, because their launch target is a steam:// link rather than an exe path.

## Details

- Writes GpuPreference=2 for the exe path under HKEY_CURRENT_USER\Software\Microsoft\DirectX\UserGpuPreferences.
- On exit the previous value is restored, or the entry is removed if there was none.
