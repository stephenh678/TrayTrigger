# Disable Fullscreen Optimization Shims

## What it changes

Turns off Windows "Fullscreen optimizations" globally. Games that ask for exclusive fullscreen get true exclusive fullscreen, rather than a borderless window that Windows manages for them.

## Why it helps

- Some older DirectX 9 and 11 engines stutter, hit unexpected frame caps, or show uneven frame pacing when the Desktop Window Manager stays involved.
- True exclusive fullscreen removes the compositor from the path entirely.

## Trade-offs

- Modern games on Windows 11 generally run better with the optimizations on. The optimized path is the flip-model path that Auto HDR and windowed variable refresh rate depend on, so disabling it globally turns those off for fullscreen games.
- Alt-tab out of an exclusive fullscreen game is slower and may flash the screen.
- Disabling optimizations per game from the exe Properties, Compatibility tab is usually the better choice.
- Opt-in for these reasons: off by default and skipped by Apply Performance Preset.
- No restart and no administrator rights needed.

## Details

- Sets GameDVR_FSEBehavior=2 and GameDVR_HonorUserFSEBehaviorMode=1 under HKEY_CURRENT_USER\System\GameConfigStore.
