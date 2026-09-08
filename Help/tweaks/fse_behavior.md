# Disable Fullscreen Optimization Shims

## What it changes

Turns off Windows "Fullscreen optimizations" globally. Games that ask for exclusive fullscreen get true exclusive fullscreen, rather than a borderless window that Windows manages for them.

## Why it helps

- Some older DirectX 9 and 11 engines stutter, hit unexpected frame caps, or show uneven frame pacing when the Desktop Window Manager stays involved.
- True exclusive fullscreen removes the compositor from the path entirely.

## Trade-offs

- Alt-tab out of an exclusive fullscreen game is slower and may flash the screen.
- Modern games on Windows 11 generally run better with the optimizations on, because of the flip-model path. This tweak mainly helps older titles.
- You can instead disable optimizations per game from the exe's Properties, Compatibility tab, which is usually the better choice.
- No restart and no administrator rights needed.

## Details

- Sets GameDVR_FSEBehavior=2 and GameDVR_HonorUserFSEBehaviorMode=1 under HKEY_CURRENT_USER\System\GameConfigStore.
