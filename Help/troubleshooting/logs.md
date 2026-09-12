# Reading the log and reporting a problem

## Where the log is

TrayTrigger writes debug.log to its local cache folder under %LocalAppData%\TrayTrigger. Settings › Diagnostics & Storage shows the exact path and has buttons to open, clear, or reveal it. When the log grows large it is archived as debug.old.log and a fresh one starts.

## What is in it

- Every run starts with a banner giving the TrayTrigger version, your Windows build, the machine and user name, and the .NET runtime. When a log covers several runs, the banners are what separate them.
- Every line has a full date and time, a level, and a category such as Launcher, PerformanceProfile, GameScript, UpdateService, or Storage.
- Info lines record normal activity: launches, profile apply and restore, update checks.
- Warning and Error lines are where to look first when something went wrong.

## Verbose logging

Turn on Verbose Logging under Settings › Diagnostics & Storage when you are about to reproduce a problem. It adds detailed traces such as launch parameters, title-matching scores, icon extraction steps, and each registry value read. Turn it back off afterwards, since it makes the log much larger.

Leave it on across the whole session you are reporting, including the launch and exit either side of it. A log with verbose on for only a few seconds rarely contains the moment that matters.

## Reporting a problem

- Reproduce the issue with verbose logging on, so the log captures it.
- On the About page, click Copy Diagnostic Info. That puts your Windows build, .NET version, and library statistics on the clipboard.
- Open Report an Issue on the About page, describe what you did and what happened, paste the diagnostic info, and attach debug.log.

> The log can contain game paths and titles from your library. Skim it before attaching if that concerns you.
