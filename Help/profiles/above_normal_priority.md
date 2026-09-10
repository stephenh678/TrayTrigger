# Above Normal Process Priority (profile)

## What it changes

Sets the game's own process to the Above Normal CPU scheduling class as soon as it starts.

## Why it helps

- When the game and a background process both want the CPU, the scheduler favours the game. Worst-case frame-time spikes caused by background work get shorter.
- This is a real Windows scheduling class, the same one Task Manager lets you set by hand, not a registry trick.

## Trade-offs

- Community testing consistently finds Above Normal gives most of the benefit with low risk. Going further to High or Realtime shows little extra gain and can starve audio and input, so TrayTrigger stops at Above Normal.
- The effect varies by game and is not guaranteed.
- Applies once TrayTrigger has found the game's process. For Steam games that happens after Steam reports the game running and its process is found under the install folder, so there can be a few seconds at the coarse priority while the game starts.

## Details

- Applied through the process priority class API after the game starts. Nothing to restore: the priority ends with the process.
- Aggressive tier only.
