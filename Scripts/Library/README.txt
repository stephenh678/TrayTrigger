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

  Example-QuietMode.ps1
    Closes background apps you name, such as OneDrive or Teams, before the
    game and reopens them after it.
    Script Arguments:  process names
                       OneDrive ms-teams Dropbox

  Example-CompanionApps.ps1
    Starts the tools a game needs, such as SimHub or TrackIR, and closes them
    after the game. Tools you had already opened are left alone.
    Script Arguments:  program paths, each in double quotes
                       "C:\Program Files (x86)\SimHub\SimHubWPF.exe"

  Example-OBSReplayBuffer.ps1
    Runs OBS in the tray with the replay buffer on, so a hotkey saves the
    last minutes of play. An OBS you opened yourself is never touched.
    Script Arguments:  none, or an OBS profile name in double quotes

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

  Work on a copy: copy the file in Explorer and give it your own name, for
  example "My-SaveBackup.ps1". TrayTrigger never overwrites a script that is
  already in this folder, so your edits are safe either way, but a copy lets
  you compare with the original. If you delete an example, the original comes
  back the next time you open this folder from TrayTrigger.


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

  A community catalog of TrayTrigger scripts is planned, where you'll be able
  to share your scripts and install other people's after they are reviewed.
  To have a script ready for it:

    - Start it with the same header the examples use: Name, Description,
      Author, Version, Phase, Needs admin, Dependencies, Script Arguments.
    - Use only the five values and Script Arguments. No downloads, no
      network calls, no Invoke-Expression, no installing modules.
    - Only close programs the user named or that your script started, and
      put back whatever you change.
    - Explain every step in a comment.
    - Test both phases with the Test buttons.
