TrayTrigger scripts folder
==========================

This folder is where TrayTrigger keeps game scripts: the blank templates and
examples it ships with, plus any script you create with "New script..." in
Edit Game. Scripts can live anywhere; this is just the default place, and the
Browse buttons open here.

TrayTrigger writes this README on every start (it is documentation) and
writes the templates and examples only if they are missing. Edit or delete
them freely; your changes are never overwritten.


What is here
------------

  _Blank.bat                          Empty batch template with the argument
                                      contract in comments and a phase branch.
  _Blank.ps1                          The same as a PowerShell script.
  Example-LogSessions.ps1             Appends one CSV line per phase. Shows how
                                      to read every argument. Touches nothing
                                      else.
  Example-ZipFolderAfterExit.ps1      Zips a folder you name after the game
                                      exits, keeps the newest 10. Shows Script
                                      Arguments and playtime. Does nothing until
                                      you give it a folder.
  Example-CloseAndRestartProgram.bat  Closes a program you name before the
                                      game and restarts it afterwards, only if
                                      it was running. Shows phase branching and
                                      carrying state between phases via the
                                      game ID. Does nothing until you give it a
                                      process name.

The examples are deliberately small. Each one is a single file that handles
both phases by branching on the phase argument, so you set the same file as
both the pre-launch and the post-exit script.


How TrayTrigger runs a script
-----------------------------

  .bat / .cmd   cmd.exe /d /s /c "<script> <arguments>"
  .ps1          powershell -NoProfile -ExecutionPolicy Bypass -File <script> <arguments>
  .exe / .com   run directly with <arguments>

Pre-launch runs after the Performance Profile is applied and before the game
starts. Post-exit runs after the game closes and the profile is restored. If
TrayTrigger is closed while a game is still running, the post-exit script runs
then, with playtime 0.


Arguments, in order
-------------------

  1  phase        prelaunch  or  postexit
  2  game name    as shown in TrayTrigger (for .bat, percent signs removed)
  3  game exe     full path of the game's executable
  4  game id      TrayTrigger's stable ID for the game
  5  playtime     minutes played. Empty on prelaunch so positions never shift;
                  a number on postexit
  6+ script args  whatever is in Edit Game, Script Arguments

  Batch:       %~1 to %~5, then %~6 onwards. The ~ strips the quotes.
  PowerShell:  a param block as in _Blank.ps1, or $args[0] to $args[4] and
               $args[5..] for the rest.

Script Arguments are free text per game. For .bat and .cmd they are passed
exactly as typed and cmd parses them, so keep double quotes paired. For .ps1
and .exe they are split with the usual Windows rules first, so "C:\My Saves"
arrives as one argument without the quotes.

When the script is not run as Administrator the same values are also in the
environment: TRAYTRIGGER_PHASE, TRAYTRIGGER_GAME_NAME, TRAYTRIGGER_GAME_EXE,
TRAYTRIGGER_GAME_ID, TRAYTRIGGER_PLAYTIME_MINUTES. An elevated script gets no
environment from TrayTrigger, which is why everything is also an argument.


Exit codes and output
---------------------

Exit 0 means success. When "Cancel the launch if the pre-launch script fails"
is ticked, a non-zero exit code, a timeout, or a script that will not start
cancels the launch.

A script that runs hidden has its stdout and stderr captured into the
TrayTrigger log under the GameScript category. The Test buttons in Edit Game
and Settings run a script right now, hidden and not elevated, and show the
exit code and output in a dialog.


Default scripts
---------------

Settings > Launch & Performance can hold a default pre-launch and a default
post-exit script. They run for any game that has no script of its own for
that phase, with the game's own Script Arguments. Any game can opt out with
"Don't run the default scripts for this game" in Edit Game.


Gotchas
-------

  Batch: never put the playtime argument directly before a redirect arrow.
  cmd reads a single digit followed by the arrow as a handle redirect and
  writes nothing. Put a space or brackets between them.

  Batch: a "rem" line is still parsed by cmd. Avoid the redirect arrows,
  ampersands and pipes inside comments.

  PowerShell: declare the playtime parameter as [string], not [int]; it is
  empty on prelaunch.

  Any script: the game name is free text. Quote it when you build a path
  from it, and strip characters Windows will not accept in file names (see
  Example-ZipFolderAfterExit.ps1).
