# Reading the log and reporting a problem

## Where the log is

TrayTrigger writes debug.log to its data folder under %AppData%\TrayTrigger. The Settings page under Storage shows the exact path and has a button to open it. When the log grows large it is archived as debug.old.log and a fresh one starts.

## What is in it

- Every line has a timestamp, a level, and a category such as Launcher, PerformanceProfile, GameScript, UpdateService, or Storage.
- Info lines record normal activity: launches, profile apply and restore, update checks.
- Warning and Error lines are where to look first when something went wrong.

## Verbose logging

Turn on Verbose Logging in Settings when you are about to reproduce a problem. It adds detailed traces such as launch parameters, title-matching scores, icon extraction steps, and each registry value read. Turn it back off afterwards, since it makes the log much larger.

## Reporting a problem

- Reproduce the issue with verbose logging on, so the log captures it.
- On the About page, click Copy Diagnostic Info. That puts your Windows build, .NET version, and library statistics on the clipboard.
- Open Report an Issue on the About page, describe what you did and what happened, paste the diagnostic info, and attach debug.log.

> The log can contain game paths and titles from your library. Skim it before attaching if that concerns you.
