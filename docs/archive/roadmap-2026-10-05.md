# Roadmap / Ideas Backlog

Notes on things discussed but not yet implemented, kept here so they survive between sessions.

## Open

### Notifications and Activity & History (owner's design, built 2026-10-04, released in 1.5.0)

**As built.** `Services/ActivityService.cs` (the history in `activity.json`, live states, grouping),
`ViewModels/ActivityViewModel.cs` and `Views/ActivityView.xaml` (the page), `App.Activity.cs` (the
after-game notification, tooltip line, tray menu row, toast click, startup tweak check),
`Help/general/activity.md`. Debug checks: `--screenshot-activity <png> [empty] [expanded]` and
`--test-activity <txt>`. Where it differs from the design below, and why:
- **One live state in v1:** a profile setting that couldn't be put back (Critical, Restore Previous
  retries it; `PerformanceProfileService` now reads the power plan back and checks each restore's
  result). A System tweak Windows changed back is a one-time Problem entry instead of a state
  (`AppSettings.TweaksAppliedByTrayTrigger`): the user may have changed it on purpose, and a state
  would keep the dot lit with nothing to dismiss it. Display refresh rate, PCIe lanes, games on a
  hard drive and not-installed games stay on their own pages, for the same reason - often
  deliberate or permanent.
- **Not installed / installed again** are Activity entries per pass (`GameEntry.LastKnownInstalled`;
  a first check after an update only remembers, so nothing floods).
- **A scan "finds", it doesn't "add":** Scan for Games always ends in the picker, so the entry is
  "The startup scan found N new games" and the setting is "Notify me when a scan finds new games",
  under "Automatically scan on startup".
- **Games the user adds are recorded** (owner's call after testing the beta): an empty History
  right after adding games looked broken. One quiet Activity line per import ("Added Elden Ring
  and Hades (Steam)"), no toast, no dot - an exception to "not recorded: what the user just did".
- **Play sessions are recorded** (owner's call): "Played Elden Ring · 2h 17m", merged with the
  profile line ("· Aggressive profile put back", level Change) so one game exit is one row, and
  grouped per game (`played|{gameId}`) so the page stays a history, not a log of every launch.
  The owner's rule: a history of important activities, not a logfile, and alerts on critical things.
- **Tools closing as set aren't recorded** (owner's call, by that rule): only a tool that didn't
  start or couldn't be closed is, as a Problem.
- **Posters and categories found aren't recorded** (owner's call): background housekeeping,
  visible in the Library.
- **DLSS Override set again before launch isn't recorded** (owner's call): upkeep. Another app's
  change, or the driver refusing it, is a Problem.
