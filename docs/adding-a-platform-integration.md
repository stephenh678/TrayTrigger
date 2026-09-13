# Adding a game-platform integration

How TrayTrigger integrates a launcher platform (Steam, GOG, EA App, Epic, Ubisoft Connect, Xbox /
PC Game Pass and Battle.net so far) into Scan for Games, manual import, launching and session tracking.

This used to be a chronological build log - GOG, then EA, Epic, Ubisoft, Xbox - where each chapter
added rules and later chapters renamed or overrode earlier ones. It is now organised as
**principles** (defaults, with the reason each exists), a **research checklist**, a **platform
reference**, the **current code map**, and a **pitfalls** catalogue. Every principle is a default,
not a law: when real data from a platform contradicts one, follow the data and record why in that
platform's row of the reference table.

## 1. Principles

1. **Read the platform's own records, verified against a real install.** Registry keys, manifests,
   product databases and the client's own logs are the source of truth. Community tools (Playnite
   above all) are a cross-check, not an authority - its Battle.net title table was missing the
   newest Call of Duty when checked. Community docs and real data disagreed on GOG (DLC shares the
   games key), Xbox (junction paths) and Battle.net (launch codes), each caught only by probing a
   real machine.
2. **No hand-maintained per-title tables.** Nothing that has to be updated when a publisher ships
   a new game: launch-code maps, exe-name lists, title fixups. If a platform seems to need one,
   look harder for the data locally, find a mechanism that doesn't need it, or accept a documented
   gap - and raise it with the user rather than quietly adding the table.
3. **Source-agnostic.** TrayTrigger does not care where a game came from. No detection of, special
   handling for, or named lists of cracked/repacked installs, release groups or upload sites -
   including in title cleanup. Messy names are handled by generic matching (see
   `GameNameExtractor.ResolveGameMatchAsync`), never by naming the mess.
4. **Identity is the platform's stable ID; paths are informational.** Store the ID the platform
   itself uses to launch the game. Re-resolve paths when they can move (Xbox package roots change
   on every update).
5. **Choose the launch mechanism from evidence, per platform.** There is no standard shape: Steam,
   EA, Epic and Ubisoft use URL protocols, GOG a client command line, Xbox shell activation of an
   AUMID, and Battle.net a command line keyed by a product code. Test it cold (client closed) and
   warm (client open) - both have silently failed on real platforms.
6. **Launch through the client when it exists; fall back only where the fallback really works.**
   A direct-exe fallback preserves DRM-free and client-less installs, but it isn't universal (GDK
   exes refuse to run outside their package). Where a fallback exists, route it through the
   install-directory tracker, never single-PID tracking.
7. **Track by install directory with the shared tracker.** Launchers and registered exes are
   routinely stubs or chains. `TrackInstallDirSession` plus `ProcessPathResolver` already handle
   stubs, chains, elevated games and helper processes - reuse them, and verify path assumptions
   with the resolver the app uses, not with PowerShell's `Get-Process`.
8. **The platform's title is trusted.** Put it in `GameEntry.Name` before enrichment, so it reaches
   Steam/RAWG matching as `knownName` and fallback guesses can't rename it.
9. **State what won't be found.** If discovery has a real gap (EA custom install folders, legacy
   UWP Store games), say so in the Settings toggle's own description, not only here.
10. **Each platform keeps its own copy of the plumbing - for now.** Import, ignore, scan-dialog and
    badge wiring are copied per platform (section 4), so a change to one integration can't ripple
    into the others. Merging them into shared code is a possible future option, not a rule and not
    part of any platform's scope; the user decides if and when. Until then, whoever changes one
    platform's copy checks the others' and says which need the same change.

### Decisions that belong to the user

Ask rather than decide:

- **Launch path** - client, direct, or both - including any trade-off, such as losing the client
  overlay or a fallback that sometimes needs a second click.
- **Accepting a discovery gap** - "these games won't be found; still worth building?"
- **Whether to ever merge the per-platform copies** into shared code (principle 10).
- **Any table or list that would need maintenance** (principle 2).
- **Live tests with side effects** - launching the user's games, closing their client.

## 2. Research checklist

