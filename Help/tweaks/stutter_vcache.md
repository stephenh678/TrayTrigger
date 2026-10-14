# AMD V-Cache optimizer

## Why it matters

The 7900X3D, 7950X3D and 9950X3D have two chiplets, and only one has the extra 3D V-Cache. AMD's chipset software installs a service, **AMD 3D V-Cache Performance Optimizer Service**, that works with Windows to keep games on the cache chiplet. If it isn't running, games can land on the other chiplet and run slower, and the cache cores may not be used the way you paid for.

## What to try

1. If the PC only just started, wait a minute and run the check again.
2. Install the latest **AMD chipset drivers** from amd.com (not the motherboard maker's older bundle). Running AMD's installer a second time after a restart has fixed it for some people.
3. Update the motherboard BIOS to the latest version.
4. Check that **Game Mode** is on in Windows Settings, and that Xbox Game Bar is installed: the optimizer relies on Windows recognizing the game. If a game isn't recognized, open Game Bar over it (Win+G) and tick **Remember this is a game**.
5. If it still isn't running, uninstall the AMD chipset software, restart, and install it again.

## Details

TrayTrigger asks Windows whether the service named `amd3dvcacheSvc` is running. The check only appears on dual-chiplet X3D CPUs: on single-chiplet X3D CPUs AMD's driver leaves the service stopped by design. TrayTrigger only reports; it doesn't change the service.
