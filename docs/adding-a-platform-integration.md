# Adding a game-platform integration

Playbook for wiring up a new launcher platform (Steam, GOG, EA App, Epic Games Store, Ubisoft
Connect, and eventually Xbox) into "Scan for Games". Written after implementing GOG (the second
platform after Steam, and the first one to follow this pattern deliberately), then updated after
EA (the third), Epic (the fourth), and Ubisoft (the fifth), which together confirmed which parts
of the pattern actually generalize and which parts are genuinely platform-specific. Steam, GOG,
EA, Epic, and Ubisoft are all good reference implementations to read alongside this doc:

- `Services/SteamScannerService.cs` / `Services/GogScannerService.cs` / `Services/EaScannerService.cs` / `Services/EpicScannerService.cs` / `Services/UbisoftScannerService.cs` — discovery
- `Services/UrlProtocolHelper.cs` — the shared "is this platform's client installed" check (see
  step 0.6) - EA, Epic, and Ubisoft all call this rather than each having their own copy
- `Services/ProcessLauncherService.cs` — launching + session tracking (`TrackInstallDirSession` is
  the shared GOG/EA/Epic/Ubisoft tracking core — see step 1). Since the 1.3.6 launcher rework the
  *decision* of which path a game takes lives in `Services/LaunchRouting.cs` (`LaunchRouter.Resolve`,
  pure and unit-tested in `LaunchRoutingTests`), process identity lookups live in
  `Services/ProcessPathResolver.cs`, and every launch is an `ActiveGameSession` that ends through
  one `FinishSession` - see "1.3.6 launcher rework" at the end of this doc for the name changes.
- `ViewModels/ImportCoordinator.cs` — scan/import pipeline
- `ViewModels/LibraryViewModel.cs` (`EnrichGameWithSteamMetadataAsync`) / `Services/GameNameExtractor.cs`
  (`ResolveGameMatchAsync`'s `knownName` param) — Steam title/genre/cover-art matching for imported
  entries, including non-Steam platforms (see step 0.7)
- `ViewModels/ScanForGamesViewModel.cs`, `Views/ScanForGamesDialog.xaml(.cs)` — picker UI
- `MainWindow.xaml` — Settings toggle + per-card platform badge (search for `IsGogGame`/`IsEaGame`/`IsEpicGame`/`IsUbisoftGame`/`HasSteamOverlay`)

## 0. Research first, don't assume

Every platform's on-disk/registry conventions and launch mechanism are different, and usually
**undocumented officially** — Steam's `steam://` URLs and GOG's registry layout are both
community-reverse-engineered, not published APIs. Before writing code:

1. **Search for how existing open-source tools handle it.** Playnite (multi-platform, most
   actively maintained) is the best reference; for GOG, `tkashkin/GOGWrapper`'s source on GitHub
   gave exact registry key names and the `GalaxyClient.exe` launch command. For EA App, look at
   Playnite's EA App library plugin source the same way.
2. **Verify against a real installed copy of the platform if at all possible.** For GOG, a
   read-only PowerShell registry probe against the user's actual GOG Galaxy install (see the
   pattern below) caught a real detail the community docs didn't fully explain: DLC entries share
   the `Games` registry key with real games and have to be filtered out via `dependsOn`. Don't
   skip this step even when a community source seems clear — real data disagrees with docs often
   enough to be worth the five minutes.
   ```powershell
   Get-ChildItem "HKLM:\SOFTWARE\WOW6432Node\<Vendor>\Games" | ForEach-Object {
     Get-ItemProperty -Path $_.PSPath
   }
   ```
3. **Nail down the launch mechanism before writing any import code** — it drives real
   architecture decisions (see step 3 below), not just a follow-up detail.
