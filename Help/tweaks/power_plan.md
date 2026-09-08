# Ultimate Plan - TrayTrigger Power Plan

## What it changes

Creates a power plan based on Windows' hidden Ultimate Performance scheme and makes it the active plan permanently. The CPU minimum state is pinned at 100 percent, core parking is off, and PCIe and USB power saving are disabled.

## Why it helps

- The default Balanced plan lets cores clock down and park during quiet moments. Ramping back up takes several milliseconds, which shows as a hitch when the game suddenly needs the CPU.
- With no power-state transitions, frame times are steadier on systems that show that kind of stutter.

## Trade-offs

- This is the permanent version. The CPU never idles down, so expect higher idle power draw, more heat, more fan noise, and shorter battery life on a laptop.
- Most people are better served by the same plan under Performance Profiles, which switches it on when a game launches and back off when it exits.
- Marked OPT-IN for that reason. Apply Performance Preset leaves it alone.

## Details

- Creates the plan with powercfg by duplicating the Ultimate Performance scheme, then sets processor minimum and maximum state, core parking, PCIe link state, and USB selective suspend.
- Restoring returns you to your previous plan. The custom plan stays available in Windows Power Options until deleted.
