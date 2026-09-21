TrayTrigger scripts
===================

A script lets TrayTrigger do something extra every time you play: pause your
animated wallpaper, close apps that compete with the game, start the tools a
game needs, keep a replay buffer running, or back up your saves.

TrayTrigger runs a script just before a game starts and again after it
closes. The scripts in this folder are examples. Each one works as it is, and
each one is written to be read, copied and turned into something of your own.
Nothing here runs until you choose it for a game.


QUICK START
-----------

  1. Settings > Launch & Performance: turn on "Enable game scripts".

  2. Edit Game > Pre-Launch & Post-Exit Scripts: choose the example as the
     pre-launch script and tick "Use the same script for pre-launch and
     post-exit". Every example handles "before" and "after" in one file.

  3. If the example needs Script Arguments, type them into that box. The
     list below shows what each example expects.

  4. Press Test next to each box. You see what the script does and what it
     prints, without launching the game.

  To use a script for every game, set it as both default scripts in
  Settings > Launch & Performance and tick "Run the default scripts".


THE EXAMPLES
------------

  Example-WallpaperEnginePause.ps1
    Pauses and mutes Wallpaper Engine while you play, then resumes it.
    Script Arguments:  none

  Example-CloseBackgroundApps.ps1
    Closes background apps you name, such as OneDrive or Teams, before the
    game and reopens them after it.
    Script Arguments:  process names
                       OneDrive ms-teams Dropbox

  Example-StartCompanionApps.ps1
    Starts the tools a game needs, such as SimHub or TrackIR, and closes them
    after the game. Tools you had already opened are left alone.
    Script Arguments:  program paths, each in double quotes
                       "C:\Program Files (x86)\SimHub\SimHubWPF.exe"

  Example-OBSReplayBuffer.ps1
    Runs OBS in the tray with the replay buffer on, so a hotkey saves the
    last minutes of play. An OBS you opened yourself is never touched.
    Script Arguments:  none, or in double quotes: a path ending in .exe if
                       OBS is somewhere other than Program Files (the Steam
                       copy is), and an OBS profile name. Either, or both.

  Example-SaveBackup.ps1
    Zips a game's save folder before and after you play, and keeps the
    newest ten backups.
    Script Arguments:  the save folder, in double quotes
                       "%APPDATA%\EldenRing"

  Every example opens with a comment block: why you might want it, how to
  set it up, how it works, and what to change. Open the file in Notepad and
  read that part first.


CHANGING AN EXAMPLE
-------------------

  Near the top of each example is a "Change these" section with the
  settings most people want to adjust. Below it, every step has a comment
  saying what it does.

  Work on a copy. The seven files TrayTrigger put in this folder are its
  copy, not yours. Every time TrayTrigger starts, and every time you browse
  or open this folder from it, each one is compared with the copy inside
  TrayTrigger and replaced if it differs. That is how a corrected template
  or a better example reaches you, and it means an edit of yours is undone
  at the next start - not only when you update TrayTrigger.

  Your version is not thrown away. Before the fresh copy is written, the
  file you changed is renamed alongside it with ".previous" on the end, for
  example "Example-SaveBackup.ps1.previous", and nothing touches it after
  that. A second edit becomes ".previous.2", and so on. So an edit made by
  mistake costs you a rename, not your work - but a file kept that way is no
  longer the one your game runs, so move your changes back into a copy of
  your own.

  Better: copy the file in Explorer first and give it your own name, for
  example "My-SaveBackup.ps1", or use "New script..." in Edit Game. A file
  TrayTrigger did not put here is never touched. Deleting an example is
  safe - the original comes back the next time TrayTrigger starts.

  Settings that differ from one PC to the next - where a program is
  installed, which apps to close - belong in Edit Game > Script Arguments
  rather than in the file, so they survive every update.


WRITING YOUR OWN
----------------

  In Edit Game, "New script..." next to a script box copies a blank template
  (_Blank.ps1 or _Blank.bat) to a name you choose and opens it for editing.

  TrayTrigger gives every script the same five values, in this order:

    Phase       prelaunch before the game, postexit after it
    GameName    the game's name as shown in TrayTrigger
    GameExe     the full path of the game's exe
    GameId      a stable ID for the game, handy for naming files
    Playtime    minutes played: empty before the game, a number after it

  Whatever you type in Script Arguments comes after those five. In
  PowerShell, the param block at the top of every example reads them all and
  puts Script Arguments in $ScriptArgs. In a batch file they are %1 to %5,
  then %6 onwards.

  The "before" run and the "after" run are separate. To pass something from
  one to the other, write a small note file in your TEMP folder named after
  the game ID, then read and delete it after the game. Every example that
  puts something back works this way.

  Exit code 0 means success. A non-zero exit code only stops the game from
  starting if you ticked "Cancel the launch if the pre-launch script fails".

  Scripts run as you, without Administrator rights, unless you tick "Run
  scripts as Administrator". None of the examples need it.

  The full details, including environment variables and batch file quirks,
  are in the app: Edit Game > "Script guide & examples".


WHEN SOMETHING DOESN'T WORK
---------------------------

  Press Test next to the script box. It runs the script straight away and
  shows the exit code and everything the script printed.

  Every script run is also written to the TrayTrigger log, under GameScript.
  Open it from Settings > Diagnostics & Storage. Turn on verbose logging
  there to see what a hidden script printed.


SHARING YOUR SCRIPT
-------------------

  The community catalog of TrayTrigger scripts is on GitHub:

    https://github.com/stephenh678/TrayTrigger-Scripts

  Every script there is read by a maintainer before it is listed. To use
  one, download its folder into this scripts folder and browse to it in
  Edit Game. To share yours, open a pull request there (the CONTRIBUTING
  file has the checklist), or post it first in Discussions > Show and tell.
  To have a script ready for it:

    - Start it with the same header the examples use: Name, Description,
      Author, Version, Phase, Needs admin, Dependencies, Script Arguments.
    - Use only the five values and Script Arguments. No downloads, no
      network calls, no Invoke-Expression, no installing modules.
    - Only close programs the user named or that your script started, and
      put back whatever you change.
    - Explain every step in a comment.
    - Test both phases with the Test buttons.
