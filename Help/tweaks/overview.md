# How Performance Tweaks work

Every tweak on this page is a documented Windows setting that TrayTrigger reads and, if you choose, changes. Nothing here is a registry cleaner, a driver hack, or a service that runs in the background.

## What the badges mean

- OPTIMAL or STANDARD shows the current state of that setting on your PC right now, read live from Windows (for HAGS, straight from the display driver).
- OPT-IN marks tweaks with a real trade-off. They stay off by default and are skipped by Apply Performance Preset, so they only change if you click them yourself.
- RESTART means Windows only picks up the new value after a reboot.
- ADMIN means the change writes a machine-wide value, so Windows shows a User Account Control prompt.
- N/A means the tweak cannot do anything on this machine, for example a Windows 11 graphics setting on Windows 10 or HAGS on a GPU without support. The row explains why and its button is disabled.
- ON or OFF (blue) marks a status-only row such as Core Isolation.

## The score

"N / M Recommended Optimizations Active" counts only the recommended set: available, toggleable, not opt-in, not status-only. Opt-in tweaks you have turned on are shown separately. Reaching M / M means the preset has nothing left to do; it does not mean every opt-in tweak should be on.

## Apply Performance Preset and Reset Defaults

- Apply Performance Preset turns on every recommended tweak that is not already on, in one pass, with at most one administrator prompt.
- Reset Defaults reverts only the tweaks that are currently applied, putting each back to what TrayTrigger found before it changed it, again with at most one prompt. A power plan or visual-effects state you set yourself is never overwritten.
- A restart is suggested only when a restart-required tweak actually changed in that pass.
- Both can create a System Restore point first, controlled in Settings under Performance Tweaks. Restore points are the safety net if something on your PC behaves differently afterward.

## Permanent tweaks versus Performance Profiles

Everything on this page is permanent until you change it back. Settings that only make sense while a game is running, such as the high-performance power plan, MMCSS game priority, process priority, timer resolution, and Do Not Disturb, live under Performance Profiles instead. Those apply when a game launches and revert when it exits.

> Refresh re-reads every setting from Windows. Use it after changing something outside TrayTrigger, for example in Windows Settings or Game Bar.