Work through it against a real install before writing code. Every item has cost a real bug when
skipped.

| Question | Where answers have come from |
|---|---|
| Which games are installed, and where? | Registry (GOG games key, Ubisoft installs, uninstall entries), JSON manifests (Epic), Gaming Services registry mirror (Xbox), product databases (Battle.net `product.db`) |
| What is the stable ID the client launches by? | The same records; confirm it in the client's own log or shortcut |
| What is the display name? | Usually the records. Ubisoft has none, so the install folder name is used (`preferExe: false`) |
| What is the real exe? | Records where they have one (GOG, Epic, Xbox, Battle.net's `DisplayIcon` - which can be a switcher, as with StarCraft II). Otherwise `FolderScannerService.ScanFolder(installDir)` - don't write new exe-picking heuristics |
| Which entries aren't real games? | DLC (GOG `dependsOn`), the client's own entry (Battle.net `battle.net` uid), component installs, betas |
| How does the client launch a game? | The client's registered URL scheme or command line, and **its own log** - Battle.net logs the exact process and arguments it starts |
| Does that launch work cold and warm? | Test both - they fail in opposite ways. GOG's command line is ignored while Galaxy is open; Battle.net's is dropped while the client is closed |
| Which process is the game? | Watch a real launch: stub → bootstrapper → game is normal. Check with `ProcessPathResolver` |
| Is there a client-less fallback? | Try running the exe directly with the client closed |
| Which client processes does "close launcher after exit" end? | The client UI, not its background updater or service |
| Can the client's own install folder sit under a Scan Location? | Then the folder scanner must skip it (see `FolderScannerService.IsSteamClientInstall`) |
| What won't be found? | Custom folders, legacy formats, account-only entitlements - goes in the toggle text |

Probing techniques that worked:

- Read-only PowerShell over the registry (`Get-ChildItem ... | Get-ItemProperty`) and uninstall
  entries filtered by publisher.
- Printable-string dumps of binary/protobuf files (Battle.net `product.db`, per-install
  `.product.db`) to find IDs and paths before deciding whether to parse them properly.
- The client's log folder, searched for the ID and words like `launch`.
- A throwaway harness outside the repo that references `TrayTrigger.csproj`, to replay real data
  through the real code (see the Dylan replay, 2026-09-12). Build it to a short path, and run it
  with `dotnet <dll>` - the app project is self-contained.

## 3. Platform reference

| Platform | Discovery | ID stored | Launch | Fallback | Notes |
|---|---|---|---|---|---|
| Steam | Library folders + app manifests | `SteamAppId` | `steam://rungameid/<id>`, or `steam://run/<id>//<args>/` when arguments are set | Edit Game "launch directly" | Tracked by Steam's per-app Running flag, then the game process via `FindInstallDirForAppId` |
| GOG | `HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\<id>` (path + exe) | `GogGameId` | `GalaxyClient.exe /command=runGame /gameId=<id> /path=<dir>` - one `/name=value` token each | Direct exe when Galaxy is **running** (warm launch is a silent no-op) or absent | DLC shares the key; filter by `dependsOn` |
| EA | `Origin Games\<id>` registry (ID + name only) + `__Installer\installerdata.xml` under default roots | `EaContentId` | `origin2://game/launch/?offerIds=<id>` | Direct exe | **Gap:** custom install folders aren't found (EA's local cache is encrypted). Stated in the toggle |
| Epic | `C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests\*.item` JSON | `EpicAppName` | `com.epicgames.launcher://apps/<AppName>?action=launch&silent=true` | Direct exe | Everything needed is in the manifest |
| Ubisoft | `HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs\<id>` (`InstallDir` only, forward slashes) | `UbisoftGameId` | `uplay://launch/<id>/0` | Direct exe | No name or exe in the records: folder scan with `preferExe: false`. Registered exe can be a chained stub (R6 Extraction) |
| Xbox | `HKLM\SOFTWARE\Microsoft\GamingServices\GameConfig\<PackageFullName>` + per-user AppModel repository | `XboxAumid` | `IApplicationActivationManager` on the AUMID (`explorer shell:AppsFolder\<AUMID>` fallback) | **None** - GDK exes need package identity | Track by the junction target, not the WindowsApps root. Full name and root change every update, so they're re-resolved at launch. **Gap:** legacy UWP Store games. Accepted: always-resident tray helpers (Roblox) block the profile |
| Battle.net | *Built in 1.4.2-beta.2 (`BattleNetScannerService`, `BattleNetCatalog`, `BattleNetCodeStore`); research and tests in section 3.1.* Uninstall entries: `--uid=<uid>` in `UninstallString`, `InstallLocation`, `DisplayName`; `DisplayIcon` is usually the game exe but can be a switcher (StarCraft II) | `BattleNetUid` (e.g. `btlr`, `hs_beta`), plus the saved launch code `BattleNetProgramId` and a backup uid → code map in `%APPDATA%\TrayTrigger\battlenet-codes.json` | `Battle.net.exe --exec="launch <program id>"`, where the program ID comes from the client's local catalog cache, not from the uid | **None** - the client passes an SSO token the game needs (`-launcherlogin`, `-sso=1`). `--exec="focus play"` when no program ID is found | A cold client drops the command: resend every 3 s until a process appears under the install folder (duplicates are harmless). Close `Battle.net`, not `Agent`. Skip the client's own `battle.net` uid |