- **Every Performance Profile and tweak change is recorded** (owner's call): a game's profile, CPU
  Cores and DLSS Override (Edit Game, card menu, batch; DLSS's Restore All), a tier's tweak on the System page, the new-game profile, and what Reset to
  Defaults changed in them (`PerformanceActivity`), alongside System tweaks applied or restored. These
  decide what TrayTrigger changes on the PC, so they're history even when the user made them.
- **Every row has a level badge** (owner's choice, mockup option C): the System page's badge style,
  CRITICAL solid red like OPTIMAL, PROBLEM amber like RESTART, CHANGE blue, ACTIVITY grey like
  OPT-IN, each with an icon. Replaces "an uppercase tag only where something needs flagging": with
  more entries the user couldn't tell them apart, and solid-versus-tinted keeps CRITICAL standing out.
- **FIXED** (owner's call): when Restore Previous puts everything back, the Critical entries get a
  green FIXED badge (`ActivityEntry.FixedUtc`, `ActivityService.MarkFixed`) and stop lighting the
  bell, instead of a separate "Put back" row; a partial fix still records what was put back. By
  entry: only the Critical entries this run reported, never an earlier run's that Restore Previous
  didn't retry, and a row with one still unfixed shows no FIXED.
- **More Critical and Problem entries** (owner's call): a damaged crash-recovery snapshot is Critical
  (it was read as "nothing to restore" and only logged); a game that couldn't be resumed, DLSS
  Override not put back when a game was removed, and an unreadable settings/library/tools file are
  Problems.
- **Tray menu row** sits first under the search box, which stays the first row so typing works.
- **Existing toasts:** "An error was logged" became a Problem entry; Suspend's and Close Game's
  stay immediate, being the answer to something the user just did.

**The gap.** Outside the Debug/verbose log, TrayTrigger tells the user things through channels
that are gone or unseen when it matters: the status bar line (overwritten, and invisible while the
window is hidden, which is most of the time during a game), the launch popup (launch only), tray
toasts (Windows suppresses them in fullscreen games and under Do Not Disturb, which TrayTrigger's
own profile can turn on) and dialogs (wrong mid-game). The log has roughly 290 Warn/Error calls
written for diagnosis, not for players. TrayTrigger is more than a launcher - profiles, system
tweaks, tools, scripts, hardware checks - so problems don't all belong to one game, and nobody
should have to visit Library, Tools and System to find out whether something is wrong.

**Two kinds of thing, handled differently.**
- **States: wrong right now.** Power plan still High Performance after a game, a tweak Windows
  reset, a display below its fastest refresh rate, a newer GPU driver, a game not installed. Worked
  out live from the app's state, never stored as a message: shown until fixed, gone by itself when
  fixed. Nothing to dismiss.
- **Events: happened once.** A post-exit script failed at 21:04, a tool didn't start, a profile was
  applied and restored, a scan added games. Recorded in History.

**One place: a bell at the bottom of the sidebar, with Exit.** Below the sidebar's bottom divider,
grouped with the power button as the tooling area, apart from the five sections (which are places
to go). It does two jobs:
1. **Notification.** A small red dot, like the Library filter button's dot, while anything needs
   the user: a current state, or a problem recorded since the page was last opened. Shown with the
   sidebar collapsed. It is the only indicator: no dots on the other sections, one place to learn.
   The count is in the page header, not on the bell.
2. **The way into History.** Clicking it opens the page.

**The page: "Activity & History"**, built to the same anatomy as Tools, System & Performance and
Settings (mocked up 2026-10-04, checked against screenshots of the current pages):
- **Header:** the title, a muted one-line subtitle ("What needs your attention, and what
  TrayTrigger changed and put back"), and status text at the top right like every page has ("5
  tools", "Saved in real time"): "1 needs attention" in red, or "No actions needed" with a check.
- **Tab strip:** All / Problems / Changes / Activity, the same segmented control as the other pages.
- **Search:** the full-width search box the Library and Tools pages use ("Search activity by game,
  tool, or what happened...").
- **Section labels** in the blue uppercase style with an icon, as on System: "NEEDS ATTENTION",
  "RECENT · last 90 days".
- **Rows inside cards** with dividers, as System groups its tweaks. No loose rows on the page.
- **NEEDS ATTENTION** only when something is wrong: the current states, one row each, with its fix
  button in the existing outline style ("Restore Previous", "Open Display Settings", "Edit Game",
  "Get Driver"). Gone when empty; the header then says "No actions needed".
- **RECENT:** newest first, one plain line per entry and its time ("Today 22:31"). An uppercase tag
  only where something needs flagging, used as sparingly as MISSING or NOT INSTALLED: CRITICAL on a
  state, a count ("7 TIMES") on a repeated problem. Everything else is plain text.
- **Clicking a row** expands it: each time it happened, "Show in log", and Copy details. Nothing
  extra shows until asked.
- **Nothing to manage.** No unread state per entry, no dismiss, no clear: opening the page clears
  the dot's "since last opened" part, states clear themselves when fixed, and entries go by
  themselves after about 90 days. Included in Copy Diagnostic Info and Save Report as "Recent
  problems", which is why the page has no copy-everything button of its own.

**Toasts.** Never during a game. After the last game exits, one toast for anything serious ("Elden
Ring's post-exit script failed", "The power plan couldn't be put back"), clicking it opens Activity & History.
The tray icon's tooltip gets a line, "2 things need attention", and the tray menu a first row while
there are some, opening the page. One setting only: **"Notify me when a scan adds games"**, a toast
as well as the History entry, the startup scan included.

**Levels.** Critical (the PC was left changed), Problem (something didn't do what you asked),
Change (what TrayTrigger changed and put back - the record that makes "puts back what it changed"
checkable), Activity (what it did on its own in the background). Not recorded: the result of
something the user just did and is looking at (a scan they started, a setting they changed).

**Events, checked against the code (2026-10-04).** *There* = already detected; *Partly* /
*Logged only* / *Needs* = what's missing.

States:
- A profile tweak not restored. *Partly:* `PerformanceProfileService` restores per tweak and
  logs; the restore methods need to report a failure upward.
- Elevated tweaks left for the next start (Windows shutdown, no prompt possible). *There:*
  `anythingDeferred`.
- A system tweak no longer in the state TrayTrigger left it. *There:* the System page reads each
  tweak's real state.
- Display below its fastest refresh rate, GPU on fewer PCIe lanes, games on a hard drive. *There:*
  the 1.4.8 hardware warnings.
- Newer NVIDIA driver. *There, on request only:* the check runs when the user clicks.
- Games NOT INSTALLED or MISSING. *There:* `GameAvailability`.

Problems:
- A pre-launch script that failed or timed out, own or default. *There:* `PreLaunchScriptResult`.
- A post-exit script that failed. *Logged only:* the result needs surfacing.
- A tool that didn't start with a game. *There:* `CompanionToolService.StartFailed` (a declined
  administrator prompt deliberately isn't reported).
- A tool that couldn't be closed. *There:* `EndResult.StillRunning`.
- DLSS Override refused or conflicting. *There:* `DlssOverrideService`.
- An edited example script set aside as `.previous`. *Logged only:* `ScriptLibraryService.SetAside`.
- The existing "An error was logged" toast (`App.xaml.cs`, L-24).

Changes:
- Profile applied and restored, per game. *There:* `BeginGameSession` / `EndGameSession`.
- Crash recovery restored settings at startup. *There:* `RecoverFromCrashIfNeeded`.
- A game left suspended, resumed at startup. *There:* `ResumeLeftSuspended`.
- System tweaks applied or reverted from the System page. *There.*
- Tools started and closed with games. *There.*

Activity:
- The startup scan added games. *Needs* the scan to return what it added.
- Games tagged not installed or found again since last time. *Needs* the last known state stored.
- Posters or game info caught up after a key was added. *There.*
- Playtime recorded for a game still running at exit. *There:* `RecordPlaytimeOnShutdown`.
- An example script retired. *Logged only:* `RetireDropped`.
- A backup restored. *There:* the restore result.
- Updated to a new version. *Needs* the last-run version stored.

**Existing toasts to route through it:** Suspend's messages, Close Game's result, the error toast
and `MainViewModel.RequestTrayNotification`.

**Cost.** A medium feature: the history store, the Activity & History page (list, search, tabs, grouping),
the live status for the top of the page and the dot, the after-game toast, and recording at 12
to 15 places, shared by History and the toasts - one system, not two. The risk is scope creep; the
rules above (no per-entry state, chosen events, states derived not stored) are what hold it.

**Considered and dropped:** an inbox with read/unread and dismiss per entry (a habit users must
keep up, or a badge they learn to ignore), status dots on every sidebar section (more places to
look), and a slide-in panel (no such surface exists in TrayTrigger; Activity & History is a page, built like the others).

### Tools started per game (proposed 2026-10-04, parked by the owner)

Today a tool with "Start when I launch a game" starts with every game. Starting SimHub only for
racing games needs the Example-StartCompanionApps.ps1 script, which is kept for exactly that in
1.4.8 (reworked to close politely, with an opt-in "force"). The owner found this proposal too
involved for 1.4.8-beta.2; the per-game script covers it for now.

The design mirrors default scripts (every game, a game's own, an opt-out):

- **Edit Tool:** "Start when I launch a game" gets a choice: with every game (today), or with the
  games I pick.
- **Edit Game, a Tools section:** a checkbox per "games I pick" tool ("Start SimHub with this
  game"); "every game" tools listed as such, with one opt-out, "Don't start tools with this game",
  like "Don't run the default scripts for this game".
- **Library batch menu:** select games, right-click, Start with > SimHub; ticked when every selected
  game has it, as batch DLSS Override is.
- **Storage:** on the game, like its scripts: `GameEntry` holds the picked tool IDs and the opt-out;
  `ToolEntry` holds the every-game / games-I-pick choice. A removed tool leaves a stale ID that is
  ignored and dropped on the next save. Backup & Restore carries both files already.
- **Starting:** `CompanionToolService.StartForGame` already knows the game, so it filters there.
  Closing is unchanged (after the last game exits).
- **Display:** the With Games tab and the controller badge say "With 12 games" for a picked tool.
- **Then:** retire Example-StartCompanionApps.ps1 through `ScriptLibraryService.RetiredFileNames`.

Considered and dropped: choosing by category. A game has one category, so "Racing" can't also be
"Favorites", and one racing game filed elsewhere would be missed.

### Feature research for 1.4.7 and later (owner decided 2026-10-02; items 1-4 built in 1.4.7-beta.3)

**Built 2026-10-02**, shipped in 1.4.7-beta.3. Where each landed:

- 1, Backup and restore: `Services/BackupService.cs`, `Services/BackupPathFixer.cs`, `App.Restore.cs`,
  Settings › Diagnostics & Storage › Backup & Restore, `Help/troubleshooting/backup.md`.
- 2, CPU Sets: `Services/CpuTopology.cs` (layout from CPU Sets and L3 sizes, pure),
  `Services/CpuTopologyService.cs` (reading and applying), `ViewModels/CpuCoreMenu.cs`, Edit Game
  › Performance, System › Hardware, `Help/profiles/cpu_affinity.md`.
- 3, Suspend: `Services/ProcessLauncherService.Suspend.cs`, `Services/ProcessSuspender.cs`
  (NtSuspendProcess and per-process mute), `Services/AntiCheatDetector.cs`, `App.SuspendGame.cs`,
  the Ctrl+Alt+P hotkey, the tray's Now Playing items, `Help/library/suspend.md`.
- 4, Background apps: `Scripts/Library/Example-CloseBackgroundApps.ps1` 2.0.
- Debug-only end-to-end checks: `--test-suspend <out.txt>` and `--test-restore-restart <folder>`.

Researched against similar apps: Playnite and its most-used extensions, Heroic, Lutris, Project
OptM, Razer Cortex, Hone, Process Lasso, CPU Set Setter, Nyrna, Borderless Gaming, Ludusavi.
Demand was judged from GitHub stars, from upvotes on the Playnite and Heroic issue trackers, and
then from Reddit (see Reddit findings below). Reddit can't be read directly. The web search tool,
the built-in browser and Claude in Chrome all refuse reddit.com; Chrome was re-tested on 2026-10-02
and still says "safety restrictions". Reddit also closed self-service API apps on 2025-11-11
(Responsible Builder Policy), so reddit-mcp-buddy can't search without an approved app. Reddit was
searched through the gemini-search MCP server (Google Search grounding) instead. The Reddit
findings are therefore Gemini's summaries of the threads, not first-hand reads, and they include
no vote counts. Each item below was checked against the code: none of them exists today
(re-checked 2026-10-02).

**Scope rule from the owner:** TrayTrigger is a launcher. Features that start games from outside
TrayTrigger (command line, desktop shortcuts, Stream Deck) or detect games started outside it
are out of scope.

**Owner's decisions (2026-10-02),** numbered in build order. Items 1 to 4 are accepted; item 5
is declined.

1. **Backup and restore, TrayTrigger's own data only (accepted).** Today only a rolling `.bak`
   per file exists (`Services/StorageService.cs`). Settings › Backup saves games, settings,
   tools, scripts and cached art to one file. Restore rescans, re-links games by launcher ID and
   fixes paths whose drive letter changed. Game saves are out: no save backup and no Ludusavi
   manifest (see Declined). Playnite's "Library cloud sync" request (22 upvotes) is the same need.
2. **CPU Cores for AMD X3D, done with CPU Sets (accepted).** Today CPU Cores is
   `PerformanceCoresOnly` with a hard `ProcessorAffinity` (`Services/CpuTopologyService.cs:127`).
   There is nothing for dual-CCD X3D (7950X3D, 9950X3D), and hard affinity can crash some games
   or be blocked by anti-cheat. CPU Set Setter (737 stars) and Game Optimizer (211) use
   `SetProcessDefaultCpuSets` instead, offer V-Cache CCD and no-SMT masks, and follow child
   processes. The design:
   - **Detect the CPU, with no list of models.** `CpuTopologyService` already calls
     `GetSystemCpuSetInformation` but reads only `EfficiencyClass`. Also read
     `LastLevelCacheIndex` (offset 16, only in the layout comment today) to group cores by CCD,
     then read each L3's size with `GetLogicalProcessorInformationEx` (`RelationCache`). On a
     dual-CCD X3D the CCD with the larger L3 (96 MB against 32 MB) is the V-Cache CCD, so future
     X3D chips work without an update. The CPU name (`SystemInfoService`) is for display only.
   - **Offer only what fits this CPU, and recommend one.** Edit Game › Performance lists the
     options for the detected CPU plus an Auto choice that picks the recommended one:

     | CPU | Options | Auto picks |
     |---|---|---|
     | Dual-CCD X3D (7950X3D, 9950X3D) | V-Cache cores, Frequency cores | V-Cache cores |
     | Single-CCD X3D (7800X3D, 9800X3D) | None: every core shares the cache | n/a |
     | Dual-CCD without X3D (7950X, 9950X) | One CCD | Default (no change) |
     | Intel hybrid (12th gen and later) | P-cores only | P-cores only |
     | Any other CPU | Option hidden | n/a |

     System › Hardware specs shows what was detected, for example "2 CCDs, V-Cache on cores
     0-15". `CpuAffinityMode` gains Auto, V-Cache, Frequency and One CCD; `PerformanceCoresOnly`
     stays so existing games keep their setting.
   - **Apply with CPU Sets,** not `Process.ProcessorAffinity`. CPU Sets are Windows' soft form of
     affinity. Apply them to the game's whole process tree (`ProcessTree`) so child processes
     follow.
   - **An optional delay before applying,** per game, for anti-cheat titles that block an early
     change. This is Reddit's Process Lasso workaround.
   - **Leave AMD's driver alone.** TrayTrigger doesn't touch core parking or Game Bar, and a game
     left on Default is not changed.
3. **Suspend and resume the running game (accepted).** A hotkey and a Now Playing tray item that
   freeze the game's process tree (`NtSuspendProcess`), mute it, and stop the playtime clock until
   resumed. For unpausable cutscenes and stepping away. Nyrna (1.3k stars) does only this;
   Playnite has it as the PlayState extension. Must refuse games with kernel anti-cheat (EAC,
   BattlEye) and warn that online games will disconnect. Builds on `ProcessTree` and Close Game.
   Reddit's pitfalls (below) add two rules: Close Game resumes the game before closing it, and
   the Now Playing tray item is the way back in, since a suspended game can't be switched to.
4. **Background apps while you play (accepted as a bundled script, not built into the app).**
   Today `Example-CloseBackgroundApps.ps1` closes the processes named in Script Arguments and
   reopens them after; it doesn't need admin. Extend that script:
   - "recommended" in Script Arguments closes a recommended list, starting from Reddit's:
     browsers, Discord, RGB and peripheral software, idle launchers. With names given, it closes
     those too. Empty does nothing, as before (owner, 2026-10-02): bundled scripts update
     themselves, and a setup relying on "no arguments = no-op" must not start closing apps.
   - Run as administrator, it also stops Windows Update (`wuauserv`) and Delivery Optimization
     (`DoSvc`) for the session and starts them again after. Without admin it skips that step and
     logs why. Windows can start `wuauserv` again by itself, so test that a stop lasts a session.
   - It reopens or restarts exactly what it closed or stopped, and logs what it did. No RAM
     cleaning and no "boost" score.
   - Efficiency mode (EcoQoS) is left out: PowerShell can reach `SetProcessInformation` only
     through inline C# (`Add-Type`).
   - Scripts don't get the Performance Profile session's crash recovery. If TrayTrigger or
     Windows crashes mid-game, closed apps stay closed. The services come back at the next
     restart, because stopping a service doesn't change its startup type.
5. **Borderless window per game (declined).** Most current games have their own borderless or
   windowed-fullscreen mode, and TrayTrigger's Optimizations for Windowed Games (DirectFlip
   Model) tweak already covers those. Special K and Magpie cover older games that lack one. The
   open-source Borderless Gaming is no longer updated.

**Dropped after the Reddit research:** "Will it run?" in Game Details. Steam's minimum and
recommended requirements are already fetched (`SteamAppDetails.PcRequirementsMin/Rec`) but only
shown as text (`GameDetailsViewModel.cs:471`), and the hardware is already known
(`SystemInfoService`). Comparing them would give "Meets recommended" or "GPU below minimum", as
Playnite's System Checker extension does. Reddit's experience with Can You Run It says that
comparison misleads. On top of that, Steam's requirements are free text, and comparing GPUs needs
a GPU ranking table that must be kept current.

**Reddit findings (2026-10-02, via gemini-search):** These cover r/playnite, r/pcgaming,
r/OptimizedGaming, r/pcmasterrace, r/Steam and r/buildapc. Most of the X3D discussion is in r/Amd
and r/AMDHelp, so those two were added. Gemini named its threads for some topics only; the named
ones are under Sources.

- **Suspend and resume: clear demand.** Redditors keep building this themselves:
  - a Windows app for unpausable cutscenes, posted to r/pcgaming and later sold on Steam;
  - Universal Pause Button;
  - an Xbox Game Bar widget, Suspended N Time, which can also suspend the game when it loses focus;
  - the Resource Monitor "Suspend process" trick.

  Reported pitfalls: some games crash on resume, Windows shows "Not Responding" while a game is
  suspended, and a suspended game can't be switched to or closed until it's resumed. Online games
  disconnect. So Close Game must resume the game first, and the tray item is the way back in.
- **CPU Sets for X3D: the most specific demand found.** Many 7950X3D and 9950X3D owners report
  games landing on the wrong CCD even with Game Bar and AMD's V-Cache driver, and fall back to
  Process Lasso. Their complaints about it match the proposal:
  - they don't know which cores belong to which CCD;
  - anti-cheat blocks affinity changes (Denuvo, EA and EAC titles are named);
  - the workaround is to apply the rule some seconds after launch.

  CPU Sets are preferred over affinity. This supports detecting the V-Cache CCD automatically and
  adding a delay before the rule is applied.
- **Background apps: wanted, but boosters are distrusted.** r/pcmasterrace and r/pcgaming call
  Razer Cortex snake oil or bloat. The complaints:
  - Auto-Boost freezing the PC after a game closes;
  - Cortex's own RAM and CPU use;
  - Booster Prime's bad advice on game settings;
  - its power plan benchmarking worse than High Performance.

  The same threads recommend closing named apps by hand instead: browsers, Discord, RGB and
  peripheral software, idle launchers. Windows Update downloading during a game is a frequent
  stutter complaint. So: named apps only, restore exactly what was closed, and show what was done.
  No RAM cleaning and no "boost" score.
- **Borderless window: steady demand.** Borderless Gaming is called a godsend for older and indie
  games. Its free open-source version stopped being updated when it moved to a paid Steam release,
  so users switched to No More Border, GoBorderless or Special K. Same caveat as the proposal: the
  game must be set to windowed mode first.
- **"Will it run?": the evidence is against it.** r/pcgaming, r/pcmasterrace and r/buildapc
  threads about Can You Run It agree that comparing hardware with a game's published requirements
  is unreliable. Requirements are vague or inflated, older high-clock CPUs get over-rated, and VRAM
  is ignored. Users trust benchmark videos instead. That is the comparison this proposal would make.
- **Backup and restore: the demand is about paths and saves.** r/playnite threads ask how to change
  many game paths at once after moving to a new PC, moving a drive, or a drive letter change. The
  answers are workarounds: the Path Replacer add-on, symlinks, SUBST, or fixing the letter in Disk
  Management. That is the proposal's re-link step. Playnite 10 has built-in backup and restore and
  LaunchBox has cloud sync, so TrayTrigger is behind both. Ludusavi's r/pcgaming release threads
  were well received, and non-Steam games have no cloud saves, so save backup has its own demand.
  The owner declined it for TrayTrigger.
- **Complaints about the other apps:**
  - Playnite: slow startup and UI lag, mostly blamed on themes and extensions; setup with plugins
    takes time; Fullscreen mode lacks Desktop features.
  - GOG Galaxy: integrations keep needing a new login; development is slow.
  - LaunchBox: Big Box is paid; it slows down with large libraries.
  - Process Lasso: CPU Sets are confusing to set up, anti-cheat blocks it, and the free version
    nags.
  - NVIDIA App: the overlay's Game Filters and Photo Mode cost up to 15% FPS, Control Panel
    features are still missing, and DLSS Override doesn't always apply.

  TrayTrigger already avoids the ones in scope. It reads each launcher's local install records,
  so there are no logins to expire, and it needs no plugins. It also closes launchers after the
  game exits and re-applies DLSS Override on every launch.
- **Already built:** per-game automatic HDR (Optimized › Enable HDR) and closing the EA app,
  Ubisoft Connect or Epic after the game came up repeatedly, and TrayTrigger has both. Stopping
  launchers from auto-updating games also came up; that is the launcher's job and out of scope.

**Build order (owner, 2026-10-02):** Backup and restore, CPU Sets for X3D, Suspend and resume,
then the Background apps script. (Before Reddit the suggested order was Backup, Background apps,
CPU Sets, Suspend, Borderless, then "Will it run?".)

- **Backup** goes first: it is low risk and protects everything else.
- **CPU Sets and Suspend** come next: they have the most specific Reddit demand, and people are
  building their own tools for both. CPU Sets builds on the CPU Cores option. Suspend builds on
  `ProcessTree` and the tray's Now Playing section.
- **Background apps** comes last and stays a script. Reddit distrusts boosters, and
  `Example-CloseBackgroundApps.ps1` already does the core of it.

**Declined by the owner (don't re-propose):** per-game resolution/refresh rate and audio device;
more NVIDIA driver settings per game (frame cap, Low Latency, V-Sync, power mode); several
launch options per game; play history, stats and Steam playtime import; launching games from
outside TrayTrigger or detecting games started outside it; game-save backup, including through
Ludusavi's manifest (Backup covers TrayTrigger's own data only); a borderless window per game;
background apps as a built-in feature (it is a bundled script instead).

Sources: [Playnite](https://github.com/JosefNemec/Playnite),
[PlayniteExtensionsCollection](https://github.com/darklinkpower/PlayniteExtensionsCollection),
[System Checker](https://github.com/Lacro59/playnite-systemchecker-plugin),
[Nyrna](https://github.com/Merrit/nyrna),
[Borderless Gaming](https://github.com/andrewmd5/Borderless-Gaming),
[CPU Set Setter](https://github.com/SimonvBez/CPUSetSetter),
[Game Optimizer](https://github.com/charlie754/Game-Optimizer-CPUs-Threads-Optimizer),
[Project OptM](https://github.com/exaiver2019/ProjectOptM),
[Hone vs Razer Cortex](https://hone.gg/comparison/razer-cortex),
[Ludusavi](https://github.com/mtkennerly/ludusavi),
[Heroic issues](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues).

Reddit threads that gemini-search named. The URLs are as Gemini gave them; none were opened, so
check one before quoting it.

- Suspend:
  [Pausing game anytime like home button on console](https://www.reddit.com/r/pcgaming/comments/nuv9x0/) (r/pcgaming),
  [Unpausable cutscenes: I made a Windows application that will pause them](https://www.reddit.com/r/pcgaming/comments/e7529l/) (r/pcgaming),
  [PSA: You can pause the game ... using UniversalPauseButton](https://www.reddit.com/r/remnantgame/comments/158656d/) (r/remnantgame).
- X3D (no URLs given): "7950x3D and process lasso", "Process Lasso: CPU sets or Affinities?" and
  "Is project lasso still needed for the 7950X3D chips?" (r/Amd); "7950X3D - core parking mess"
  and "AMD Core Parking Issues (9950x3d)" (r/AMDHelp); "7950x3d How to Assign the right CCD in
  Process Lasso" (r/pcmasterrace).
- Background apps, Razer Cortex (no URLs given): "Razer cortex bad?", "Razer Cortex Yes Or No??"
  and "Should I use Razer Cortex Power Plan in Power Settings?" (r/pcmasterrace).
- Will it run:
  [Are websites like SystemRequirementsLab and PCbenchmark actually reliable](https://www.reddit.com/r/pcgaming/comments/1ch00r1/) (r/pcgaming),
  [Anyone else find "Can you run it" slightly inaccurate?](https://www.reddit.com/r/pcmasterrace/comments/2z0vpg/) (r/pcmasterrace),
  [How accurate is Can you run it?](https://www.reddit.com/r/pcmasterrace/comments/ij777c/) (r/pcmasterrace),
  [Is systemrequirementslab.com (Can I Run It) accurate?](https://www.reddit.com/r/buildapc/comments/1x1f3r/) (r/buildapc).
- Backup:
  [Is there a way to mass change game paths?](https://www.reddit.com/r/playnite/comments/10t4x35/),
  [How to handle path to external drive?](https://www.reddit.com/r/playnite/comments/hmj8f9/),
  [Moved my ROMs, is there an easy way of changing their paths?](https://www.reddit.com/r/playnite/comments/s9e53h/) (all r/playnite),
  [PSA: If Steam ever can't find a game because the drive letter changed](https://www.reddit.com/r/pcmasterrace/comments/8ur97q/) (r/pcmasterrace),
  [Ludusavi: A new, open source tool for backing up game saves](https://www.reddit.com/r/pcgaming/comments/hndnly/) and
  [Ludusavi v0.11.0](https://www.reddit.com/r/pcgaming/comments/wu975t/) (r/pcgaming).
- Borderless, GOG Galaxy, Playnite, LaunchBox and NVIDIA App findings came from Gemini summaries
  that named no threads.
## Shipped

### 1.4.0: scripts for advanced users (2026-09-11)

Decisions made while building it:

- **Positioning.** Scripts stay an advanced, off-by-default feature. 1.4.0 makes them more
  powerful and easier to debug, not "easy". The release notes should say that.
- **Real-world examples (decision reversed the same day).** The first cut shipped three
  Windows-only examples (session CSV log, zip a named folder, close/restart a named program),
  because templates for third-party apps depend on their install paths and command lines and can
  read as a TrayTrigger bug when those change. The owner found them hard to follow and not useful,
  so 1.4.0 ships five real-world examples instead: Wallpaper Engine pause, Quiet Mode, Companion
  Apps, OBS replay buffer and Save Backup (the first two renamed in 1.4.6 to Close Background Apps
  and Start Companion Apps, for what they do), plus the two blank templates and a task-first README
  (`Scripts/Library/`, `Services/ScriptLibraryService.cs`). The breakage risk is handled inside
  the scripts: each is presented as an example to copy, declares its dependencies in a
  header (Name, Description, Phase, Needs admin, Dependencies, Script Arguments), finds the
  third-party app at run time or through one setting at the top, and exits 0 with a plain message
  when the app isn't there. All are PowerShell except the batch template. An audio-device switcher
  and a display-mode changer were considered and left out: both need a large inline C# block that
  is hard to learn from.
- **Two script fields, not one.** A single-script model breaks for the two most common
  attachments: a plain `.exe` that cannot branch on the phase, and different types per phase.
  A dual-phase script is supported by putting the same file in both boxes, which the examples do.
- **Test Run** uses the values typed in the dialog, ignores the Settings kill-switch, always runs
  hidden and non-elevated so output can be captured, and kills the process on timeout.
- **Defaults resolve per phase**, a game's own script always wins, and default scripts receive the
  game's Script Arguments so one generic default can be parameterised per game.
- **Batch gotcha found by the tests:** `echo %~5> file` is a handle redirect when playtime is a
  single digit, and `rem` lines are still parsed by cmd (`%~N` in a comment aborts the script).
  Both are documented in the help page and the scripts README.

### Folder-import launcher detection (2026-09-10)

When a game is added via "Add Folder", drag-and-drop (folder, .exe, or .lnk), or "Batch Add
Games from Folder", the exe path is now checked against each installed platform's own records
(`Services/PlatformLookupService.cs`) before it becomes a Local entry. On a match the game is
imported through that platform's normal route (real ID, name, art, launcher-aware launch) instead
of the generic exe heuristic.

Decisions made while building it, against the open questions that were listed here:

- **Blocking was considered and rejected.** Refusing drops from launcher-owned folders needs the
  same detection code as redirecting, then sends the user to a full "Scan for Games" to add one
  game they'd just handed us, and can't be complete anyway (see EA below). Redirecting costs the
  same and gives the right result.
- **Mixed parent folders** are handled by matching per candidate exe, not per dropped folder, at
  the three points where a path becomes a `GameEntry` (`HandleFileDropAsync`, `AddCandidateAsync`,
  `ImportBatchGamesAsync`). `FolderScannerService` is untouched.
- **Silent, not confirmed.** The status bar says which platform each game went through (e.g.
  "Added 3 new game(s)! (2 via Steam, 1 via GOG)") and the log records each resolution.
- **Independent of the integration toggles.** Recognition runs even when a platform's auto-scan is
  off.
- **EA custom-location installs** still can't be resolved (inherited `EaScannerService`
  limitation) and land as Local.
- A dropped exe that differs from the platform's own registered exe is replaced by the platform's
  exe. A user who wants a specific alternate exe edits the path in Edit Game *and* ticks "Launch
  this executable directly" there - the client-launch branches in `ProcessLauncherService`
  otherwise ignore `ExecutablePath` (see `GameEntry.LaunchDirectly`).
- A game already in the library as a plain Local entry for the same exe is linked to the platform
  in place (`ImportCoordinator.UpgradeExistingEntries`) rather than duplicated.
- Folder-scan candidates are resolved to their platform *before* the batch dialog
  (`GameCandidate.Platform`), so the preview shows the platform's name and logo, "in library" is
  judged by platform ID, and Ignore applies to the platform ID - the same identity the scan
  picker uses.

### Steam-specific exe-path dedup gap (2026-09-10)

`ScanForGamesCoreAsync`'s Steam task now applies the same `existingExePaths` fallback as the
GOG/EA/Epic/Ubisoft tasks, so a Steam game that reached the library without a `SteamAppId` is no
longer offered again as "new".