4. **Don't assume the registry gives you everything, even when a registry key exists — and don't
   assume the worst case (EA) is the norm either. There's a third shape too: an install-location
   pointer with nothing else at all.**
   - GOG's `Games\<id>` registry key has the full install path *and* exe right there.
   - Epic is even better: no registry data needed at all - Epic Games Launcher writes a complete,
     structured JSON manifest per installed game (`*.item` files under
     `C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests\`, with `InstallLocation`,
     `LaunchExecutable`, `AppName`, and `DisplayName` all present directly - `JsonDocument.Parse`
     and read the fields, no path-hunting at all).
   - EA's `Origin Games\<id>` key has only a content ID and display name - no path at all. The
     practical fallback (used by Playnite, confirmed by EA's own on-disk data) is: search a small
     set of default install roots for the platform's own install manifest file, matched by an ID
     embedded in that manifest (EA: `__Installer\installerdata.xml`, containing `<contentID>`),
     and read the real exe path from there. **This has a real, permanent consequence: a game
     installed to a custom (non-default) folder won't be found.** This isn't a bug to fix later -
     every other third-party integration doing this (Playnite, Lutris) hits the same wall, because
     the platform's own local cache is usually opaque/proprietary (EA App's
     `%PROGRAMDATA%\EA Desktop\` data is encrypted binary blobs, not a readable manifest). Surface
     this limitation explicitly in the Settings toggle's own description, not just in this doc -
     see EA's toggle text in `MainWindow.xaml` for the wording used. If a manifest embeds its own
     path as a registry-indirection string (EA:
     `[HKEY_LOCAL_MACHINE\SOFTWARE\<Publisher>\<Game>\Install Dir]relative\path.exe`, where the key
     name varies per game/publisher, not a consistent platform-wide key), don't bother resolving
     that indirection - the manifest file's own location on disk already tells you the real
     install directory, and the bracketed part can just be stripped and ignored.
   - Ubisoft's `Launcher\Installs\<id>` key is thinner still: only `InstallDir` (using forward
     slashes - normalize before any `Path`/`Directory` call) and `Language`. No name, no exe, and
     no readable local manifest either - Ubisoft's own `uplay_install.manifest` file (present in
     every install folder) is a compressed/encrypted binary blob, same dead end as trying to parse
     EA's local cache. Unlike EA, though, this isn't a permanent-limitation case: since the
     registry *does* give a real install path (any location, not just default roots), the game is
     always findable - only its name and exe need discovering. **Don't reinvent exe-picking
     heuristics for this.** `FolderScannerService.ScanFolder(installDir)` - the same scored
     candidate search "Add Folder" already uses (uninstaller/crash-handler/anti-cheat filtering,
     confidence scoring, a sensible display name derived from the folder) - does exactly this job
     already; call it and take the top-scored `GameCandidate`. `UbisoftScannerService` has no
     scanning logic of its own at all, just registry enumeration plus this reuse.
5. **When a platform's discoverability is meaningfully worse than the ones already implemented,
   raise that with the user explicitly before building it** - not just "here's how it'll work" but
   "here's what won't be found, is it worth building anyway given your actual user base." This is a
   real product decision (support-burden vs. feature value for a solo-maintained project), not
   something to decide unilaterally mid-implementation.
6. **The specific "client launch" mechanism varies per platform even among ones that support it.**
   GOG has no public URL scheme - it needs a raw command-line invocation of `GalaxyClient.exe`. EA
   (`origin2://game/launch/?offerIds=<id>`), Epic
   (`com.epicgames.launcher://apps/<AppName>?action=launch&silent=true`), and Ubisoft
   (`uplay://launch/<gameId>/0`) all turned out to have a real registered URL protocol -
   architecturally closer to Steam's `steam://` than to GOG's approach. Checking whether the
   client is installed (and finding its exe) is the exact same check every time -
   `Services/UrlProtocolHelper.cs` centralizes it (`HKEY_CLASSES_ROOT\<scheme>\shell\open\command`,
   parse the quoted exe path, verify it exists); call `UrlProtocolHelper.IsHandlerInstalled(scheme)`
   rather than writing another copy - EA and Epic each had their own near-identical private method
   for this until Ubisoft made it a third copy and it got extracted. Still verify which launch
   shape (URL protocol vs. raw command line) a new platform actually uses - don't assume it matches
   the last platform you implemented.
7. **Pass the platform's own display name into Steam title-matching - don't let it get silently
   discarded.** Every discovered-game record carries an authoritative `Name` straight from the
   platform's own metadata (GOG/EA/Epic manifests, Steam's own listing). But
   `LibraryViewModel.EnrichGameWithSteamMetadataAsync` (used to find a `SteamAppId` for cover
   art/genre, even for a non-Steam-launched game) used to search only with a name *guessed* from
   the exe filename or install folder via `GameNameExtractor.ResolveGameMatchAsync` - e.g. Epic's
   "Orcs Must Die! 3" produced the exe `OMD.exe` and folder `OrcsMustDie3`, neither of which
   reliably matched the real Steam listing, even though the game is genuinely on Steam. Fixed by
   adding a `knownName` parameter to `ResolveGameMatchAsync` (a "Pass 0" tried before the exe/folder
   guesses) and passing `entry.Name` into it from `EnrichGameWithSteamMetadataAsync`. Any future
   platform's imported entries flow through this same method, so this fix already covers them - just
   don't reintroduce a path that creates a `GameEntry` and skips straight to enrichment without
   `Name` already holding the platform's real title.

