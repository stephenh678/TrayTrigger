# NVIDIA DLSS Override

## What it does

Runs a game on the DLSS files installed with your GeForce driver instead of the ones inside the game. Games ship with whatever DLSS version was current when they were built and often never update it; the driver keeps newer ones. It covers all three DLSS features the game supports: Super Resolution, Ray Reconstruction, and Frame Generation.

It is the same setting NVIDIA App calls DLSS Override. TrayTrigger switches it on per game, in Edit Game, and can do it for games NVIDIA App does not list.

## Why it helps

- Newer DLSS versions usually mean a sharper image, less ghosting and shimmer, and steadier fine detail at the same frame rate.
- Nothing in the game folder changes. No files are swapped, so a game update or "verify files" cannot undo it and there is nothing to break.

## Turning it on and off

- Edit Game, Performance tab, **Enable DLSS Override for this game**, or right-click the game and tick **DLSS Override**. To switch it for several games at once, select them and right-click one; see Selecting several games at once. The card appears on any PC with an NVIDIA driver. For a game that ships no DLSS it is one line, "Not available. This game doesn't include DLSS.", with no switch: there is nothing in the game for the driver's files to replace.
- The two numbers beside the switch are the version inside the game and the version your driver would use instead.
- It takes effect the next time you start the game. TrayTrigger checks the setting again each time it launches the game and puts it back if NVIDIA App has reset it.
- Unticking the switch returns that game's settings to exactly what they were before. **Restore All** in Settings > Launch & Performance > NVIDIA DLSS does the same for every game at once - do that before uninstalling TrayTrigger, because the setting lives in NVIDIA's driver, not in TrayTrigger. Removing a game from the library also puts its override back.

## Preset

The **Preset** choice on the card says what the override does about DLSS presets, for all three features at once.

- **NVIDIA recommended** (the default): NVIDIA picks the preset for each DLSS mode, whatever the game asks for. At the time of writing that is K for Quality, Balanced and Native, M for Performance and L for Ultra Performance. It is NVIDIA App's **Recommended** choice.
- **Default**: no preset is written, so the game picks its own, as NVIDIA's own profiles leave it and as DLSS Swapper's Default does. The game still gets the newer DLSS version.

Changing it while the override is on takes effect the next time you start the game. Newer presets can cost more performance on RTX 20 and 30 cards, so Default can suit them better. If NVIDIA App sets a preset for the game after you, NVIDIA App's choice stays.

## Checking that it worked

- The card's **Last run** line says nothing is recorded yet until you have played with the override on. After that it shows the DLSS version the game actually loaded, and whether it came from NVIDIA or from the game's own files.
- For the preset letter and render resolution, tick **Show the NVIDIA DLSS overlays in games** in Settings > Launch & Performance > NVIDIA DLSS. It draws NVIDIA's own overlays in the game: a corner line with the version, preset and resolution, and, where Frame Generation is running, a bar across the top that also names the driver profile the override was written to.

## If another tool changes it

DLSS Swapper, RHI, NVIDIA App and NVIDIA Profile Inspector all write to the same game profile in NVIDIA's driver, and the driver keeps one value per setting, so the last tool to write wins. TrayTrigger never overwrites a value another tool has changed since it applied the override.

- When that happens, the next launch of the game leaves the settings alone and Activity & History shows **DLSS Override was changed by another app**. The game still runs, with whatever mix of settings is now on its profile.
- The **Last run** line is where it shows. It names each feature and where it loaded from, so "Ray Reconstruction 310.4.0 from the game's own files" means that feature is no longer using the driver's DLSS.
- To take the settings back, untick **Enable DLSS Override for this game** and tick it again. That writes the override's values again. Settings that belong to another tool, such as Frame Generation's multi-frame count, are not touched.
- If another tool keeps changing them, leave the override off for that game and let that tool manage it.

## Trade-offs

- If it does not take, the game simply uses its own DLSS files, as it always did. Nothing else is affected.
- A few games ignore the override, and games with strict anti-cheat may not let TrayTrigger read what was loaded, so the Last run line keeps saying nothing is recorded for them. The override can still be working.
- Needs a GeForce driver recent enough to support DLSS Override.
- If NVIDIA App, Profile Inspector or another tool already turns the override on for every game (NVIDIA's "Global" profile), those games use the driver's DLSS whether this switch is on or off. TrayTrigger leaves settings it did not write alone.

## Details

- Writes six values to the game's profile in NVIDIA's driver settings: for each of the three features, "Enable DLL Override" and, unless the preset is Default, "Forced Preset Letter" set to NVIDIA's recommended preset. In NVIDIA App that is the game's **DLSS Override - Model Presets** set to **Recommended**: NVIDIA chooses the preset for each mode, rather than the game's own choice ("Use the 3D application setting"). If NVIDIA has no profile for the game, TrayTrigger creates one and removes it again on Restore.
- Only that game's profile is written. NVIDIA App's Global settings are left as they are, so a game without the override keeps choosing its own model.
- Before writing, TrayTrigger records what each value was and where it came from. Restore only touches a value that is still exactly as TrayTrigger left it.
- For a game started through a launcher, the setting goes on the program that actually draws the game, which TrayTrigger finds by looking through the game's folder for its DLSS files and the program that goes with them.
