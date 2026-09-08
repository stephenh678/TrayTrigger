# System Restore points

## What it is

A System Restore point is a Windows snapshot of system files, drivers, and the registry. If a change makes Windows misbehave, you can roll the system back to the snapshot without touching your documents or games.

## When TrayTrigger creates one

- Before Apply Performance Preset and before Reset Defaults, if the option is on in Settings under Performance Tweaks.
- Not before individual tweak toggles. Those change one value each and can be reverted from the same button.
- Not for Performance Profiles, which restore themselves when the game exits.

## Limits to know

- Windows skips creating a restore point if another was created in the last 24 hours. The bulk action still runs; you just reuse the recent point.
- System Restore must be turned on for the system drive. Check System Properties, System Protection.
- Creating a point takes a few seconds and needs administrator rights, so expect a UAC prompt.

## Rolling back

Open Start, type "Create a restore point", and click System Restore. Pick the TrayTrigger-labelled point. Windows restarts and reverts system settings. Your files are not affected.
