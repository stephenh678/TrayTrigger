# Roadmap

Written 2026-10-23, replacing the earlier roadmaps. Ten items, in the order to work them, from a look at
Playnite, GOG Galaxy, LaunchBox, Heroic, AutoActions, Display Changer, Process Lasso, Razer Cortex,
Lossless Scaling and Ludusavi. The competitor claims are web-search summaries, not first-hand reads; check
one before relying on it. Nothing here is scheduled until the owner says so.

Released as 1.7.5: the 1.7.1 betas (1 to 5) were renamed when the owner chose the stable number, as 1.6.3 became 1.7.0.

In progress as 1.7.6-beta.1: Game Details is the page for each game (header with Play and the recorded figures, Overview, and a Settings tab that hosts Edit Game in place, so Edit has no window of its own). Only facts TrayTrigger already records are shown, never a measured result.

## Rules

- **Proven benefits only.** A tweak or claim is added only with independent, measured evidence.
- **No performance numbers.** Nothing that shows or implies an FPS, 1% low or "faster" result for a game
  (owner, 2026-10-23: frame rates vary too much to promise one). That rules out per-game A/B testing,
  session receipts and a shared tweak dataset.
- **Report, don't tweak, unless proven.** Stutter Check detects and points.
- **Nothing interrupts a game, and anything left changed is put back.**
- **Source-agnostic.** No crack, NoDVD, release-group or repack handling.
- **Still declined:** launch-in command, the Steam/GOG wrapper, late attach, closing launchers globally,
  muting other apps, a Clear Shader Cache button, shader pre-compiling.

## The list

| # | Item | Status |
|---|---|---|
| 1 | Activity gaps: Edit Game changes, game removal and undo | **Done** (1.7.1-beta.2; entries seen in Activity during beta 3 testing) |
| 2 | Per-game display and audio device, with crash-safe restore | **Done** (2026-10-23, 1.7.1-beta.2), in one Display and sound card in Edit Game: resolution and refresh rate, monitor choice, Identify, make a monitor the main display, keep the mouse on the game's screen, dim the other screens, and the sound output device; each put back after the game and after a crash. Tested live on one monitor (resolution and refresh rate, the mouse hold, the sound switch, the overlays, the monitor list and a no-change layout call). **Not tried on two monitors:** changing the main display, the mouse hold across screens, dimming another screen. To try with a second monitor (Preston's) |
| 3 | Game recipes (export and import a game's setup) | **Skipped** (2026-10-23) |
| 4 | Companion tool presets, such as Lossless Scaling | **Done** (2026-10-23): companion tools already start with a game and close with it or after it; a Lossless Scaling preset would only save picking the .exe, so it is not built |
| 5 | Save backup through Ludusavi | **Skipped** (2026-10-23): saves stay out of TrayTrigger, as decided before |
| 6 | Back up and restore TrayTrigger's own data | **Done** (2026-10-23): already built (Settings > Diagnostics & Storage). Reviewed for gaps; Activity & History is an option when backing up (a tick, on by default), and automatic backups are an opt-in (weekly, keep 4, in the data folder's Backups\Automatic; the uninstaller keeps them unless asked), and a test makes every new data file be either backed up or listed as staying behind |
| 7 | "Copy diagnostics" button | **Done** (2026-10-23): already built (About > Copy Diagnostic Info); reviewed and updated: user folder removed from paths, appearance, profile and per-game settings, recent changes, monitor count and refresh rate |
| 8 | Discord Rich Presence | **Closed, not built** (2026-10-23): nobody asked for it, and the benefit is thin |
| 9 | Dock and handheld scenes | **Skipped** (2026-10-23) |
| 10 | Library tiering | **Closed, not built** (2026-10-23): few players ask for it, and telling an SSD from a hard drive reliably could not be checked |

## Notes on each

1. **Activity gaps.** Saving Edit Game records one Change entry with each old and new value; removing a
   game, removing a launcher's games with its integration, and Undo are recorded. Favorites, categories
   and pictures are left out as noise. Launch arguments show in plain text in the entry's detail.
2. **Display and sound.** Built. Edit Game's Display and sound card holds the monitor (remembered by its own identity), its resolution and refresh rate, Identify, making it the main display (the layout is changed in Windows' own setting, so a note is kept and it is put back after the game, on exit and at the next start), keeping the mouse on the game's screen, dimming the other screens (a click-through layer owned by TrayTrigger) and the sound output (the voice-chat job is left alone). The part that couldn't be tested here, the multi-monitor layout call, was written to the order Windows asks for and reviewed, and is the first thing to try with a second monitor.
3. **Game recipes.** Skipped: a game holds few settings of its own, since most tweaking lives in the
   Optimized and Aggressive profiles, so a recipe would mostly say "Aggressive" with no way to know what
   that means on another PC. Sharing a profile instead would spread other people's tweak choices, which
   the proven-benefits rule makes unwelcome.
