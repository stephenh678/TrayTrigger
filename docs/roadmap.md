# Roadmap

Written 2026-10-23, replacing the earlier roadmaps. Ten items, in the order to work them, from a look at
Playnite, GOG Galaxy, LaunchBox, Heroic, AutoActions, Display Changer, Process Lasso, Razer Cortex,
Lossless Scaling and Ludusavi. The competitor claims are web-search summaries, not first-hand reads; check
one before relying on it. Nothing here is scheduled until the owner says so.

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
| 1 | Activity gaps: Edit Game changes, game removal and undo | **Done** (1.7.1-beta.2, built and tested, not yet tried in the app) |
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

## To check at home

The 1.7.1 work has not been run on real games: the layout at 130% text on the Stutter tab, `tools\Audit-Layout.ps1`
with TrayTrigger closed, the PLAYING tag on poster cards at large text sizes, and the new Edit Game and
removal entries in Activity.

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