### 3.1 Battle.net research and launch tests (2026-09-12)

Tested on one machine with four games: Call of Duty: Black Ops 6 (a newer title), Hearthstone
(an older title whose uid and program ID differ completely), Overwatch, and StarCraft II (launches
through a switcher and allows multiple instances). Evidence came from the client's own log
(`%LOCALAPPDATA%\Battle.net\Logs\battle.net-*.log`), the Agent's
(`C:\ProgramData\Battle.net\Agent\Agent.<version>\Logs\Agent-*.log`), and a process tracer that
recorded every new process (parent, path, command line, first window, exit) - not from watching
the screen.

**Discovery.** Each installed game has an uninstall entry, publisher `Blizzard Entertainment`
(all installs under `C:\Program Files (x86)`):

| Field | Black Ops 6 | Hearthstone | Overwatch | StarCraft II |
|---|---|---|---|---|
| `DisplayName` | Call of Duty: Black Ops 6 | Hearthstone | Overwatch | StarCraft II |
| uid (`--uid=` in `UninstallString`) | `btlr` | `hs_beta` | `prometheus` | `s2` |
| `InstallLocation` | `Call of Duty Black Ops 6` | `Hearthstone` | `Overwatch` | `StarCraft II` |
| `DisplayIcon` | `_retail_\cod24-cod.exe` | `Hearthstone.exe` | `_retail_\Overwatch.exe` | `Support\SC2Switcher.exe` ⚠ a switcher, not the game |

The client's own entry has uid `battle.net` and must be skipped. Component installs such as
`herjabtlr` (an anti-cheat driver) appear only in `product.db`, not in the uninstall list.
Hearthstone's folder also contains `Hearthstone Beta Launcher.exe`, so the registry's exe pointer
beats a folder scan - but StarCraft II shows the pointer isn't always the game. Tracking by
install folder finds the real process either way.

Each install folder also has a hidden `.product.db` (protobuf) holding the uid, the install path
and the TACT product code (`btlr`, `hsb`, `pro`, `s2`). The TACT code is **not** the program ID
(`hsb` vs `WTCG`, `pro` vs `Pro`), so the file only cross-checks the uid.

**Launch tests.** `Battle.net.exe --exec="launch <X>"`:

| # | X | Client | Result |
|---|---|---|---|
| 1 | `btlr` (uid) | running | ❌ Log: "Selecting game family by id: btlr", then nothing |
| 2 | `BTLR` (program ID) | running | ✅ Game started in ~3 s |
| 3 | `HS_BETA` (uid, upper-cased) | running | ❌ Nothing in 40 s |
| 4 | `WTCG` (program ID) | running | ✅ Game started in ~2 s |
| 5 | `WTCG` | **closed** | ❌ Client started, signed in within ~5 s ("Logged into Battle.net successfully"), dropped the launch; nothing in 90 s |
| 5b | `WTCG` again, ~2 min later | running | ✅ Game started in ~2 s |

**What the client starts** (client running, times from the command):

