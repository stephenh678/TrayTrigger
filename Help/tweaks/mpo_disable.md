# Disable Multiplane Overlay (MPO)

## What it changes

Turns off the Desktop Window Manager multiplane overlay path, where the display controller composes a game window directly instead of the desktop compositor doing it.

## Why it helps

- On some GPU, driver, and monitor combinations that path causes intermittent stutter, flickering, or a brief black screen when a borderless game sits on top of other windows.
- This registry value is the workaround NVIDIA documents for those symptoms, and it applies to AMD and Intel systems too.

## Trade-offs

- The overlay path normally saves a little GPU work and a little latency, so turn this on only if you actually see the symptoms.
- Newer drivers have fixed most of the original cases.
- Opt-in, off by default, and skipped by Apply Performance Preset.

> Requires administrator rights and a restart.

## Details

- Sets OverlayTestMode=5 under HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\Dwm. Revert deletes the value, which is the Windows default.
