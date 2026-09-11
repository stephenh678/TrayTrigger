@echo off
rem ============================================================================
rem  Example: close a program while you play, restart it afterwards.
rem
rem  What it does
rem    prelaunch  If the named program is running, remembers where its exe is
rem               (in a marker file named after the game ID, in TEMP) and
rem               closes it.
rem    postexit   If the marker exists, starts that exe again and deletes the
rem               marker. A program that was not running before the game is
rem               never started.
rem
rem  Set it as
rem    Both the pre-launch and the post-exit script of a game, or both defaults
rem    in Settings to do it for every game.
rem
rem  Script Arguments (required)
rem    The process name, for example:  Discord.exe
rem    Nothing happens if no name is given.
rem
rem  Notes
rem    Uses taskkill /f, so the program closes without saving anything.
rem    A program running as Administrator cannot be inspected from a normal
rem    script, so it is reported as "not running" and left alone.
rem
rem  Arguments TrayTrigger passes (see _Blank.bat for the full description)
rem    1 phase, 2 game name, 3 game exe, 4 game id, 5 playtime, then your
rem    Script Arguments from 6 onwards. The set lines below read them.
rem ============================================================================

set "PHASE=%~1"
set "GAME_ID=%~4"
set "PROC=%~6"

if "%PROC%"=="" (
    echo No process name given. Put one in Edit Game, Script Arguments, for example Discord.exe
    exit /b 0
)

set "MARKER=%TEMP%\TrayTrigger-restart-%GAME_ID%.txt"

if /i "%PHASE%"=="prelaunch" goto prelaunch
if /i "%PHASE%"=="postexit" goto postexit
exit /b 0

:prelaunch
set "EXEPATH="
rem Get-Process wants the name without .exe; Path is the running exe's full path.
set "PROCNAME=%PROC:.exe=%"
for /f "usebackq delims=" %%P in (`powershell -NoProfile -Command "(Get-Process -Name '%PROCNAME%' -ErrorAction SilentlyContinue | Select-Object -First 1).Path"`) do set "EXEPATH=%%P"
if "%EXEPATH%"=="" (
    echo %PROC% is not running. Nothing to close.
    exit /b 0
)
> "%MARKER%" echo %EXEPATH%
echo Closing %PROC% at %EXEPATH%
taskkill /im "%PROC%" /f >nul 2>&1
exit /b 0

:postexit
if not exist "%MARKER%" (
    echo %PROC% was not closed by this script. Nothing to restart.
    exit /b 0
)
set "EXEPATH="
set /p EXEPATH=<"%MARKER%"
del "%MARKER%" >nul 2>&1
if "%EXEPATH%"=="" exit /b 0
echo Restarting %EXEPATH%
start "" "%EXEPATH%"
exit /b 0