| Game | Process chain | Game window |
|---|---|---|
| Black Ops 6 | `_retail_\bootstrapper.exe cod24-cod.exe -uid btlr` → `cod24-cod.exe` ~4 s later | - |
| Hearthstone | `Hearthstone.exe -launch -uid hs_beta` | 1.5 s |
| Overwatch | `_retail_\Overwatch.exe -uid prometheus` | 5.3 s |
| StarCraft II | `Support64\SC2Switcher_x64.exe -sso=1 -launch -uid s2` → `Versions\Base97563\SC2_x64.exe` (same arguments); the switcher exits once the game window is up (~8 s) | 8.1-8.4 s |

Other processes under the install folders: Overwatch's `ErrorReporting\x64\CrashMailer_64.exe`
(no window, lives as long as the game - already excluded by the helper "crash" rule) and
StarCraft II's in-game `Support\BlizzardBrowser\BlizzardBrowser.exe` (no main window, starts after
the game's). A replica of `TrackInstallDirSession`'s ranking on a live StarCraft II launch attached
to `SC2_x64.exe` after three polls: the switcher is older than the game it spawns, so newest-first
picks the game. Every chain stays under the install folder, so the tracker fits unchanged.

**Duplicate commands** (client running):

| Game | Sends | Result |
|---|---|---|
| StarCraft II (multi-instance) | 0, 2 and 14 s | One game. The 2 s send did nothing; the 14 s send started a switcher that exited in 0.5 s |
| Hearthstone | 0, 2 and 20 s | One game |

**Cold start** (client closed, Agent running unless noted; Hearthstone unless noted):

| Method | Result |
|---|---|
| One `--exec`, nothing else | ❌ Login window at 2.3 s, client window at 4.2 s, no game in 60 s (repeats test 5) |
| Start the client, send when its first window appears | ❌ Sent during "Battle.net Login" (2.2 s) - dropped, not queued |
| Start the client with `--game=hs_beta` (GOG Galaxy plugin) | ❌ Client opens, game never starts |
| Start the client, send once a `Battle.net` window is ≥7 s old (Hearthstone Deck Tracker's rule) | ✅ Sent at 7.3 s, game window 8.3 s |
| **Send every 3 s until a process appears under the install folder** | ✅ Send 2 (login window) dropped, send 3 (6.3 s) launched; game window 7.5 s |
| Same, StarCraft II | ✅ Send 3; game process 6.8 s, window 14.5 s |
| Same, fully cold (client and Agent ended, as after a reboot) | ✅ The client started the Agent itself (0.9 s); send 3; game process 6.9 s |

**Failure and fallback:**
- A wrong program ID (`launch NOPE`) fails silently - no dialog. Only the tracker's "game never
  appeared" timeout notices.
- `--exec="focus play"` restores a minimized client and brings it to the foreground, so it works
  as the fallback when no program ID is found.

**Catalog cache recovery.** With the client closed, the cache folder was renamed away and the client
started: all four uids were back in catalog files 1.2 s later, before sign-in finished. A missing
program ID means the client hasn't run since its cache was cleared.

Conclusions:
- `--exec` takes the **program ID**, and it can't be derived from the uid (tests 1 and 3). The
  catalog lookup worked for all four games.
- A **cold client drops commands** until it's signed in (the login window gave way to the client
  at ~4 s here). They aren't queued.
- **Resend every 3 s until a non-helper process appears under the install folder.** It needs no
  readiness guess and no window title (titles are localized - the Galaxy plugin also matches
  "暴雪战网"), it was the fastest cold method, and duplicates are harmless. Playnite's "5 or more
  processes plus 5 s" and HDT's "window ≥7 s old" are timing guesses. Cap the loop (~90 s) and let
  `TrackInstallDirSession`'s 3-minute window cover the rest.
- Look up the program ID at launch, not only at import. When it's missing, send `focus play` and
  tell the user.
- Close the client by name (`Battle.net`: window close, then kill). The Agent can stay.

