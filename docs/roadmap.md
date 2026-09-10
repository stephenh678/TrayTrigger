# Roadmap / Ideas Backlog

Notes on things discussed but not yet implemented, kept here so they survive between sessions.

_(Nothing open right now.)_

## Shipped

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
