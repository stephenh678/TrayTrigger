# Display mode (per game)

## What it does

Edit Game > Performance has a **Display** choice for each game: the resolution and refresh rate the main display runs at while that game does. TrayTrigger sets it just before the game starts and puts the display back when the game ends. It's for a game that has no refresh rate or resolution setting of its own, or a monitor that starts a game at the wrong rate.

There are two lists. **Resolution** shows the resolutions your main display offers, the largest first; **Don't change the display** is how every game starts out, and leaves the display alone. Once you pick a resolution, **Refresh rate** shows only the rates the display offers at that resolution, the fastest first. To change only the refresh rate, pick your current resolution and the rate you want.

## What it does not do

- It changes the main display only. It doesn't choose a monitor, move the game to another one, or change the sound output.
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
- A change to a game's display mode is recorded in Activity & History, as are the mode being set and put back.
