# Back up and restore TrayTrigger

Settings > Diagnostics & Storage > Backup & Restore saves everything TrayTrigger keeps to one .zip file, and puts it back. Use it to move TrayTrigger to a new PC, before reinstalling Windows, or to undo a change you regret.

## What a backup holds

- Your game library: every game with its category, favorite, hotkey, playtime, Performance Profile, CPU Cores, scripts and the rest of Edit Game.
- Your settings, including scan locations and ignored games.
- Your tools, and your scripts folder.
- The themes you made, with their background pictures. The built-in themes are part of TrayTrigger itself.
- Cached artwork and icons, so posters don't have to be fetched again.
- Activity & History, when **Include Activity & History in the backup** is ticked beside Back Up (it is on by default). Untick it to leave this PC's history out, which suits a backup you will restore on a different PC, where the old entries could mislead. A restore from a backup that holds it replaces your history with the backup's; a restore from one that doesn't leaves your history alone. The backup made before a restore always includes it.

Game saves are not in it, and neither is anything in the games' own folders. Game details fetched from Steam and RAWG are fetched again.

## Backing up

Press **Back Up...** and choose where to save the file. Somewhere outside the PC - a USB drive, a cloud folder - is what makes it a backup.

## Automatic backups

Tick **Back up automatically** beside Back Up and TrayTrigger makes a backup for you, so you don't have to remember. It is off until you turn it on.

- **How often:** every week (the default), every 2 weeks or every month. **Keep the last** says how many automatic backups stay (4 by default); older ones are deleted.
- **When:** at the first start and then once a day while TrayTrigger runs, whenever the last backup of any kind is older than the interval. A backup you make by hand counts, so it moves the next automatic one back. It never runs while a game is running; it waits and is made when the last game exits.
- **Where:** in the Backups\Automatic folder inside TrayTrigger's data folder, named TrayTrigger auto backup with the date. **Open backups folder** shows it. That is on this PC's own disk, so copy a backup somewhere else (a USB drive, a cloud folder) to be safe from a disk failure. Backups you make with Back Up... go wherever you choose.
- **Activity & History:** each backup is noted there. If one fails (the disk is full, the folder can't be written), it shows under Needs attention with the reason, and TrayTrigger tries again at its next check, up to three times a day.
- **Uninstalling:** the uninstaller offers to delete your data. Even then it keeps the Backups folder unless you also tick **Also delete my backups**.

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