4. **Companion tool presets.** Companion tools already start with a game and close with it or after it.
   Lossless Scaling's per-game profiles and scale hotkey live inside that app, so TrayTrigger has nothing
   more to hand over.
5. **Ludusavi.** If wanted, call Ludusavi and do not write a save engine of our own.
6. **Own-data backup.** Already exists: one .zip of the library, settings, tools, scripts, themes and art,
   with a safety backup before each restore. Activity & History is a tick beside Back Up. Not in it, on purpose: the details caches
   and what describes this PC (a running session, suspended games, DLSS records).
7. **Diagnostics.** Version, Windows and GPU details, the recent log and recent Activity entries, with
   paths and keys removed, so a tester can send a real report.
8. **Rich Presence.** Small and local; Playnite's most-loved add-on.
9. **Dock and handheld scenes.** When an external display or the charger is connected, switch display,
   refresh rate, audio device and power plan, and put them back. TDP stays out: it needs a kernel driver.
10. **Library tiering.** Says which unplayed games to move off the SSD and opens the launcher's own move
    option.

## Done in 1.7.1-beta.5 (2026-10-25)

Not roadmap items: a review of the Edit Game code, started from the owner's report that Convert to Local game on a Battle.net game offered no way back and showed the Steam option. One commit.

- **Convert to Local game can be undone before Save.** A note with an Undo button replaces the platform box; Undo puts the link and the Steam flag back and leaves the dialog unchanged. After Save a scan does not offer the platform's record again (the exe is already in the library); the Help page and code comment said it might.
- **Save kept a Steam tick wrongly dropped.** Ticking "Launch through the Steam client" after converting was thrown away by Save; it is kept now.
- **Right name in the messages.** Converting a Steam game said it was linked to "Local" (status line, log, Activity); the name is taken before the Steam flag is cleared.
- **No convert without an executable.** A launcher game with no exe on record is refused until one is chosen. Convert is not offered for Xbox / Game Pass (the GDK exe cannot run on its own).
- **Steam badge option for Local games only.** It is cosmetic (for screenshots); a launcher game that already has it ticked keeps the box so it can be turned off.
- **A refused Save leaves a blank title as typed**; the dead `HasPlatform` setter is gone.
- **DLSS card.** Read in a second pass over Edit Game. The switch was disabled (`CanEnable`) for a game whose DLSS a patch removed, though the card stayed open "so Restore can be reached" and has no Restore button: it is now `CanToggleOverride`. A Restore or preset change that failed before reading the driver (`DlssOperationResult.Failure` carries no records) dropped every record and saved that, leaving driver values nothing could undo: `StillOwned` keeps them. A click that did nothing now resets the tick.
- **Outside review (Gemini), each claim checked against the code.** Fixed: the window-wait poller on a `Win32Exception`; `BoundedProcess` output drain bounded; a declined prompt at game start no longer leaves the scheduler values marked captured (`ForgetUnwrittenSchedulerCaptures`, only for what this launch captured, not for `Unfinished`); the exit prompt is Put back now / Close anyway / Stay open, with Esc on Stay open; tray search Down after no match, and the box for tools alone; Reset keeps the backup, session-count and driver-notice records; log rotation re-checks the size under the mutex; the installer is not run without a verified hash; Xbox ID checked before explorer's command line, Steam ids before `steam://`; CI also builds Debug; the script-launched game's handle and the PDH query closed. Not changed, with reasons: the Performance Tweaks header split and `App.Screenshot.cs` in Release are deliberate; the DLSS poller already stops on a disposed process; `WTD_CACHE_ONLY_URL_RETRIEVAL` is a choice (releases are unsigned, integrity is the signed SHA256SUMS); `SemanticVersion` hash and sign quirks, `UpdateDialog`'s 400 ms, the unused hotkey fallback, shutdown-thread and `Dispatcher.Invoke` claims are too small or unshown. Not checked: Help opening frozen behind a modal dialog (needs a manual try), `OpenGameDetails` saving, null JSON collections. No test for the exit prompt or the tray Down key (UI); the poller change has none (needs a process that refuses `HasExited`).
- **Read and found no defect:** the Identity, Performance, Display and Scripts XAML (bindings, enable rules). Left as small and unconfirmed: a typed name is overwritten when browsing an exe for a new game, Look Up's Undo after a mid-lookup edit, Cancel/X without a prompt, Save during a lookup.
- Tests in `GameEditConvertToLocalTests`; Help (`library/launch`) and CHANGELOG updated. Not run on screen: the new Undo note.

