# Activity & History

TrayTrigger changes things for your games and puts them back afterwards: a Performance Profile, tools that start and close with a game, scripts. Activity & History is where it says what it did and what didn't go to plan. Open it from the bell at the bottom of the sidebar, above Exit App.

## The bell

- A red dot on the bell means something needs you: a setting that wasn't put back, or a problem since you last opened the page.
- Opening the page clears the dot for problems you've now seen. Something that's still wrong keeps it until it's fixed.
- When the window is hidden, the tray icon's tooltip and a first row in the tray menu say how many things need attention. That row opens the page.

## Needs attention

What's wrong right now, each with a button to fix it. It's shown only when there's something there, and each item goes by itself once it's fixed, so there's nothing to dismiss.

- **A setting a Performance Profile couldn't put back**, such as the power plan or Windows' multimedia scheduler settings. Marked CRITICAL, because your PC was left changed. **Restore Previous** tries again; it may ask for administrator permission. Once everything is back, the item goes and the entry in Recent gets a green **FIXED** badge beside CRITICAL, with when it was put back.

## Recent

Everything TrayTrigger noted in the last 90 days, newest first. Older entries go by themselves. Each row has a badge saying what kind it is: **CRITICAL** in solid red (your PC was left changed), **PROBLEM** in amber (something didn't do what you asked), **CHANGE** in blue and **ACTIVITY** in grey.

- **Critical**: a setting a Performance Profile couldn't put back after a game, when TrayTrigger closed, or after it closed unexpectedly; and a damaged record of what a profile changed, so TrayTrigger can't tell what to put back - the entry says what to check by hand.

- **Problems**: a script that failed or wasn't found, a tool that didn't start with a game or couldn't be closed after it, DLSS Override another app changed, a System tweak a Windows update changed back, an edited example script moved aside, a game that couldn't be resumed after it was suspended and is still frozen, DLSS Override that couldn't be put back when a game was removed, a settings, library or tools file that couldn't be read at startup.
- **Changes**: what TrayTrigger changed and put back, and every change to what it's told to change - a game's profile put back after you played, a game's profile, CPU Cores or DLSS Override changed (from Edit Game, the card's menu or several games at once), Restore All for DLSS Override, a tweak switched on or off in the Optimized or Aggressive profile, the profile new games get, System tweaks you applied or restored, settings put back after TrayTrigger closed unexpectedly, a game left suspended that was resumed.
- **Activity**: what happened in your library - games you played and for how long, games you added, games the startup scan found, games no longer installed or installed again, playtime recorded when TrayTrigger closed during a game, an update installed.

Each game you play is one row, "Played Elden Ring · 2h 17m", naming the profile when one was put back, and moves to the top each time you play it. The same thing happening again is one row too: a problem shows how many times (7 TIMES), and clicking a row shows each time, what it means, **Show in log** for the technical detail, and **Copy details** for a bug report. The tabs narrow the list to Problems, Changes or Activity, and the search box finds a game, a tool or what happened.

Games you add and changes to performance settings - Performance Profiles, CPU Cores, DLSS Override and System tweaks - are listed, quietly - no notification and no dot - because they decide what TrayTrigger changes on your PC. Other settings you change aren't listed: you're already looking at the result.

## Notifications

- Nothing interrupts a game. After the last game exits, one notification sums up anything that went wrong, such as "Elden Ring's post-exit script failed". Clicking it opens Activity & History.
- **Notify me when a scan finds new games**, in Settings > Library & Art, adds a notification when the startup scan finds games. It's off by default and is the only notification setting.

## For bug reports

Copy Diagnostic Info and Save Report, in Settings > Diagnostics & Storage and on About, include the recent problems from this page.
