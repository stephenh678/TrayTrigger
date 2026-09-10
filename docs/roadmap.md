# Roadmap / Ideas Backlog

Notes on things discussed but not yet implemented, kept here so they survive between sessions.

## Folder-import launcher detection (non-Steam first)

When a game is added via "Add Folder", drag-and-drop, or "Batch Add Games from Folder", it always
goes through the generic exe-heuristic scanner (`FolderScannerService`) and gets tagged as a
"Local" game - even if the folder is actually inside a real GOG/EA/Epic/Ubisoft install directory.
"Scan for Games" doesn't have this problem, since it already discovers those platforms' games via
their own registry/manifest records, independent of any folder path.

Idea: before running the generic folder scan, check the dropped/browsed path against each of the
four scanners' own known install directories (GOG/Ubisoft via registry `path`/`InstallDir` values,
Epic via its manifest files' `InstallLocation`, EA indirectly via its manifest search) - regardless
of whether that platform's integration toggle is currently on, since recognizing what something
*is* shouldn't depend on whether auto-scanning for it is enabled. On a match, skip the generic
heuristic and import using that platform's real record (ID, name, exe, official art) instead of a
guessed name and extracted exe icon.

Open questions to resolve before building:

- **Mixed parent folders.** A dropped folder can contain a mix of matched and unmatched
  subfolders (e.g. one GOG game plus three genuinely local ones) - the check has to run
  per-subfolder during the batch scan, not once for the whole drop.
- **EA's default-install-root limitation carries over.** `EaScannerService` can only resolve
  installs under EA's default install roots (see its class doc comment - no clean registry→path
  mapping exists). A custom-location EA install won't match even under this new check, so it'll
  still land as Local for EA specifically. Not a new problem, just inherited.
- **Silent vs. confirmed.** Leaning toward silently tagging the game correctly with no extra
  prompt, but worth confirming that's the desired UX.
- **Scope across entry points.** Should apply to Add Folder, drag-and-drop of a folder, and
  Batch Add Games from Folder consistently. A single dragged-in .exe/.lnk should probably get the
  same treatment for consistency, since the exe path is the same signal either way.

Touches: `ViewModels/ImportCoordinator.cs`, `Services/FolderScannerService.cs`.

## Steam-specific exe-path dedup gap

`ImportCoordinator.ScanForGamesCoreAsync`'s Steam task only dedupes by Steam App ID (the
`steamTask` local function) - unlike GOG/EA/Epic/Ubisoft, it has no exe-path fallback check
against `existingExePaths`. If a Steam game was added manually (Add Folder/drag-drop) before ever
being properly Steam-scanned, it has no `SteamAppId`, so a later "Scan for Games" will offer it
again as "new" - selecting it creates a duplicate library entry (two rows pointing at the same
exe: one Local, one properly Steam-tagged).

Fix: add the same `(g.ExePath == null || !existingExePaths.Contains(g.ExePath))` condition already
used by the other four platforms' tasks to Steam's task.

Related to the folder-import detection idea above - fixing the folder-import side (recognizing a
Steam install directory up front) would prevent this scenario from arising in the first place, but
the missing Steam-side dedup is worth fixing independently regardless, as a safety net for any
other route a Steam game's exe could end up in the library without a SteamAppId.
