# Exempt the Game From Power Throttling (profile)

## What it changes

Opts the game's process out of Windows' power throttling (EcoQoS), and out of Windows 11 ignoring its timer requests while its window is hidden. Applied as soon as TrayTrigger has found the game's process.

## Why it helps

- Power throttling lowers the clock speed of a process Windows thinks is in the background. On an Intel hybrid CPU it also moves the process onto the efficiency cores.
- Windows can decide a game is in the background while you're still watching it, for example on a second monitor while you type in Discord, or after a quick alt-tab. The frame rate then drops until you click back into the game.
- Windows 11 also ignores the timer resolution requests of a window it can't see. In some engines that upsets frame pacing when the window is covered.

## Trade-offs

- None worth noting for a game: it's the process you want running at full speed.
- A game running as administrator, or protected by some anti-cheat, can refuse it. TrayTrigger logs that and the game runs as normal.

## Details

- Uses SetProcessInformation with ProcessPowerThrottling, the documented opt-out: the execution speed and ignore-timer-resolution bits set in ControlMask and cleared in StateMask. Windows 10 releases that don't know the timer bit get the execution speed part only.
- Nothing to put back: the setting belongs to the process and ends with it.
- Applied to the game's own process. For Steam games, that happens once Steam reports the game running and TrayTrigger finds its process in the install folder.
