# NVIDIA DLSS Overlays

## What it changes

Turns on NVIDIA's own on-screen overlays for DLSS. There are two, and this switch turns on both, because each answers something the other does not.

- **The corner line**, in every game that uses DLSS: the DLSS version in use, the preset letter, and the render and output resolution.
- **The bar across the top**, in a game that is using Frame Generation: the driver and Streamline versions, the graphics API, the output and motion-vector resolutions, the frame multiplier, your refresh rate, how the game hands its HUD to Frame Generation, and the name of the driver profile in effect.

## Why it helps

- It is the one place the active preset letter is shown. Nothing else reports it.
- It is the quickest way to confirm that a DLSS Override set in Edit Game is really in effect: the version on screen should be the newer one from your GeForce driver, not the one inside the game.
- The Frame Generation bar names the driver profile the game is running under, which is where an override is written. It also shows what Frame Generation is actually doing - the multiplier, and the resolution it is generating from - which nothing in TrayTrigger can tell you.

## Trade-offs

- For testing, not for leaving on. They draw over the game, and they show in every DLSS game on this PC, not just one, until you turn them off here.
- Needs administrator rights, because the settings live in a part of the registry shared by all users. No restart is needed; start the game after switching them on.
- Off until you tick it. It is in Settings > Launch & Performance > NVIDIA DLSS, not among the Performance Tweaks, so Apply Performance Preset and Restore Previous Settings never touch it.
- The top bar only appears in a game that is using Frame Generation. A DLSS game without it shows the corner line alone - that is the game, not a fault.
- NVIDIA only. On a PC without a GeForce driver that supports DLSS the NVIDIA DLSS card is not shown.

## Details

- Both values are under HKEY_LOCAL_MACHINE\SOFTWARE\NVIDIA Corporation\Global\NGXCore, and both are written in one step, so there is one administrator prompt rather than two.
- ShowDlssIndicator=0x400 (1024) draws the corner line. NVIDIA's own registry file sets 1, which only draws in developer builds; 1024 is the value that allows a retail game to draw it.
- DLSSG_IndicatorText=2 draws the Frame Generation bar. NVIDIA documents 0, 1 and 2 as off, minimal and detailed; 2 is the one worth having, because minimal leaves out the resolutions and the profile name.
- Unticking puts back whatever was there before, for both. If a value did not exist, it is removed rather than set to 0 - so a Frame Generation bar you had switched on yourself is still there afterwards.
- The switch reads as on only when both values are as it left them. If something else changes one - NVIDIA App, a driver install, a registry edit - it reads as off, and ticking it puts both back.