## Done in 1.7.1-beta.4 (2026-10-24)

Not roadmap items: a code-tidying pass with no change in what TrayTrigger does. One commit.

- **Unused code removed.** Sixteen unused members, parameters and assignments, found by name search and the compiler's unused-code checks.
- **Big files split.** `PerformanceProfileService`, `ProcessLauncherService`, `ImportCoordinator` and `App` are partial classes by area
  (NVIDIA, admin put-back, Steam, Battle.net, exit tracking, drop import, ignored games, the tray, and so on). The seven launcher imports share one
  method; startup is three named steps. Guard tests now name `App.Tray.cs`.
- **View models split.** `SettingsViewModel`, `GameEditViewModel` and `LibraryViewModel` are partial classes by area (scan locations, scripts, display and hardware, launch, removal, DLSS, categories and so on). Still one class each, so no binding changes.
- **Two more repeats gone.** The seven launcher "ignore this game" methods share one; the "process has already exited" checks (id, name, main window, start time) share a small `SafeProcess` helper.
- **Registry reads.** A small `RegistryValues` helper replaces the repeated open, read and catch blocks in 14 tweak checks and two other reads.

## Done in 1.7.1-beta.3 (2026-10-24)

Not roadmap items: an outside review of the code (Gemini; each claim checked against the code, some wrong or already fixed), the owner's
testing, and what that turned up. One commit.

- **From the review.** Steam games no longer lose their Steam flag at startup; Play pressed twice launches once; crash recovery accepts GPU
  values that carry Auto HDR; Intel APO on 14th-gen K names; the mute record is kept; Scan Locations expand a library root; a damaged
  settings.json no longer lets the next save overwrite the backup; store matching ignores a year in brackets; powercfg and sc.exe calls time
  out (`BoundedProcess`); Help follows a theme change; the Resizable BAR read retries on a sleeping GPU; the Visual effects tweak restores the
  Performance Options marker; the CI skips only unembedded markdown and the release runs the tests. Declined: COM `[PreserveSig]` (works, risky
  to touch), the edit-dialog close guard, the shared cursor backend.
- **NVIDIA driver notice.** The newest driver Check for Newer Driver found is saved in settings and shown again after a restart (nothing
  is asked of NVIDIA without a press); one Activity entry per version; a Needs attention item with Get Driver, naming the NVIDIA app;
  it clears once that driver is installed. No automatic check: it would break the "only when you press" promise in the README and SECURITY.
- **Browse buttons** open at the path in the box beside them (`DialogStartLocation`), or at the nearest folder above it that exists.
- **Elevated games and scripts** (from Call of Duty, launched by a .bat that elevates itself, and a second game started by a plain .bat).
  Found in the log: TrayTrigger found the real game but, not elevated, could not subscribe to its exit, and ended the session at once. Now it
  polls whether the process is still running (`TryWatchByPolling`). For a .bat or .cmd that stays alive, priority, power throttling, CPU cores,
  the DLSS check and the window go to the game found under the install folder, not to `cmd` (`StartRealGameDiscovery`). Running TrayTrigger
  itself elevated would work for the process settings but was rejected: it changes the trust model. **Open, by choice:** GPU Preference,
  Auto HDR and a Defender exclusion are keyed to the file path and are still set for the script, not the game; Windows reads GPU preference at
  process start, so the real exe would have to be remembered from the last run (option A, not built). Help says so.
- **Activity & History row menu**: Copy details, Copy this line, Show only this game, Open this game, Open the log, Remove this entry. Remove
  takes exactly the entries the row shows; a Critical row is kept only while an item with its key is under Needs attention.
- **Tests.** The collection that points `ActivityService.Current` at its own service now runs alone; the intermittent Resizable BAR test
  failures were another class recording to Activity at the same moment.

## Checked on the owner's PC (2026-10-29)

Before the 1.7.5 release the owner confirmed, on real use: the layout at 130% text and `tools\Audit-Layout.ps1`, the PLAYING tag at large
text sizes, the Activity row menu, the NVIDIA driver notice across a driver install, a game whose anti-cheat hides its process, two monitors,
and from the last beta the Convert to Local Undo note, the DLSS switch, the exit prompt (Esc, Close anyway, Put back now), tray search Down, the
Enable scripts checkbox and the already-running Play message. A Help window opened from inside Edit Game works normally (the review's claim that
it opens frozen was wrong).