**Program ID lookup, with no table.** The client caches its product catalog as JSON under
`%LOCALAPPDATA%\Battle.net\Cache\<xx>\<yy>\<hash>` - file names are hashes, so the whole folder is
scanned. A catalog file has `fragment_id`, `installs` (uid → `tact_product`, `sso_launch_argument`,
...) and `products[]`. Each product's `id` is a program ID, and the install uids it owns appear as
nested `"uid"` values inside that product. **The program ID for a uid is the `id` of the product
that references it:**

| uid | Owning product `id` | Checked by launch |
|---|---|---|
| `btlr` | `BTLR` | ✅ |
| `hs_beta` | `WTCG` | ✅ |
| `prometheus` (Overwatch) | `Pro` | ✅ |
| `s2` (StarCraft II) | `S2` (a second `S2` product owns `s2_cn`) | ✅ |
| `fenris` (Diablo IV) | `Fen` | - |
| `wow`, `wow_classic`, ... | `WoW` (the `WoWC`/`WoWF` products own no uids) | - |

Open (not tested):
- Whether World of Warcraft Classic launches with `WoW` or needs `WoWC`.
- A cold start where sign-in needs the user (password, 2FA), and a launch where the game needs an
  update first - both reasons the resend loop is capped.

**Ruled out:**
- *Launching the exe directly.* Catalog installs carry `requires_sso_token` or
  `sso_launch_argument: -launcherlogin`, which only the client can supply.
- *Playnite's table* (`BattleNetGames.cs`, 35 entries). It has no Black Ops 6, and lists Diablo IV
  as uid `Fen` where the real uid is `fenris` - principle 2 in practice.
- *The client cache's `uid#CODE_ASSET` keys* (`hearthstone#HS_ICON`). These are artwork labels
  keyed by fragment name, not launch codes.
- *`Battle.net.exe --game=<uid>`* (GOG Galaxy plugin). Opens the client; doesn't launch the game.
- *Waiting on window titles.* "Battle.net Login" → "Battle.net" marks sign-in, but titles are localized.
- *The Agent's local HTTP API* (`127.0.0.1`, port in `Agent\Agent.dat`; `GET /game/<uid>` returns
  the launch binary and arguments). It's undocumented and not needed, since the client does the
  launch. It's also unstable: every StarCraft II launch starts another `Agent.exe` (four ran at
  once), and each rewrites `Agent.dat` with its own port. Worth knowing for diagnostics - its
  response marks switchers (`"switcher": true`, plus a `regex` for the real game exe).

### 3.2 Battle.net as built (1.4.2-beta.2)

| Piece | What it does |
|---|---|
| `BattleNetScannerService` | Reads uninstall entries (32- and 64-bit views) whose `UninstallString` is Blizzard's uninstaller with `--uid=`; skips uid `battle.net`. Exe = `DisplayIcon` when it exists under the install folder, else `ScanFolder`. Every scan reads the catalog and refreshes the saved code map |
| `BattleNetCatalog` | Merges every cache file, maps uid → owning product `id`, keeps the id's case, refuses a uid claimed by two ids, validates ids (`[A-Za-z0-9_-]`) before they can reach a command line |
| `BattleNetCodeStore` | `%APPDATA%\TrayTrigger\battlenet-codes.json`: add/update only, never delete; written only when something changed. `Installed` (uid → display name, from this machine's uninstall entries) sits above `Codes` (uid → program ID, Blizzard's whole catalog), so a person can find the one line that matters to them |
| `GameEntry` | `IsBattleNetGame`, `BattleNetUid` (identity), `BattleNetProgramId` (saved launch code) |
| `ProcessLauncherService.LaunchBattleNetGame` | Saved code → otherwise catalog, then saved map, then start the client and re-ask for 20 s → otherwise `focus play` + tray notice. Sends `--exec="launch <code>"` now and every 3 s until a non-helper process is under the install folder, for up to 90 s; then re-checks the catalog once (a different code is saved and gets a fresh 90 s), else `focus play` + tray notice. `TrackInstallDirSession` runs alongside, unchanged |
| Close launcher | `Battle.net` process only; the Agent is left alone |
| Helpers | `BlizzardError` and `BlizzardBrowser` added to `ProcessPathResolver`'s helper list; Battle.net's own install folder is skipped by folder scans |

