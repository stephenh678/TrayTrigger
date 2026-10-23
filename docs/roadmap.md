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
| 2 | Per-game display and audio device, with crash-safe restore | Not started. Needs a second monitor to test (Preston's) |
| 3 | Game recipes (export and import a game's setup) | **Skipped** (2026-10-23) |
| 4 | Companion tool presets, such as Lossless Scaling | **Done** (2026-10-23): companion tools already start with a game and close with it or after it; a Lossless Scaling preset would only save picking the .exe, so it is not built |
| 5 | Save backup through Ludusavi | **Skipped** (2026-10-23): saves stay out of TrayTrigger, as decided before |
| 6 | Back up and restore TrayTrigger's own data | **Done** (2026-10-23): already built (Settings > Diagnostics & Storage). Reviewed for gaps; Activity & History stays behind on purpose and Help now says so, and a test makes every new data file be either backed up or listed as staying behind |
| 7 | "Copy diagnostics" button | **Done** (2026-10-23): already built (About > Copy Diagnostic Info); reviewed and updated: user folder removed from paths, appearance, profile and per-game settings, recent changes, monitor count and refresh rate |
| 8 | Discord Rich Presence | **Closed, not built** (2026-10-23): nobody asked for it, and the benefit is thin |
| 9 | Dock and handheld scenes | **Skipped** (2026-10-23) |
| 10 | Library tiering | **Closed, not built** (2026-10-23): few players ask for it, and telling an SSD from a hard drive reliably could not be checked |

## Notes on each

1. **Activity gaps.** Saving Edit Game records one Change entry with each old and new value; removing a
   game, removing a launcher's games with its integration, and Undo are recorded. Favorites, categories
   and pictures are left out as noise. Launch arguments show in plain text in the entry's detail.
2. **Display and audio.** The most-asked feature in competing tools (AutoActions, Display Changer and
   LaunchBox's feedback board). TrayTrigger's strength is the put-back guarantee. Resolution, refresh rate,
   monitor and default audio device per game, put back when the game ends and after a crash.
3. **Game recipes.** Skipped: a game holds few settings of its own, since most tweaking lives in the
   Optimized and Aggressive profiles, so a recipe would mostly say "Aggressive" with no way to know what
   that means on another PC. Sharing a profile instead would spread other people's tweak choices, which
   the proven-benefits rule makes unwelcome.
4. **Companion tool presets.** Companion tools already start with a game and close with it or after it.
   Lossless Scaling's per-game profiles and scale hotkey live inside that app, so TrayTrigger has nothing
   more to hand over.
5. **Ludusavi.** If wanted, call Ludusavi and do not write a save engine of our own.
6. **Own-data backup.** Already exists: one .zip of the library, settings, tools, scripts, themes and art,
   with a safety backup before each restore. Not in it, on purpose: Activity & History, the details caches
   and what describes this PC (a running session, suspended games, DLSS records).
7. **Diagnostics.** Version, Windows and GPU details, the recent log and recent Activity entries, with
   paths and keys removed, so a tester can send a real report.
8. **Rich Presence.** Small and local; Playnite's most-loved add-on.
9. **Dock and handheld scenes.** When an external display or the charger is connected, switch display,
   refresh rate, audio device and power plan, and put them back. TDP stays out: it needs a kernel driver.
10. **Library tiering.** Says which unplayed games to move off the SSD and opens the launcher's own move
    option.

## To check at home

The 1.7.1 work has not been run on real games: the PC facts card (about ten games, one non-Steam, Refresh
with the network off), the layout at 130% text on the new card and the Stutter tab, `tools\Audit-Layout.ps1`
with TrayTrigger closed, the PLAYING tag on poster cards at large text sizes, and the new Edit Game and
removal entries in Activity.
