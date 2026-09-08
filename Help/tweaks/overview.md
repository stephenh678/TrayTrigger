# How Performance Tweaks work

Every tweak on this page is a documented Windows setting that TrayTrigger reads and, if you choose, changes. Nothing here is a registry cleaner, a driver hack, or a service that runs in the background.

## What the badges mean

- OPTIMAL or STANDARD shows the current state of that setting on your PC right now, read live from Windows.
- OPT-IN marks tweaks with a real trade-off. They stay off by default and are skipped by Apply Performance Preset, so they only change if you click them yourself.
- RESTART means Windows only picks up the new value after a reboot.
- Tweaks that need administrator rights ask for a UAC prompt when applied.

## Apply Performance Preset and Reset Defaults

- Apply Performance Preset turns on every non-opt-in tweak in one pass.
- Reset Defaults puts each of those back to the standard Windows value.
- Both can create a System Restore point first, controlled in Settings under Performance Tweaks. Restore points are the safety net if something on your PC behaves differently afterward.

## Permanent tweaks versus Performance Profiles

Everything on this page is permanent until you change it back. Settings that only make sense while a game is running, such as the high-performance power plan, MMCSS game priority, and process priority, live under Performance Profiles instead. Those apply when a game launches and revert when it exits.

> Refresh re-reads every setting from Windows. Use it after changing something outside TrayTrigger, for example in Windows Settings or Game Bar.
