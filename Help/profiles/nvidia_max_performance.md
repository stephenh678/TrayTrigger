# NVIDIA: Prefer Maximum Performance (profile)

## What it changes

Sets the NVIDIA driver's power management mode to "Prefer maximum performance" when the game launches, and puts back what you had when the last game exits.

## Why it helps

- By default the driver lowers the GPU's clocks whenever a scene asks less of it, and takes a moment to raise them again when the action picks up. That moment shows up as an uneven frame time.
- Keeping the clocks up steadies the 1% lows. It matters most in lighter or CPU-bound games, where the GPU keeps dropping into its lower clocks. The average frame rate barely changes.

## Trade-offs

- The GPU draws more power and runs warmer while you play. Once the last game exits the setting goes back, and the GPU idles normally.
- A few setups report stutter with this on, usually with a manual GPU overclock. If a game feels worse, switch this off.
- NVIDIA GPUs only. On AMD or Intel graphics it does nothing.

## Details

- Writes setting 0x1057EB71 ("Power management mode") = 1, Prefer maximum performance, on the driver's Global profile. That's the same setting as Manage 3D settings in the NVIDIA Control Panel. Using the Global profile means it covers every launch, Steam games included.
- A game whose own NVIDIA profile sets a power management mode keeps its own.
- Applied by the first game to launch and restored when the last one exits, like the power plan. If the setting is changed elsewhere during the session, for example in the NVIDIA App, TrayTrigger leaves that change alone.
- If it's already Prefer maximum performance, nothing is changed or recorded.
- The value it found is kept in the crash-recovery record, so it's put back on the next start if TrayTrigger closes during a game.
