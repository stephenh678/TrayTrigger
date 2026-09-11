@echo off
rem ============================================================================
rem  TrayTrigger game script (batch template)
rem
rem  TrayTrigger runs this file just before a game starts and again after it
rem  exits, with these arguments in this order. The set lines below show how
rem  to read each one; the tilde in the percent form strips the quotes.
rem
rem    1  phase        "prelaunch" or "postexit"
rem    2  game name    as shown in TrayTrigger (percent signs removed)
rem    3  game exe     full path of the game's executable
rem    4  game id      TrayTrigger's stable ID for this game
rem    5  playtime     minutes played: empty on prelaunch, a number on postexit
rem    6+ script args  Edit Game, Script Arguments, exactly as typed
rem
rem  When the script is not run as Administrator the same values are also in
rem  the environment as TRAYTRIGGER_PHASE, TRAYTRIGGER_GAME_NAME,
rem  TRAYTRIGGER_GAME_EXE, TRAYTRIGGER_GAME_ID and TRAYTRIGGER_PLAYTIME_MINUTES.
rem
rem  Exit code 0 means success. A non-zero exit code cancels the launch when
rem  "Cancel the launch if the pre-launch script fails" is ticked.
rem  Everything this script prints is captured to the TrayTrigger log when it
rem  runs hidden, and is shown by the Test button in Edit Game.
rem
rem  Gotchas
rem    cmd still parses rem lines: do not write argument references, redirect
rem    arrows, ampersands or pipes inside comments.
rem    Never put the playtime value directly before a redirect arrow. cmd reads
rem    a digit followed by the arrow as a handle redirect and writes nothing.
rem    Put a space or brackets between them.
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
exit /b 0

:postexit
rem --- Runs after the game exits. The PLAYTIME variable holds the minutes played
echo Post-exit for "%GAME_NAME%" after %PLAYTIME% minute(s)
exit /b 0
