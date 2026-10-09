@echo off
rem ============================================================================
rem  My TrayTrigger script
rem
rem  TrayTrigger runs this file just before the game starts (PHASE is
rem  prelaunch) and again after it exits (PHASE is postexit). The set lines
rem  below read the game's name, exe, ID and minutes played. Whatever you typed
rem  in Edit Game, Script Arguments, starts at the sixth argument.
rem
rem  Exit 0 for success. Press Test next to the script box to try it.
rem
rem  A line that starts with TT: is for the player: TrayTrigger puts it on the
rem  game's Played row in Activity and History, and shows it in the launch
rem  popup before the game. Everything else you echo goes to the log.
rem
rem  More - environment variables, exit codes, and the batch file pitfalls to
rem  avoid - is in README.txt in the scripts folder.
rem ============================================================================

set "PHASE=%~1"
set "GAME_NAME=%~2"
set "GAME_EXE=%~3"
set "GAME_ID=%~4"
set "PLAYTIME=%~5"

if /i "%PHASE%"=="prelaunch" goto prelaunch
if /i "%PHASE%"=="postexit" goto postexit
echo Unknown phase "%PHASE%"
exit /b 1

:prelaunch
rem --- Runs just before the game starts ---------------------------------------
echo Pre-launch for "%GAME_NAME%"
echo TT: nothing set up yet
exit /b 0

:postexit
rem --- Runs after the game exits. The PLAYTIME variable holds the minutes played
echo Post-exit for "%GAME_NAME%" after %PLAYTIME% minute(s)
echo TT: nothing to put back yet
exit /b 0
