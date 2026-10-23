# Display (per game)

## What it does

Edit Game > Performance has a **Display** card for each game. It holds the resolution and refresh rate for the main display, and, with more than one monitor, an option to keep the mouse on the game's screen. The first part: the resolution and refresh rate the main display runs at while that game does. TrayTrigger sets it just before the game starts and puts the display back when the game ends. It's for a game that has no refresh rate or resolution setting of its own, or a monitor that starts a game at the wrong rate.

There are two lists. **Resolution** shows the resolutions your main display offers, the largest first; **Don't change the display** is how every game starts out, and leaves the display alone. Once you pick a resolution, **Refresh rate** shows only the rates the display offers at that resolution, the fastest first. To change only the refresh rate, pick your current resolution and the rate you want.

## Keep the mouse on the game's screen

With more than one monitor attached, the card has a checkbox, **Keep the mouse on the game's screen**, off by default. It's for a game in a window or borderless full screen, where the mouse can slide onto another monitor and a click there pulls the game out of focus.

- While the game's window is in front, the mouse can't leave the monitor that window is on.
- Anything else that takes the focus frees it at once: Alt+Tab, the Windows key, a prompt, another program. When the game is in front again, it holds again. So you can always get the mouse out.
- It is freed when the game ends, when TrayTrigger closes, and by Windows itself if TrayTrigger stops.
- A game that confines the mouse itself, for a camera, is left alone: TrayTrigger only holds the mouse to the whole screen, never tighter, and never lifts a hold it didn't set.
- A game running as administrator may need TrayTrigger run as administrator too. This hasn't been tried on a PC with two monitors.
- With one monitor the checkbox is hidden, since there is nowhere for the mouse to go. A game that has it on keeps it.

## What the resolution and refresh rate do not do

- They change the main display only. TrayTrigger doesn't choose a monitor, move the game to another one, dim the others, or change the sound output.
- It sets a resolution and a refresh rate, not HDR (see HDR), colour depth or scaling.
- It never asks for a mode the display doesn't list. A mode Windows would refuse is skipped, the game starts as usual, and Activity & History says why.

## Putting it back

- When the game ends, TrayTrigger asks Windows for the desktop's own mode, the one set in Windows Settings. It doesn't re-apply a remembered one, so a change you made in Windows in between isn't undone.
- The mode is a temporary one, never saved as the desktop's own setting, so a restart brings your normal mode back. Windows also undoes it when TrayTrigger stops.
- TrayTrigger keeps a short note of the mode it set, and at the next start puts the display back if it is still in that mode, which covers TrayTrigger stopping while a game runs. If the display has changed since, or the monitor is no longer there, it does nothing.
- If the display can't be put back, Activity & History says so and TrayTrigger tries again at the next start. Windows Settings > System > Display changes it by hand.

## Things to know

- TrayTrigger asks Windows to treat the change as temporary, which is meant to keep it from rearranging open windows and desktop icons. A different resolution can still disturb them on some PCs; a different refresh rate at the same resolution doesn't change the layout.
- A game that sets its own resolution, such as one in exclusive full screen, overrides this while it runs. TrayTrigger still puts the display back when the game ends.
- One game at a time. If a second game with its own display mode starts while the first is running, the second leaves the display alone and the first one puts it back.
- It doesn't work in a remote desktop session, and it does nothing there.
- A change to a game's display settings is recorded in Activity & History, as are the mode being set and put back.