End-to-end on the test machine, through the real services (scan → `ProcessLauncherService.LaunchGame`
→ close the game), 2026-09-12:

| Scenario | Result |
|---|---|
| Scan | 4 games in 0.2 s, all with codes (`BTLR`, `WTCG`, `Pro`, `S2`); 229 catalog files, 454 uids saved to the code map; dropped SC2 / Overwatch paths resolve to Battle.net; the client folder isn't offered |
| Hearthstone, client closed | ✅ 3 sends (matches the research), game tracked, session ended on exit |
| Hearthstone, client open | ✅ 1 send |
| StarCraft II, client closed | ✅ 3 sends; tracker attached to `SC2_x64.exe`, not the switcher |
| Overwatch, client open | ✅ 1 send; `CrashMailer_64` ignored as a helper |
| Black Ops 6, client open | ✅ 1 send; tracker attached to `cod24-cod.exe` past `bootstrapper.exe` and both crash handlers |
| No saved code | ✅ Catalog lookup at launch, code saved, 1 send |
| Wrong saved code (`WRONG1`) | ✅ 90 s of resends, catalog re-check, code corrected to `WTCG`, game started |
| No catalog and no saved map | ✅ 20 s catalog wait, `focus play`, tray notice, session rolled back when ended |

## 4. Code map

### Shared - reuse, don't copy

- `Services/LaunchRouting.cs` - `LaunchRouter.Resolve` (pure, covered by `LaunchRoutingTests`),
  `LaunchRoute`, `LaunchClientAvailability`, `ClientPlatformFor`, `PlatformLabelFor`.
- `Services/ProcessLauncherService.cs` - `LaunchViaClientUrl` (any URL-scheme client),
  `LaunchPlatformExeDirectly` (any direct fallback), `TrackInstallDirSession` (2 s polls, 3 stable
  sightings, 3 min timeout), `TryActivateRunningProcessUnderDirectory`, and `BeginSession` /
  `FinishSession` / `RollbackSession` for the session lifecycle.
- `Services/ProcessPathResolver.cs` - process paths via `QueryFullProcessImageName` (works on
  elevated games), candidate ranking (has a window, then newest), `IsKnownHelperProcess`.
- `Services/UrlProtocolHelper.cs` - `IsHandlerInstalled` / `GetHandlerExecutablePath`, plus the
  launch-URL scheme allow-list (`AllowedLaunchSchemes`).
- `Services/PlatformLookupService.cs` - resolves a dropped or browsed path to a platform game by
  install-directory containment, so manual imports route through the platform.
- `Services/LibraryConstants.cs` - `PlatformCategories`, `IsEnrichableCategory`,
  `PlatformCategoryFor`: the single category allow-list.
- `Services/LauncherClientCloser.cs` - client process names per platform.
- `Services/GameNameExtractor.cs` / `LibraryViewModel.EnrichGameWithSteamMetadataAsync` - title and
  metadata matching, with `knownName` for trusted titles.
- `Services/FolderScannerService.cs` - scored exe discovery, for platforms whose records don't
  name the exe.

### Still per-platform

A new platform touches every item below. Items marked ⚠ break silently if missed; the compiler
catches the rest.

