# Roadmap / Ideas Backlog

Notes on things discussed but not yet implemented, kept here so they survive between sessions.

## Open

### Community script catalog (design only, 2026-09-11)

A curated catalog of game scripts that advanced users contribute and install from inside
TrayTrigger. Decided against any in-app upload: a script runs with the installing user's full
privileges (and the card offers an Administrator option), so distributing strangers' uploads
without review is not acceptable, and upload needs a backend with auth, abuse handling and
takedowns. A GitHub repository with pull-request review gives provenance, review and hosting for
free. **Review is the security control; hashes only prove the file is what the reviewer approved.**

**Catalog repository** (separate from the app, e.g. `stephenh678/TrayTrigger-Scripts`):

- One folder per script: the script file(s) plus a `script.json` with `name`, `description`,
  `author`, `phase` (`prelaunch`, `postexit`, `both`), `needsAdmin` (bool, must be justified in
  the PR), `scriptArguments` (what the script expects in Script Arguments), `version`, and
  `dependencies` (free text: "OpenRGB installed", "AudioDeviceCmdlets module").
- A GitHub Action regenerates `catalog.json` on merge to `main`: every entry above plus a
  SHA-256 per file. Contributors never hand-write hashes and cannot forge them.
- `CONTRIBUTING.md` with the PR checklist: uses only the documented positional arguments and
  Script Arguments (see `Scripts/Library/README.txt` in this repo for the contract); no
  downloads, no network calls, no `Invoke-Expression`, no module installs; nothing destructive
  uncommented (closing only processes the user named or the script itself started); `needsAdmin` absent or
  justified; tested with Test Run in both phases; comments explain every action. The repo owner
  reviews every PR.
- The first entries are this repo's bundled examples, in the same format, so the manifest code
  is written once. The audio-device switcher that was cut from 1.4.0 (needs the AudioDeviceCmdlets
  module) is the first catalog-only candidate, because the catalog can declare the dependency.

**In the app** (1.4.x):

- Browse from Settings › Launch & Performance (a "Community scripts..." button) and from the
  scripts card in Edit Game. Fetch `catalog.json` from the repo's raw `main` (or a release asset),
  reusing `UpdateService`'s HttpClient, timeout and `FindExpectedSha256` / `ComputeSha256Async`
  patterns (`Services/UpdateService.cs`).
- Every entry shows name, author, phase, needs-admin, dependencies and the **full source** before
  install. Install downloads into `%AppData%\TrayTrigger\Scripts\Community\<name>\`, verifies
  every file's SHA-256 against the catalog, refuses on mismatch, and records the installed hash
  and version in a small `installed.json`.
- Never auto-updated. The browser shows "newer version in catalog" and "modified locally" (installed
  hash vs file hash) and lets the user re-install explicitly, which overwrites only with consent.
- A trust banner on the browser: community scripts run as you; read them before installing. Same
  text in `SECURITY.md`.
- "Share your script" opens the catalog repo's contributing guide in the browser. No upload code.
- Disclose the new network call (one GET of the catalog, plus one per installed file, only on
  user action) in `SECURITY.md` and the README's privacy/network section, next to the existing
  update-check disclosure.
- Ratings, comments and download counts stay on GitHub (stars, issues). Nothing in-app until the
  catalog is large enough to need it.

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
  Apps, OBS replay buffer and Save Backup, plus the two blank templates and a task-first README
  (`Scripts/Library/`, `Services/ScriptLibraryService.cs`). The breakage risk is handled inside
  the scripts: each is presented as an example to copy, declares its dependencies in a
  catalog-style header, finds the third-party app at run time or through one setting at the top,
  and exits 0 with a plain message when the app isn't there. All are PowerShell except the batch
  template. They are the seed entries for the community catalog. An audio-device switcher and a
  display-mode changer were considered and left for the catalog: both need a large inline C#
  block that is hard to learn from.
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
