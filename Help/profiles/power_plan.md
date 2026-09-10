# "Ultimate Plan - TrayTrigger" Power Plan (profile)

## What it changes

Switches Windows to the "Ultimate Plan - TrayTrigger" power plan when the game launches and back to your previous plan when it exits. The plan keeps the CPU at full clock, disables core parking, and turns off PCIe and USB power saving.

## Why it helps

- The default Balanced plan clocks cores down and parks them during quiet moments. Ramping back up takes several milliseconds, which shows as a hitch when the game suddenly needs the CPU.
- Keeping everything awake gives steadier frame times on systems that show that kind of stutter.

## Trade-offs

- While the game runs, idle power draw and heat are higher. On a laptop, battery drains faster. The moment the game exits you are back on your normal plan, so none of that applies at the desktop.
- This is the same plan the permanent Performance Tweak uses. Prefer this session-scoped version unless you have a reason to run it all day.

## Details

- Uses powercfg to create the plan on first use by duplicating Windows' hidden Ultimate Performance scheme, then activates it with powercfg /setactive.
- The previous plan's GUID is saved in the session snapshot and restored on exit or after a crash.