| Area | Where |
|---|---|
| Discovery record + scanner | `Services/<Platform>ScannerService.cs` (`Discovered<Platform>Game`, `ScanInstalledGames(existingIds)`) |
| Entry fields | `Models/GameEntry.cs` (`Is<Platform>Game`, ID), `Models/LauncherPlatform.cs`, `Models/IgnoredGamePath.cs` |
| Settings | `Models/AppSettings.cs` (`<Platform>IntegrationEnabled`), `ViewModels/SettingsViewModel.cs`, toggle + gap text in `MainWindow.xaml` |
| Routing + launch | `LaunchRoute` values, a `LaunchClientAvailability` field, the `Resolve` branch, `LaunchGame` switch case, `LauncherClientCloser` list |
| Import | `ImportCoordinator`: scan branch, `Import<Platform>GamesAsync`, `Ignore<Platform>Game`, `PlatformImportBuckets`, `PlatformKey`; `MainViewModel` forwarding; `App.xaml.cs` construction |
| Manual import | `PlatformLookupService` (`PlatformMatch.For<Platform>` + lookup) |
| Scan dialog | `ScanForGamesViewModel` (constructor lists, ignore callbacks, `SourceLogoUri`), `ScanForGamesDialog.xaml.cs`, `MainWindow.xaml.cs` (`OnRequestScanResultsPicker`) |
| Library UI | Platform badge in all three views (`MainWindow.xaml`), `GameCardViewModel`, `LibraryFilterViewModel`, `GameEditViewModel` |
| Detection + logos | `Views/LauncherDetectionDialog.xaml.cs` (`DetectedLauncher`), `FolderBatchImportViewModel` logo switch, `Assets/LauncherLogos/` |
| Category | `LibraryConstants.<Platform>Category` + its `PlatformCategories` entry |
| ⚠ Ignored-games list label | `IgnoredGamePathRowViewModel.DisplayPath` |
| ⚠ Dev diagnostics | `App.DevDiagnostics.cs` constructs `ScanForGamesDialog` directly |
| ⚠ Folder scans | If the client can be installed under a Scan Location, skip its install folder in `FolderScannerService` |

Seven platforms each have their own copy of the import, ignore, bucket, scan-dialog and badge rows,
by the user's choice (principle 10). Merging them into one platform descriptor - ID, label, logo,
category, route, scanner - is a possible future option the user decides on; it is not part of adding
a platform. When one platform's copy changes, check the other six.

## 5. Pitfalls

Each of these happened for real.

**Launching**
- **Warm client ignores the launch.** GOG's command line only works cold; open, it shows the
  game's page. Check whether the client is running before dispatching.
- **Registered exe is a stub.** Cyberpunk's `REDprelauncher.exe` exits in milliseconds, and EA's
  fallback exited in ~250 ms, both ending the Performance Profile immediately. Every platform
  fallback goes through `LaunchPlatformExeDirectly`, never the generic direct-exe path.
- **Chained handoffs outlast a short debounce.** R6 Extraction's exe lived ~4 s before handing off,
  hence 3 stable sightings and window-first ranking. If one still slips through, the fix is to
  resume polling when the attached process dies implausibly fast, not a bigger number.
- **"Already running" must match by directory**, because the registered exe may not be the game.

**Tracking**
- **Elevated or anti-cheat games** hide `Process.MainModule`; `ProcessPathResolver` doesn't.
- **Junctions:** the kernel reports the resolved path (Xbox `XboxGames\...\Content`), not the path
  you launched.
- **Always-resident helpers** in an install folder (Roblox's tray process) look like the game is
  already running - an accepted gap.
- **Late setup on a timer thread** can't rely on the launch method's try/catch: roll back
  explicitly, and end every session through `FinishSession`.
- **Playtime** starts at `process.StartTime`, not when polling found the process.
- **Nothing to track** (profile Off, no post-exit script): skip polling entirely.

**Discovery and names**
- **DLC and component entries** share keys with games; filter them.
- **Forward slashes** in registry paths (Ubisoft) - normalise before any `Path` call.
- **Trusted names get overwritten** by folder or exe guesses - pass `knownName`, and keep
  `GuardAgainstKnownName` in the loop ("Fortnite" once became "Content Warning").
- **Folder-named platforms** need `preferExe: false`, or the exe stem wins ("R6 Extraction Plus").
- **Client installs look like games** to a folder scan - Dylan's `C:\Games\Steam` became "Steam
  Deck".
- **Unreal project folders** (`G1R`, `P3R`) aren't titles; the install folder above is.
- **Easy-to-miss wiring** (the ⚠ rows above) has shipped broken before: GOG's ignored-games label
  and the category allow-list were both caught only in review.

## 6. Verify

- `dotnet build` and `dotnet test` after the backend wiring, before XAML - most call-site mistakes
  surface in one pass.
- Scan a real install; import; launch cold and warm; confirm in `debug.log` that the session
  attached to the real game process, applied the profile, and restored it on exit (timestamps show
  stub-exit bugs immediately).
- Drop the game's exe onto the library to confirm manual import routes through the platform.
- For a large diff, `/code-review ultra` caps at about 8,000 changed lines per run: split into
  batches such as services vs. view-model/UI.
