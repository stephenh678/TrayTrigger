# NVIDIA DLSS Override

## What it does

Runs a game on the DLSS files installed with your GeForce driver instead of the ones inside the game. Games ship with whatever DLSS version was current when they were built and often never update it; the driver keeps newer ones. It covers all three DLSS features the game supports: Super Resolution, Ray Reconstruction, and Frame Generation.

It is the same setting NVIDIA App calls DLSS Override. TrayTrigger switches it on per game, in Edit Game, and can do it for games NVIDIA App does not list.

## Why it helps

- Newer DLSS versions usually mean a sharper image, less ghosting and shimmer, and steadier fine detail at the same frame rate.
- Nothing in the game folder changes. No files are swapped, so a game update or "verify files" cannot undo it and there is nothing to break.

## Turning it on and off

- Edit Game, Performance tab, **Enable DLSS Override for this game**. The card appears on any PC with an NVIDIA driver. For a game that ships no DLSS it is one line, "Not available. This game doesn't include DLSS.", with no switch: there is nothing in the game for the driver's files to replace.
- The two numbers beside the switch are the version inside the game and the version your driver would use instead.
- It takes effect the next time you start the game. TrayTrigger checks the setting again each time it launches the game and puts it back if NVIDIA App has reset it.
- **Restore** on the card, or unticking the switch, returns that game's settings to exactly what they were before. **Restore All** in Settings > Launch & Performance > NVIDIA DLSS does the same for every game at once - do that before uninstalling TrayTrigger, because the setting lives in NVIDIA's driver, not in TrayTrigger. Removing a game from the library also puts its override back.

## Checking that it worked

- The card's **Last run** line says nothing is recorded yet until you have played with the override on. After that it shows the DLSS version the game actually loaded, and whether it came from NVIDIA or from the game's own files.
- For the preset letter and render resolution, tick **Show the NVIDIA DLSS Indicator in games** in Settings > Launch & Performance > NVIDIA DLSS. It draws NVIDIA's own overlay in the game.

## Trade-offs

- If it does not take, the game simply uses its own DLSS files, as it always did. Nothing else is affected.
- A few games ignore the override, and games with strict anti-cheat may not let TrayTrigger read what was loaded, so the Last run line keeps saying nothing is recorded for them. The override can still be working.
- Needs a GeForce driver recent enough to support DLSS Override.
- If NVIDIA App, Profile Inspector or another tool already turns the override on for every game (NVIDIA's "Global" profile), those games use the driver's DLSS whether this switch is on or off. TrayTrigger leaves settings it did not write alone.

## Details

- Writes six values to the game's profile in NVIDIA's driver settings: for each of the three features, "Enable DLL Override" and "Forced Preset Letter" set to NVIDIA's recommended preset. If NVIDIA has no profile for the game, TrayTrigger creates one and removes it again on Restore.
- Before writing, TrayTrigger records what each value was and where it came from. Restore only touches a value that is still exactly as TrayTrigger left it.
- For a game started through a launcher, the setting goes on the program that actually draws the game, which TrayTrigger finds beside the game's DLSS files.