## 1. Decide the launch mechanism explicitly with the user

This was the one place the GOG work forked into real complexity, and it's worth surfacing as an
explicit question rather than assuming:

- **Direct exe launch** — simplest, reuses 100% of `ProcessLauncherService`'s existing "normal
  executable" branch (Process.Start, Exited event, playtime, Performance Profile, scripts). Costs
  nothing new to build. Loses whatever the platform client would have provided (cloud saves,
  achievement sync, overlay) if the game is normally launched through that client.
- **Launch through the platform's own client** — preserves those features, but needs:
  - A way to find/confirm the client is installed (a URL protocol handler registry key, or the
    client's own executable path via another registry read).
  - The launch invocation - either a documented/reverse-engineered URL protocol (EA:
    `origin2://game/launch/?offerIds=<id>`, launched exactly like Steam's `steam://` via
    `Process.Start` with `UseShellExecute = true`) or an undocumented direct command-line
    invocation of the client exe (GOG: `GalaxyClient.exe /command=runGame /gameId=<id>
    /path=<installDir>` - each flag MUST be a single `/name=value` token via `ArgumentList`, not
    `/name` and `value` as separate entries; Galaxy silently ignores args it doesn't recognize
    instead of erroring, so a malformed invocation just opens the client's normal UI with no error
    at all - caught only by actually launching a real game and confirming it starts unattended).
    Even with the syntax right, GOG's command line only starts the game silently on a **cold
    start** - confirmed on GOG's own forums - if Galaxy is already running (the common case once a
    user has logged in once), the identical command line just brings Galaxy to the game's page
    with a Play button, no error, no different log output, nothing to catch except playing the
    game yourself while Galaxy is already open in the background. The fix: check whether the
    client process is already running (`Process.GetProcessesByName`) before dispatching the
    command line, and fall through to a direct exe launch when it is - the client running in the
    background is enough for its own achievement/cloud-save hooks regardless of what actually
    started the game process. Worth checking whether EA/Epic/Ubisoft's URL-protocol launches have
    the same "already running" gap the next time one is touched - not verified either way yet.
    That direct-exe fallback is its own trap, though: don't route it through the generic
    "normal executable handling" path used for manually-added games. That path tracks the exact
    PID it spawned and assumes a 1:1 process/session relationship - true for a typical exe, false
    for a game whose *registered* exe is a prelauncher stub (Cyberpunk 2077's
    `REDprelauncher.exe` spawns the real game and exits within milliseconds). Routing GOG's direct
    fallback through the generic path did exactly this: the Performance Profile got restored the
    instant the stub exited, before the real game had even started. The fix is the same debounced
    install-dir polling (`TrackInstallDirSession`) the client-CLI path already uses - never the
    single-PID watch - for *any* direct-exe fallback on a platform whose games can have a
    prelauncher stub as their registered exe. Confirmed hit for real, not just theoretical: EA's
    "EA App not found → launch directly" fallback originally routed through the generic path
    (`LaunchGame` fell through to "Normal executable handling" instead of a dedicated
    `LaunchEaGameDirectly`) and a real EA title's registered exe exited within ~250ms of starting,
    restoring the Performance Profile almost instantly - caught via the debug log timestamps, same
    signature as the original GOG bug. Epic's and Ubisoft's direct-exe fallbacks had the identical
    gap (never actually hit, since they were fixed alongside EA's) - every "no client installed"
    fallback needs its own `Launch<Platform>GameDirectly` using `TrackInstallDirSession`, not a
    shared fallthrough to the generic path.
  - Even `TrackInstallDirSession`'s own debounce (wait for the same PID on N consecutive polls
    before committing) can itself be too short: a real Ubisoft title (Tom Clancy's Rainbow Six
    Extraction, registered exe `R6-Extraction_Plus.exe`) turned out to be a *chained* handoff -
    its exe stayed alive past the original 2-poll (~2s) debounce, got confirmed and attached to,
    then exited on its own (~1.85s later) once it handed off to the real game process, restoring
    the Performance Profile almost instantly. Bumped `RequiredStableSightings` from 2 to 3
    (~4s of confirmed stability) to fix this specific case, but the debounce is fundamentally a
    timing guess - a long enough handoff chain (or a slow machine) can still outlast whatever N is
    chosen. If this recurs, the more robust fix is for `TrackInstallDirSession` to resume polling
    (excluding the dead PID) when its attached candidate exits implausibly fast after being
    confirmed, instead of ending the session - not implemented yet since a single N bump resolved
    the one reproduced case.
  - Session tracking, since the client is typically just a launcher stub that exits or keeps
    running as a background helper — the actual game process has to be found separately.
    **This part is already fully generalized and shared - don't re-derive it.** GOG and EA both
    use `TrackInstallDirSession`/`FindRunningProcessUnderDirectory`/`TryGetExecutablePath`/
    `TryActivateRunningProcessUnderDirectory` in `ProcessLauncherService.cs` (poll for a process
    running under the game's known install directory, then hand off to the same
    `AttachExitTracking` every launch path uses). A third client-launch platform should call these
    directly, passing its own `platformLabel` for logging, rather than writing a
    `Track<Platform>Session` copy - GOG's original per-platform copy is exactly what got
    generalized once EA needed the identical thing (see the reuse note at the bottom of this doc).

