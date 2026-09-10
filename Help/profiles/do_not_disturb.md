# Do Not Disturb While Playing

## What it does

When a game with this profile starts, TrayTrigger turns off Windows toast notifications using the notification centre's own global switch, and puts the switch back to its previous value when the game exits.

## Why it helps

- Windows 11 turns on Do Not Disturb automatically only for games it detects as fullscreen. A borderless or windowed game still gets Teams, Discord, and Windows Update toasts over it.
- Because the prior value is recorded, a user who already had notifications off stays off afterward.

## Trade-offs

- You will not see a message arrive mid-game. That is the point, but it is why this is opt-in.
- If a future Windows build stops honouring the value live, notifications simply keep working as before; nothing else changes.

## Details

- Writes NOC_GLOBAL_SETTING_TOASTS_ENABLED=0 under HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings, the value the notification centre Do Not Disturb toggle uses. The prior value (or its absence) is captured in the session snapshot, so a crash still restores it on the next start.