Not shown by any test: whether a game whose own NVIDIA profile has no Resizable BAR value picks up the Global one (open item 4 below), which is why
the release notes say the setting "applies", not that it speeds a game up.

## Open: Resizable BAR for undecided games (found 2026-10-24)

The Aggressive tweak writes one value, `0x000F00BA = 1`, on the driver's **Global** profile. On the owner's PC the driver refused
the write (`NVAPI_INVALID_USER_PRIVILEGE`, -137, from SetSetting) and the step was skipped, so the System page showed ENABLED while
nothing was set.

Read with the debug capture `--test-rebar <out.txt>` (reads only, not elevated):

- NVIDIA's own approved profiles (Cyberpunk 2077, Red Dead 2, Horizon Zero Dawn, Gears 5, AC Valhalla, Battlefield V, Borderlands 3,
  Hitman 3, Forza Horizon 4) set only `0x000F00BA = 1`. The rejected ones (Hogwarts Legacy, Alan Wake 2, FFXVI) set it to 0.
  `0x000F00BB` and `0x000F00FF` are absent on all of them except Red Dead 2 (`BB = 1`). So one value is what NVIDIA itself uses; the
  three-value guides are about forcing it with extra options, and our single value is probably the right one.
- The Global profile holds none of the three. The driver does not know their names (they are hidden settings), though profiles read fine.
- Reading needs no elevation. Writing this hidden setting was refused without it. The other NVIDIA writes (power mode, frame cap)
  gave no warning today.

Write test (`--test-rebar-write`, 2026-10-24, driver value put back afterwards and checked):

- Not elevated: `SetSetting 0x000F00BA = 1` on the Global profile is **refused** (`NVAPI_INVALID_USER_PRIVILEGE`, -137), before any save.
- Elevated: SetSetting ok, read back 1, Save ok, put back ok, Save ok. So **this write needs administrator rights.**

Built 2026-10-24 (option 2): `ElevatedHelper`. TrayTrigger runs itself elevated (`--elevated-apply`) for the NVIDIA change and the registry
import together, so it is still one prompt each way. It accepts only the settings and values in `NvidiaAdminOp.Allowed` (Resizable BAR, 0/1,
delete, restore default) and a .reg file TrayTrigger made in the temp folder. Tested end to end with real UAC runs (`--test-elevated`): set,
read back, removed, driver left as it was. A future setting that needs administrator rights is a new entry in `Allowed` plus its own checks.
Code review (max) of the helper, 2026-10-24: the helper now reads the .reg file once and checks every line against the two Windows scheduler values
(`TryReadRegistryChanges`), applying what it read, instead of trusting the file's name and place; a step that started and was still running when
the wait ended keeps its record (`Unfinished`); the exit code's meaning is one tested function (`Interpret`). Known and left: the helper is
TrayTrigger.exe itself, which sits in a folder the user can write to and is not code-signed, so a process that can replace it gets administrator
rights at the next prompt; signing the release, or installing under Program Files, is what would close that.

Still to settle:

1. (Answered above: it needs admin.)
2. (Done, see above.) It needs admin: mark it ADMIN and write it in an elevated step (the bundled UAC runs PowerShell, not NVAPI, so it is a new
   elevated NVAPI step), or take the tweak out.
3. ~~Make a failed write visible~~ Done: a Problem entry in Activity & History. The ENABLED badge still says nothing about whether it took effect.
4. Does a Global value reach a game whose own profile lacks it (Hellraiser's profile has 16 settings and no value)? Only a game
   running can show that.
5. Done 2026-10-24: a put-back that needs administrator rights and is declined (found in UAT) no longer prompts by surprise.
   The scheduler values are kept on record with Resizable BAR (they were dropped, and stayed at 10/High); start and exit leave
   them waiting and ask first in a TrayTrigger dialog (`App.PutBack.cs`, `PerformanceProfileService.CheckPutBacks`), "Put back
   now" or Restore Previous doing the one combined prompt. Exit with a game running says the prompt is coming. A start in the
   tray shows a toast and asks when the window opens. Not covered: audio and display records (they need no prompt).
   Code review of this (max): what was left waiting is "leftover only" until a session applies an ADMIN tweak of its own (an Optimized
   game ending doesn't prompt for it); later restores settle every entry that offered it (scheduler, Defender, not only NVIDIA); a
   session over a waiting put-back keeps the original it recorded; Restore Previous retries notes nothing twice. Left as is: the exit
   put-back runs on the UI thread while the prompt waits, (Esc on the "Some settings are still changed" dialog closed TrayTrigger then; in 1.7.5 it stays open: Put back now, Close anyway or Stay open).