Ask which one the user wants, and whether the "client launch" path should fall back to direct-exe
when the client isn't installed (this is what GOG and EA both do, and is almost certainly what any
future platform should do too, to still support DRM-free/offline-installed games).

### Pitfalls found in `/code-review high --fix` on the finished GOG client-launch path

A high-effort review of the finished GOG diff (8 finder angles, verified) caught 6 real bugs, all
in the "poll for the real process, then track it" pattern that any client-launch platform will
need. Reference implementation: `TrackInstallDirSession`/`FindRunningProcessUnderDirectory`/
`TryGetExecutablePath` in `ProcessLauncherService.cs` (originally named `TrackGogGalaxySession`
before EA's addition generalized it - check `git log` if you're reading an old copy and the names
don't match). Build the next platform's version on top of these already-fixed helpers rather than
re-deriving the pattern — they exist specifically to avoid re-hitting this list:

1. **Don't commit to the first process match.** If the client is just a launcher stub, or the game
   itself runs a short-lived prelauncher (GOG: `REDprelauncher.exe`, confirmed on a real install),
   a single poll tick can catch the wrong process. Require the same PID on two consecutive polls
   before calling it "found."
2. **Add an "already running" check before dispatching a client launch**, mirroring the one the
   direct-exe path already has — but a name-based check (`Process.GetProcessesByName`) won't work
   if the registered exe is a prelauncher stub; match by install directory instead, the same way
   the polling loop does.
3. **A process running at higher privilege than TrayTrigger (anti-cheat/DRM/elevated games) makes
   `Process.MainModule` throw**, and TrayTrigger runs non-elevated by default. A naive catch-and-skip
   makes that process permanently invisible to matching. Fall back to a WMI query
   (`SELECT ExecutablePath FROM Win32_Process WHERE ProcessId = ...`, already used elsewhere in this
   codebase via `System.Management` — see `SystemInfoService.cs`) rather than silently excluding it.
4. **If tracking is set up from an async `Timer` callback (not from inside `LaunchGame`'s own
   try/catch), a failure in that setup has no rollback path** unless you deliberately put it inside
   a try/catch that calls `EndGameSession` on failure — `LaunchGame`'s outer catch doesn't reach code
   that runs later, on a timer thread, after `LaunchGame` has already returned.
5. **Stamp playtime start time from `process.StartTime`, not `DateTime.Now` at detection time** —
   poll granularity plus any launcher/loading-screen delay before the real process appears otherwise
   undercounts every session's recorded playtime.
6. **Add the same "nothing to track" short-circuit `TrackSteamSession` has** (return immediately if
   `PerformanceProfile == Off` and there's no post-exit script) — otherwise every launch polls
   `Process.GetProcesses()` (a full system enumeration) every 2 seconds for minutes, for no benefit.

## 2. Watch for the ultrareview line-count cap when reviewing a big change like this

If this repo's `/code-review ultra` gets used on the finished platform-integration diff, note it
caps around 8,000 changed lines per run. A brand-new platform integration touching this many files
can exceed that easily — split into logical batches (e.g. "all service-layer code", "all
ViewModel/UI wiring") rather than trying to review it in one pass. See the session history for how
this was worked around for GOG (throwaway branches off an orphan empty-tree commit, each batch
checked out via `git checkout <base-branch> -- <files>`).

## 3. File-by-file checklist

Assuming the "direct exe, with optional client-launch" shape (adjust if the platform is launched
some entirely different way — see the Xbox caveat at the bottom):

| # | File | What changes |
|---|------|---------------|
| 1 | `Services/<Platform>ScannerService.cs` (new) | `Discovered<Platform>Game` record (mirror `DiscoveredGogGame`: Id, Name, InstallDir, ExePath, any extra launch args, IconPath, IsAlreadyImported) + `ScanInstalledGames(existingIds)`. Filter out non-real entries (GOG: DLC; other platforms will have their own equivalents — verify-in-progress installs, demo/trial entries, etc.). If a client-launch path was chosen, also add `Get<Platform>ClientPath()`. |
| 2 | `Models/GameEntry.cs` | `Is<Platform>Game` bool + `<Platform>GameId` string?, mirroring `IsGogGame`/`GogGameId`. |
| 3 | `Models/AppSettings.cs` | `<Platform>IntegrationEnabled` bool, default `true` to match Steam/GOG. |
| 4 | `Models/IgnoredGamePath.cs` | `<Platform>GameId` string?, mirroring `GogGameId`. |
| 4b | `ViewModels/IgnoredGamePathRowViewModel.cs` | **Easy to miss — not referenced from anywhere the compiler would catch.** `DisplayPath` branches on `ExePath`/`SteamAppId`; add the new `<Platform>GameId` as a further fallback, or an ignored entry from this platform silently displays a broken label (e.g. `"Steam AppId "` with nothing after it) in Settings. Missed entirely in the first GOG pass — caught only by a later code review, not by build/test. |
| 5 | `Services/LibraryConstants.cs` | `<Platform>Category` const. |
| 6 | `Services/LaunchRouting.cs` + `Services/ProcessLauncherService.cs` | Add `<Platform>Client` / `<Platform>Direct` values to `LaunchRoute`, a field on `LaunchClientAvailability`, and the branch in `LaunchRouter.Resolve` (add the matrix rows to `LaunchRoutingTests`). In `ProcessLauncherService.LaunchGame`'s switch, a URL-scheme client is one line: `LaunchViaClientUrl(game, route, "<scheme>://...", out errorMessage)`; the direct fallback is `LaunchPlatformExeDirectly(game, route, ...)`. Both already do the already-running check, `BeginSession`, dispatch, and `TrackInstallDirSession` - don't write a new `Launch<Platform>...`/`Track<Platform>Session` method. Add the platform's label to `LaunchRouter.PlatformLabelFor`/`ClientPlatformFor`, and its client process names to `LauncherClientCloser` for "close the launcher after exit". |
| 7 | `ViewModels/ImportCoordinator.cs` | `_<platform>ScannerService` field + constructor param; a branch in `ScanForGamesAsync` (check whether the platform needs a `ScanLocations`-style per-folder toggle like Steam, or is location-free like GOG — **if location-free, remember to fix the `enabledLocations.Count == 0` early-return gate**, or a platform with no folders configured will incorrectly show "No Scan Locations"); filter the scan results against `existingExePaths` (not just the platform's own game-ID set) so a game the user already added manually — before this platform's integration ever matched it — doesn't get offered again as a duplicate; `Import<Platform>Games`/`Import<Platform>GamesAsync`/`Ignore<Platform>Game`; extend `ImportScanResultsAsync`'s signature with the new list. |
| 8 | `ViewModels/ScanForGamesViewModel.cs` | Third (or Nth) source on `ScannedGameItemViewModel`; extend the constructor, `ImportConfirmed` event, `ImportSelected`, `OnItemIgnoreRequested`. |
| 9 | `Views/ScanForGamesDialog.xaml.cs` | Thread the new list through the dialog constructor. |
| 10 | `MainWindow.xaml.cs` | Extend `OnRequestScanResultsPicker`'s signature. |
| 11 | `App.DevDiagnostics.cs` | Fix the `--screenshot-steam` dev-diagnostics call site that constructs `ScanForGamesDialog` directly (easy to miss — it's not part of the main app flow). |
| 12 | `ViewModels/MainViewModel.cs` | `_<platform>ScannerService` field + constructor param; thread through to `Import`; extend `RequestScanResultsPicker`'s event signature and its forwarding line; forward `Import<Platform>Games(Async)`/`Ignore<Platform>Game`. |
| 13 | `App.xaml.cs` | Construct the new scanner service; pass it to `ProcessLauncherService` and `MainViewModel`. |
| 14 | `ViewModels/SettingsViewModel.cs` | `<Platform>IntegrationEnabled` property (mirrors `SteamIntegrationEnabled`/`GogIntegrationEnabled` — no scanner-service dependency needed unless there's a location-sync concept to trigger). |
| 15 | `MainWindow.xaml` | Settings toggle next to Steam's/GOG's; platform badge in all three library views (Poster Grid, Compact Icons, Details List) — search for `IsGogGame`/`HasSteamOverlay` to find every spot. |
| 16 | `ViewModels/GameCardViewModel.cs` | `Is<Platform>Game` passthrough property + `OnPropertyChanged` notification; extend `ShowCategoryBadge` so the platform's own category name (e.g. "GOG") is hidden once it's genre-enriched, same as Steam's. |
| 17 | `ViewModels/LibraryViewModel.cs` | **Don't skip this one.** `EnrichGameWithSteamMetadataAsync` only overwrites `Category` when it's currently `Uncategorized` or `SteamCategory` — a hardcoded allow-list. Add `<Platform>Category` to both checks, or every game from the new platform gets permanently stuck showing its platform name as its category instead of a real genre. This was the actual bug in the first GOG pass — the platform wiring worked, but the category system silently rejected the enrichment because of this allow-list. Already fine for a new platform without further changes: this same method now passes `entry.Name` as a `knownName` search pass (see step 0.7), so as long as your import path sets `entry.Name` to the platform's real title before calling `FinalizeNewEntryAsync`, title/genre matching should work out of the box. |
| 18 | `ViewModels/ImportCoordinator.cs` (`EnrichLibraryAsync`) | Same allow-list problem exists a second time, in the periodic re-enrichment candidate filter — add `<Platform>Category` there too for full symmetry (narrower impact than #17, but still worth it). |
| 19 | `Services/PlatformLookupService.cs` | **The manual-import routes (drop/Add Folder/Batch Add) never see `ScanInstalledGames`.** They resolve a dropped exe/folder to a platform record through `PlatformLookupService.Index.Match`, matching by install-directory containment. Add a branch for the new platform there (a `PlatformMatch.For<Platform>` factory + record property, and a lookup - either `ScanInstalledGames([])` cached on the index if the scan is cheap registry/manifest reads like GOG/Epic/EA, or a per-path `FindGameByPath` on the scanner if the full scan walks the filesystem per game like Steam/Ubisoft). Then extend `ImportCoordinator.PlatformImportBuckets` (`Add`/`Describe`/`IsEmpty`/`Count`) and `ImportPlatformBucketsAsync`. Skip this and a game dragged in from the new platform's install folder lands as a Local exe. |

`Models/AppJsonContext.cs` does **not** need a new entry — `Discovered<Platform>Game` is an
in-memory-only record (never serialized), same as `DiscoveredSteamGame`/`DiscoveredGogGame`.

## 4. Verify

- `dotnet build` and `dotnet test` after the backend wiring, before touching XAML — catches most
  signature-mismatch errors from the many call sites above in one pass.
- Actually run a scan against a real install of the platform if the user has one (this caught the
  registry data details for GOG that no amount of reading source code first would have).

## What actually generalized vs. what's still duplicated per platform

A code review on the GOG diff flagged (but deliberately left unfixed, as an architecture decision
rather than a bug) that several pieces would keep growing one copy per platform. EA was the second
data point, Epic the third, and Ubisoft the fourth:

- **Session tracking generalized on the very next platform and held for the third and fourth.**
  `TrackGogGalaxySession` becoming `TrackInstallDirSession` was a small, low-risk, mechanical
  extraction once EA needed the identical polling/debounce/rollback logic - and both Epic's and
  Ubisoft's launch methods called straight into the existing
  `TrackInstallDirSession`/`TryActivateRunningProcessUnderDirectory` helpers with zero further
  changes needed. Four platforms now share one implementation - this abstraction is proven.
- **The URL-protocol-handler check generalized on the third repetition, not the first.**
  EA and Epic each wrote their own private `GetXHandlerPath`-shaped method (identical logic,
  different scheme string). Rather than let Ubisoft add a third copy, it got extracted into
  `Services/UrlProtocolHelper.cs` as part of the Ubisoft pass. Two copies of something small was
  tolerated; a third copy was the trigger to consolidate - a reasonable threshold to apply to the
  next piece that starts repeating, rather than a hard rule to consolidate everything on sight.
- **The `Category` allow-list (`LibraryViewModel.EnrichGameWithSteamMetadataAsync`,
  `ImportCoordinator.EnrichLibraryAsync`, `GameCardViewModel.ShowCategoryBadge`) and the
  `Import<Platform>GamesAsync`/`Ignore<Platform>Game` per-platform methods still did *not* get
  generalized** - Ubisoft added a fifth hardcoded clause/copy in each spot (Steam, GOG, EA, Epic,
  now Ubisoft), exactly as the original review predicted, three times in a row now. Five real,
  live call sites each is a stronger case than the doc previously made for consolidating this on
  the next platform (Xbox or otherwise) rather than adding a sixth copy - a `Platform` enum /
  single-source-of-truth version, as the original review suggested, is overdue at this point.

## Platforms this pattern *won't* fit cleanly

**Xbox / Microsoft Store** — architecturally different from Steam/GOG/EA. Those are UWP/MSIX
packaged apps, not a traditional installed `.exe` with a registry entry pointing at it. Discovery
would go through `Windows.Management.Deployment.PackageManager`/`Get-AppxPackage`, and launching
uses shell activation (`shell:AppsFolder\<PackageFamilyName>!<AppId>`), not `Process.Start` on a
file path. Don't assume this checklist applies as-is — treat it as its own research pass (step 0
above) before deciding how much of the pattern actually transfers.

## 1.3.6 launcher rework (2026-09-10) - name changes for readers of the sections above

The per-platform history above is still accurate about *why* things are shaped the way they are;
only the names moved. Map old to new:

- `LaunchGame`'s chain of `if (game.IsXGame ...)` branches → `LaunchRouter.Resolve(game, clients)`
  in `Services/LaunchRouting.cs` returns a `LaunchRoute`; `LaunchGame` is now a `switch` on it.
  The Galaxy-running / client-installed / `LaunchDirectly` decisions are all in the router and
  covered by `LaunchRoutingTests`.
- `LaunchEaGameViaClient` / `LaunchEpicGameViaClient` / `LaunchUbisoftGameViaClient` →
  one `LaunchViaClientUrl(game, route, url)`. `LaunchGogGameViaGalaxy` stays (Galaxy is a
  command line, not a URL).
- `Launch<Platform>GameDirectly` (x4) → one `LaunchPlatformExeDirectly(game, route)`.
- `FindRunningProcessUnderDirectory` / `TryGetExecutablePath` (MainModule + WMI fallback) →
  `ProcessPathResolver.FindProcessesUnderDirectory` / `GetProcessPath` (OpenProcess with
  PROCESS_QUERY_LIMITED_INFORMATION + QueryFullProcessImageName: no exceptions, no WMI, works on
  elevated targets). Candidates are ranked (has a main window, then newest start time) and
  known helpers (crash handlers, anti-cheat services, redistributable installers) are excluded -
  see `ProcessPathResolver.IsKnownHelperProcess`. That ranking is what makes the "chained
  handoff" case above (R6 Extraction) less timing-dependent: the real game is the newest process
  with a window.
- Raw `System.Threading.Timer` polling → the `Poller` class (period infinite, re-armed after the
  callback returns) so ticks never overlap; the "handled" interlocked guards are gone with it.
- `EndGameSession` + `RunPostExit` + playtime scattered across timer callbacks → one
  `FinishSession(session, gameRan, reason)`, guarded by `ActiveGameSession.Finished`. Every
  launch path creates its session through `BeginSession` (profile → pre-launch script, which may
  now abort the launch) and ends it through `FinishSession`; `EndSessionNow` is the user-facing
  "restore now / force close" entry point used by the tray "Now Playing" section and the card menu.
- The generic direct-exe path gained the stub handoff the platform paths always had: if the
  launched process exits within `StubHandoffWindow`, tracking moves to whatever it spawned under
  the install folder (`TrackInstallDirSession` with `ownsProcessAlready: true`).
- Steam: `TrackSteamSession` now also locates the game's process via
  `SteamScannerService.FindInstallDirForAppId` once Steam's Running flag flips, so priority,
  window focus and force-close work for Steam games; it checks the Running flag *before*
  dispatching so a second click never re-runs scripts or starts a second tracker.
