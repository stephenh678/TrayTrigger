# "Ultimate Plan - TrayTrigger" Power Plan (always on)

## What it changes

Creates a power plan based on the hidden Windows Ultimate Performance scheme, sets minimum and maximum processor state to 100 percent, disables core parking, sets boost mode to Aggressive and cooling policy to Active, and turns off PCI Express link power management and USB selective suspend. Then makes it the active plan, all the time.

## Why it helps

- The Balanced plan downclocks cores and parks idle ones during quiet moments and takes 5 to 15 ms to ramp back up. Pinning the CPU avoids that transition.
- PCIe and USB power saving can add latency for the GPU and input devices.

## Trade-offs

- Running this 24/7 costs idle power, heat, and on a laptop battery life; the values apply on battery too. That is why it is opt-in here. The same plan applied only while a game runs is available under Performance Profiles, which is the better choice for most people.
- No administrator rights needed; powercfg can change plans for a normal user.

## Details

- TrayTrigger records which plan was active before switching and puts that exact plan back on revert. Balanced is only the fallback if that plan no longer exists.
- Each powercfg setting is applied and checked individually; a rejected setting is logged rather than hidden.
- Not every CPU accepts every value. Core parking, for one, is expressed as a minimum percentage of cores to keep unparked, and some machines only allow a narrower range than 0-100. TrayTrigger reads what each setting will actually accept on your hardware and uses the nearest value it allows, so a plan applies as fully as the machine permits instead of failing on one setting.
