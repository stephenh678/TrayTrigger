# Reading the log and reporting a problem

## Where the log is

TrayTrigger writes debug.log to its local cache folder under %LocalAppData%\TrayTrigger. Settings > Diagnostics & Storage shows the exact path and has buttons to open, clear, or reveal it. When the log grows large it is archived as debug.old.log and a fresh one starts.

## When TrayTrigger will not start, or stops

If TrayTrigger cannot start, or stops on its own, it says so in a message box and writes what happened to crash.log, in the same folder as debug.log. The message shows the full path. crash.log is written whatever the logging setting is, so there is nothing to turn on first. Each entry has the same banner as the log (version, Windows build, machine, .NET runtime) followed by the error and where it happened. A file that grows past 256 KB starts again.

Attach crash.log when you report it. It is a short file, and it holds the one thing the report needs most.

## What is in it

- Every run starts with a banner giving the TrayTrigger version, your Windows build, the machine and user name, and the .NET runtime. When a log covers several runs, the banners are what separate them.
- Every line has a full date and time, a level, and a category such as Launcher, PerformanceProfile, GameScript, UpdateService, or Storage.
- Info lines record normal activity: launches, profile apply and restore, update checks.
- Warning and Error lines are where to look first when something went wrong.

## Verbose logging

Turn on "Enable verbose logging" under Settings > Diagnostics & Storage when you are about to reproduce a problem. It adds detailed traces such as launch parameters, title-matching scores, icon extraction steps, and each registry value read. Turn it back off afterwards, since it makes the log much larger.

Leave it on across the whole session you are reporting, including the launch and exit either side of it. A log with verbose on for only a few seconds rarely contains the moment that matters.

## Reporting a problem

- Reproduce the issue with verbose logging on, so the log captures it.
- On the About page, click Copy Diagnostic Info. That puts a report on the clipboard: version, Windows build, CPU, GPU and RAM, the number of monitors and the main one's refresh rate, which launchers are installed, library counts and how many games use each per-game setting, the settings that matter to bugs (including text size, font and theme, and which tweaks your Optimized and Aggressive profiles switch on), applied tweaks, the last Stutter Check, the last 30 days of problems and changes from Activity & History, and the last warnings from the log. API keys are never included, and your Windows user folder is replaced by %USERPROFILE% so the report doesn't give out your user name. Save Report writes the same thing to a file.
- Open Report an Issue on the About page, describe what you did and what happened, paste the diagnostic info, and attach debug.log. If TrayTrigger would not start or stopped on its own, attach crash.log too.

> The log can contain game paths and titles from your library. Skim it before attaching if that concerns you.
