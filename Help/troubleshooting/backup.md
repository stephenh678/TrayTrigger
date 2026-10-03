# Back up and restore TrayTrigger

Settings > Diagnostics & Storage > Backup & Restore saves everything TrayTrigger keeps to one .zip file, and puts it back. Use it to move TrayTrigger to a new PC, before reinstalling Windows, or to undo a change you regret.

## What a backup holds

- Your game library: every game with its category, favorite, hotkey, playtime, Performance Profile, CPU Cores, scripts and the rest of Edit Game.
- Your settings, including scan locations and ignored games.
- Your tools, and your scripts folder.
- Cached artwork and icons, so posters don't have to be fetched again.

Game saves are not in it, and neither is anything in the games' own folders.

## Backing up

Press **Back Up...** and choose where to save the file. Somewhere outside the PC - a USB drive, a cloud folder - is what makes it a backup.

## Restoring

Press **Restore...** and choose a backup. TrayTrigger says what is in it and asks first. Then:

- What you have now is backed up first, to the Backups folder in TrayTrigger's data folder, so a restore can be undone by restoring that file. The last five are kept.
- TrayTrigger restarts and puts the backup in place.
- Games are found where they are now. A game from GOG Galaxy, the EA app, Epic or Ubisoft Connect is found through its launcher. A game whose drive letter changed (D: is now E:) is found on the new drive, and paths in your old Windows user folder are moved to the new one.
- A game that isn't installed on this PC is kept, greyed out with its NOT INSTALLED or MISSING tag. Reinstall it, or use Locate Executable.

A restore waits until no game launched from TrayTrigger is running.

## Kept from this PC

A few settings describe the PC rather than your choices, so a restore keeps this PC's: what each tweak found before TrayTrigger applied it, the window's size and position, and Start with Windows.

SteamGridDB and RAWG API keys are encrypted for your Windows account. On another PC or account they can't be read, and TrayTrigger asks you to enter them again in Settings > Library & Art.
