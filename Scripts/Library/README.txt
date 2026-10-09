TrayTrigger scripts
===================

A script lets TrayTrigger do something extra every time you play: close apps
that compete with the game, or start the tools a game needs.

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

  Example-CloseBackgroundApps.ps1
    Asks background apps, such as OneDrive or Dropbox, to close before the
    game and reopens them after it. An app that won't close is left running,
    unless you add "force".
    Script Arguments:  "recommended" for its recommended list, and/or
                       process names, and "force" if you want it; in any
                       order; empty does nothing
                       recommended Spotify force

  Example-StartCompanionApps.ps1
    Starts the tools a game needs, such as SimHub or TrackIR, and closes them
    after the game. Tools you had already opened are left alone. For a tool
    you want with every game, use the Tools page instead.
    Script Arguments:  program paths, each in double quotes, and "force" if
                       you want it
                       "C:\Program Files (x86)\SimHub\SimHubWPF.exe" force

  Every example opens with a comment block: why you might want it, how to
  set it up, how it works, and what to change. Open the file in Notepad and
  read that part first.


CHANGING AN EXAMPLE
-------------------

  Near the top of each example is a "Change these" section with the
  settings most people want to adjust. Below it, every step has a comment
  saying what it does.

  Work on a copy. The files TrayTrigger put in this folder are its
  copy, not yours. Every time TrayTrigger starts, and every time you browse
  or open this folder from it, each one is compared with the copy inside
  TrayTrigger and replaced if it differs. That is how a corrected or better
  example reaches you, and it means an edit of yours is undone
  at the next start - not only when you update TrayTrigger.

  Your version is not thrown away. Before the fresh copy is written, the
  file you changed is renamed alongside it with ".previous" on the end, for
  example "Example-CloseBackgroundApps.ps1.previous", and nothing touches it after
  that. A second edit becomes ".previous.2", and so on. So an edit made by
  mistake costs you a rename, not your work - but a file kept that way is no
  longer the one your game runs, so move your changes back into a copy of
  your own.

  Better: copy the file in Explorer first and give it your own name, for
  example "My-CloseBackgroundApps.ps1", or use "New script..." in Edit Game. A file
  TrayTrigger did not put here is never touched. Deleting an example is
  safe - the original comes back the next time TrayTrigger starts.

  Settings that differ from one PC to the next - where a program is
  installed, which apps to close - belong in Edit Game > Script Arguments
  rather than in the file, so they survive every update.


WRITING YOUR OWN
----------------

  In Edit Game, "New script..." next to a script box creates a PowerShell
  script from a blank template under a name you choose, and opens it for
  editing. It is yours: TrayTrigger never changes it.

  TrayTrigger runs it like this, just before the game starts and again after
  it exits:

    powershell -NoProfile -ExecutionPolicy Bypass [-WindowStyle Hidden]
               -File <script> <1> <2> <3> <4> <5> <6...>

  -WindowStyle Hidden is there when "Run scripts hidden" is ticked, and always
  for the Test button.

  The values, in order, with the names the template's param block gives them:

    1  $Phase       prelaunch before the game, postexit after it.
    2  $GameName    The game's name as shown in TrayTrigger. A leading "-" is
                    removed, since PowerShell would read "-Foo" as a parameter
                    name rather than a value.
    3  $GameExe     The full path of the game's exe, unchanged.
    4  $GameId      A stable ID for the game. Use it to name note files.
    5  $Playtime    Empty before the game, the minutes played after it. It is
                    passed even when empty, so the values after it never shift.
    6+ $ScriptArgs  Whatever you typed in Script Arguments, split into words
                    the way Windows splits a command line: spaces separate,
                    double quotes keep a value together and are removed.
                      recommended "C:\My Tools\x.exe" force
                    arrives as three words.

  A default script (Settings > Launch & Performance) gets the game's own
  Script Arguments when it has some, and the Default Script Arguments
  otherwise.

  Unless the script runs as Administrator, the same values are also in
  environment variables: TRAYTRIGGER_PHASE, TRAYTRIGGER_GAME_NAME,
  TRAYTRIGGER_GAME_EXE, TRAYTRIGGER_GAME_ID and, after the game,
  TRAYTRIGGER_PLAYTIME_MINUTES. TRAYTRIGGER_GAME_NAME is the exact name,
  leading "-" included. Windows can't pass them through the administrator
  prompt, which is why the ID and playtime are arguments too.

  The Test button in Edit Game uses what is typed in the dialog right now:
  the Name box (or "Unnamed Game" when it's empty), the executable box and
  the Script Arguments box, with the saved game's ID. Post-exit tests pass a
  playtime of 0. A test always runs hidden and never as Administrator, and is
  stopped after 30 seconds; a real run is never stopped. The result says
  where your real settings would differ.

  The "before" run and the "after" run are separate. To pass something from
  one to the other, write a small note file in your TEMP folder named after
  the game ID, then read and delete it after the game. Both examples work
  this way.

  Exit code 0 means success. A non-zero exit code only stops the game from
  starting if you ticked "Cancel the launch if the pre-launch script fails".
  Whatever the script prints is shown by the Test button, and goes to the
  TrayTrigger log when the script runs hidden.

  A line that starts with "TT:" is for the player: TrayTrigger puts it on
  the game's Played row in Activity & History, after "ran", and shows it in
  the launch popup before the game. Say what you did, in a few words:

    Write-Output "TT: closed OneDrive and Discord"

  Up to five such lines per run are kept. When the script fails, its last
  "TT:" line explains why on the problem row. Only a script that runs hidden
  and not as Administrator is heard, since that is when its output is
  captured. Both examples and the template do this.

  Scripts run as you, without Administrator rights, unless you tick "Run
  scripts as Administrator". Neither example needs it.

  A batch file (.bat or .cmd) you already use still runs, through cmd.exe,
  but the template, the examples and the catalogue are PowerShell, and so
  is everything this README describes.

  The same details, with more examples, are in the app: Edit Game >
  "Writing your own".


WHEN SOMETHING DOESN'T WORK
---------------------------

  Press Test next to the script box. It runs the script straight away and
  shows the exit code and everything the script printed.

  Every script run is also written to the TrayTrigger log, under GameScript.
  Open it from Settings > Diagnostics & Storage. Turn on verbose logging
  there to see what a hidden script printed.


MORE SCRIPTS, AND SHARING YOURS
-------------------------------

  Ready-made scripts are in the TrayTrigger-Scripts catalogue on GitHub:

    https://github.com/stephenh678/TrayTrigger-Scripts

  Each is one PowerShell file with the same header as the examples here.
  Download it into this folder and choose it in Edit Game; its header says
  what to put in Script Arguments. "Get more scripts" beside the script boxes
  opens the catalogue.

  To share a script you wrote, open a pull request there, or post it in Show
  and tell in TrayTrigger's Discussions:

    https://github.com/stephenh678/TrayTrigger/discussions/7

  Scripts posted there are not reviewed by TrayTrigger, so read one before
  you use it. A script others can trust:

    - Start it with the same header the examples use: Name, Description,
      Author, Version, Phase, Needs admin, Dependencies, Script Arguments.
    - Use only the five values and Script Arguments. No downloads, no
      network calls, no Invoke-Expression, no installing modules.
    - Only close programs the user named or that your script started, and
      put back whatever you change.
    - Say what it did with "TT:" lines, so it shows on the Played row.
    - Explain every step in a comment.
    - Test both phases with the Test buttons.
