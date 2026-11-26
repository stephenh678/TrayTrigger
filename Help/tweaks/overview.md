# How Performance Tweaks work

Every tweak on this page is a documented Windows setting that TrayTrigger reads and, if you choose, changes. Nothing here is a registry cleaner, a driver hack, or a service that runs in the background.

## How a group reads

Each group (Display & Input, CPU & Scheduling Priority, Network & Background Activity) opens with a tinted heading: its name, what it covers, and at the right its count. Its tweaks follow in two parts:

- **In the Performance Preset**: the tweaks Apply Performance Preset turns on and Put Back Preset reverts. The count on the Performance Preset card is these rows.
- **Opt-in**: tweaks with a real trade-off. They stay off by default and are skipped by Apply Performance Preset, so they only change if you switch them on yourself.

Each row has a switch at the right, and the switch is its state, read live from Windows (for HAGS, straight from the display driver): **On** means TrayTrigger's value is in place, **Off** means the value TrayTrigger found. Flip it to apply the tweak or put it back. While the change is applied it says "Working...", and if Windows refuses it (a declined administrator prompt, say) the switch goes back to what Windows actually has.

A tag beside the name appears only for what the switch can't say:

- NOT APPLIED (amber) is a preset tweak that is off: what Apply Performance Preset would change.
- N/A means the tweak cannot do anything on this machine, for example a Windows 11 graphics setting on Windows 10 or HAGS on a GPU without support. The row explains why and its switch is greyed out.
- ON or OFF (blue) marks a status-only row such as Core Isolation, which has no switch.

Under the description, a quiet line says what applying the tweak asks of you: "Asks for administrator permission" when it writes a machine-wide value, so Windows shows a User Account Control prompt, and "Needs a restart" when Windows only picks up the new value after a reboot. Learn more, at the end of the description, opens the tweak's own page.

The confirmation before Apply Performance Preset or Put Back Preset says whether a System Restore point will be created first. It is a preference, the "Create a System Restore point" box on the Performance Preset card; the status line says after each run whether a restore point was actually made.

## The score

The Performance Preset card counts only the preset's tweaks: available, toggleable, not opt-in, not status-only. "12 of 14 preset tweaks applied" names the ones that are off and what applying them will ask for, and a tweak this PC can't use is listed as not counted. Each group's heading counts its own preset tweaks the same way, with its opt-in tweaks after. Reaching 14 of 14 means the preset has nothing left to do; it does not mean every opt-in tweak should be on.

## Apply Performance Preset and Put Back Preset

- Apply Performance Preset turns on every recommended tweak that is not already on, in one pass, with at most one administrator prompt.
- Put Back Preset reverts the preset's tweaks that are currently applied, putting each back to what TrayTrigger found before it changed it, again with at most one prompt. An opt-in tweak you switched on yourself is left as you set it; switch it off on its own row. That is not always the Windows default. Switching a single tweak off does the same for that one tweak. A power plan or visual-effects state you set yourself is never overwritten.
- A restart is suggested only when a restart-required tweak actually changed in that pass.
- Both can create a System Restore point first, controlled by the box on the Performance Preset card. The confirmation before either says whether one will be made; the status line at the bottom says afterwards whether a restore point was actually created, skipped, or refused by Windows. Restore points are the safety net if something on your PC behaves differently afterward.

## Permanent tweaks versus Performance Profiles

Everything on this page is permanent until you change it back. Settings that only make sense while a game is running, such as the high-performance power plan, MMCSS game priority, process priority, timer resolution, and Do Not Disturb, live under Performance Profiles instead. Those apply when a game launches and revert when it exits.

> Refresh, at the right of the Performance Tweaks heading, re-reads every setting from Windows and runs Stutter Check again. Use it after changing something outside TrayTrigger, for example in Windows Settings or Game Bar.
