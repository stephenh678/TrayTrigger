# TrayTrigger Code Review Findings

Full-codebase review of the shipped .NET 10 / WPF code as of commit `300d1cc` (v1.0.7).
`App.DevDiagnostics.cs` (DEBUG-only tooling) was deliberately excluded. XAML was reviewed
only where it affects behaviour (bindings, commands, input handling), not for visual styling.

## How to use this file

Each finding below is a self-contained task. Work them **one at a time, highest severity first**,
and follow this loop for every one:

1. **Re-verify before changing anything.** Line numbers are from commit `300d1cc` and will drift.
   Open the file, find the code described, and confirm the problem still exists. If it does not,
   mark the finding `Status: Already fixed` with a one-line note and move on.
2. **Fix only what the finding describes.** Do not refactor surrounding code, rename things, or
   "improve" unrelated logic. If the suggested fix turns out to be wrong for a reason you can
   see in the code, write your reasoning in the `Resolution` line and either apply a better fix
   or mark it `Status: Needs human decision`.
3. **Build after every change:**
   ```bash
   dotnet build TrayTrigger.csproj -c Release -nologo -v q
   ```
   The build must finish with 0 warnings and 0 errors. If a change adds a warning, fix it.
4. **Run the verification steps** listed in the finding. If you cannot run them (no display, no
   network), say so in the `Resolution` line rather than claiming success.
5. **Record the outcome** by ticking the checkbox and filling in `Status` and `Resolution`.
   Allowed status values: `Fixed`, `Already fixed`, `Won't fix (reason)`, `Needs human decision`.
6. **Commit one finding per commit** with the message `Fix <ID>: <short summary>` so the history
   maps to this file.

Confidence markers:
- **CONFIRMED** = the defect was traced through the code and the failure path is certain.
- **PLAUSIBLE** = strongly indicated by the code but depends on runtime/OS behaviour that was
  not executed during review. Verify with the steps given before and after fixing.

Severity guide: **High** = data loss, system-wide side effect, or security. **Medium** = user-visible
bug, noticeable performance problem, or robustness gap. **Low** = correctness nit, cleanup, or
maintainability.

Things that were checked and are **correct as written** are listed at the end under
"Verified OK - do not change". Do not "fix" those.

## Project facts for implementers

Read this before touching code. There is no CLAUDE.md; this section is the substitute.

**What it is.** A single-project .NET 10 WPF desktop app (`TrayTrigger.csproj`, `net10.0-windows`,
`win-x64`, single-file self-contained publish). No solution file, no test project, no DI container.
Services are constructed in `App.OnStartup` and handed to `MainViewModel`. UI is MVVM with
`ViewModelBase` / `RelayCommand`; dialogs are in `Views/` and are shown via events raised by the
ViewModel and handled in `MainWindow.xaml.cs`.

**Build (must be clean):**
```bash
dotnet build TrayTrigger.csproj -c Release -nologo -v q
```
Output: `bin\Release\net10.0-windows\win-x64\TrayTrigger.exe`. If the build fails to copy the exe,
a TrayTrigger instance is running from that folder; close it first (tray icon > Exit).

**Run for manual verification:**
```bash
"bin/Release/net10.0-windows/win-x64/TrayTrigger.exe"
```
`--minimized` starts to the tray only. The app is single-instance (mutex); a second launch just
activates the first. Do **not** run `publish\` or an installed copy to verify a fix; those are stale.

**Where runtime data lives** (safe to inspect, back up before deliberately corrupting for H-03):
- Library + settings: `%AppData%\TrayTrigger\games.json`, `settings.json` (+ `.bak`)
- Icon/poster cache: `%LocalAppData%\TrayTrigger\Icons`, `...\Covers`
- Log: `%LocalAppData%\TrayTrigger\debug.log` (turn on Settings > Diagnostics > Verbose for traces)

**Conventions to match:**
- File-scoped namespaces, nullable enabled, `ImplicitUsings` on.
- P/Invoke uses `[LibraryImport]` on `partial` classes, not `[DllImport]` (one legacy exception in
  `SystemInfoService`). Regexes use `[GeneratedRegex]`.
- Logging goes through `LoggingService.Info/Warn/Error/Verbose("Category", msg)`.
- JSON uses the source-generated `AppJsonContext`; a new persisted type must be added there.
- Dialogs: use `ModernDialog.Confirm/ShowInfo/ShowWarning(owner, ...)`, never `MessageBox`.
- New windows must call `WindowThemeService.PrepareForFirstShow(this)` right after
  `InitializeComponent()` (see any dialog in `Views/`).

**Hard constraints:**
- Do not add NuGet packages without asking. Current: `H.NotifyIcon.Wpf`, `System.Management`.
- Do not edit `App.DevDiagnostics.cs`, `setup.iss`, or `.github/workflows/release.yml` unless a
  finding names them.
- Do not change the `<Version>` in the csproj; releases are cut separately.
- Do not commit anything under `bin/`, `obj/`, `publish/`, or `*.bak` (already git-ignored).
- Keep changes on a branch (`git checkout -b review-fixes` first) and never force-push.

**Suggested kickoff prompt for the implementing session:**

> Open `CODE_REVIEW_FINDINGS.md` in this repo and read the "How to use this file" and
> "Project facts for implementers" sections in full. Then work through the findings in order,
> starting with `<ID>`, following the loop exactly: re-verify, fix only what is described, build
> clean, run the verification steps, record Status and Resolution in the file, commit one finding
> per commit as `Fix <ID>: <summary>`. Stop and report if a finding is marked "Needs human decision"
> or if you disagree with a suggested fix.

Give a session a contiguous range (for example "H-01 through H-03" or "M-06 through M-12") rather
than the whole file, so its context stays small and its commits stay reviewable.

---

## High

### H-01 Hotkey text boxes register partial and unmodified hotkeys system-wide on every keystroke
- [x] Status: Fixed | Resolution: Changed both hotkey TextBox bindings (`MainWindow.xaml` GlobalManageHotkey, `Views/GameEditDialog.xaml` per-game Hotkey) from `UpdateSourceTrigger=PropertyChanged` to `LostFocus`, so intermediate keystrokes no longer flow to the ViewModel/`RegisterHotkeys` at all. Also hardened `HotkeyManager.ParseHotkey` to reject any combo with no modifier unless the key is a standalone F1-F24, closing the remaining case where a user tabs away with an unmodified single key (or the recommended "F10" typed alone). Release build: 0 warnings, 0 errors. Could not run the manual GUI verification (type in Settings, switch to Notepad, inspect debug.log) — no tool available in this session to drive native WPF window focus/typing; verified by code inspection only (the setter path from TextBox -> `_onHotkeySettingChanged` -> `RegisterHotkeys` -> `ParseHotkey` no longer fires per-character, and `ParseHotkey("c", ...)` now returns false).
- **Confidence:** CONFIRMED
- **Category:** correctness / system side effect
- **Files:** `MainWindow.xaml:1345`, `Views/GameEditDialog.xaml:296-300`, `ViewModels/SettingsViewModel.cs:706-719`, `ViewModels/MainViewModel.cs:2118-2121`, `Services/HotkeyManager.cs:67-101,121-151`
- **What is wrong:** Both hotkey inputs are plain `TextBox`es bound with `UpdateSourceTrigger=PropertyChanged`. The settings binding calls `AutoSaveSettings()` and `_onHotkeySettingChanged` on every character, which runs `HotkeyManager.RegisterHotkeys` (unregister all, re-register all). `ParseHotkey` does not require a modifier, so while the user types `Ctrl+Alt+G` the intermediate strings `C`, `Ctrl+A`, `Ctrl+Alt+G` are each registered globally. For a moment `C` and `Ctrl+A` are stolen from every application on the machine. The Edit dialog's help text even recommends `F10` alone, which permanently steals F10 from all apps.
- **Why it matters:** Global hotkey theft breaks other applications and is very hard for a user to diagnose. Re-registering every hotkey per keystroke also spams the log with "Failed to register" warnings.
- **Suggested fix:**
  1. In `HotkeyManager.ParseHotkey`, return `false` when `modifiers == 0` unless the key is `F1`-`F24` (function keys alone are a defensible exception; document it).
  2. Stop applying on every keystroke: change both bindings to `UpdateSourceTrigger=LostFocus` **or** keep `PropertyChanged` for display but only call `_onHotkeySettingChanged` when `ParseHotkey` succeeds, and debounce with a `DispatcherTimer` (~400 ms).
  3. Better: replace the TextBox with a key-capture control that builds the string from `PreviewKeyDown` (`Keyboard.Modifiers` + `e.Key`), so users cannot type invalid text at all.
  4. Show a red border / status text when the string does not parse.
- **How to verify:** Open Settings, clear the hotkey box, type `c` and then switch to Notepad and type `c`; the character must appear. Confirm `debug.log` no longer gets a "Failed to register" line per keystroke.

### H-02 `ParseHotkey` accepts numeric strings as raw enum values ("Ctrl+5" registers Ctrl+Clear)
- [x] Status: Fixed | Resolution: `ParseHotkey` now special-cases a single digit main-key token, mapping it to `Key.D0 + digit` instead of `Enum.TryParse`, and rejects multi-digit numeric tokens outright (no real key corresponds to them). Release build: 0 warnings, 0 errors. Verified with a standalone harness (referencing the built `TrayTrigger.dll`) covering the finding's test table: `"Ctrl+5" -> D5`, `"Ctrl+Alt+G" -> G`, `"F10" -> F10`, `"Ctrl+" -> false`, `"5" -> false` (per H-01 policy), `"c" -> false`, `"Ctrl+Alt+5" -> D5` — all passed. Did not run the full end-to-end verification (bind a game to Ctrl+Alt+5, press it, confirm the game launches) — no tool in this session can send a global hotkey keypress and observe process launch; the harness confirms the parser now resolves to the correct virtual key instead.
- **Confidence:** CONFIRMED
- **Category:** correctness
- **Files:** `Services/HotkeyManager.cs:144-148`
- **What is wrong:** `Enum.TryParse<Key>("5", true, out key)` succeeds with `Key` value 5 (`Key.Clear`), not `Key.D5`. Any digit typed as the main key maps to an unrelated key. Same for any other integer string.
- **Suggested fix:** Before `Enum.TryParse`, handle digits explicitly: if `mainKeyStr` is a single char `0`-`9`, use `Key.D0 + digit`. Reject any remaining string that `int.TryParse` accepts. Add a unit test table: `"Ctrl+5" -> D5`, `"Ctrl+Alt+G" -> G`, `"F10" -> F10`, `"Ctrl+" -> false`, `"5" -> D5` or false depending on H-01 policy.
- **How to verify:** Set a game hotkey to `Ctrl+Alt+5`, press it, the game launches.

### H-03 A corrupt `games.json` is silently discarded and the backup is overwritten on the next save
- [x] Status: Fixed | Resolution: `StorageService` now archives an unreadable primary to `<file>.corrupt-yyyyMMdd-HHmmss` before falling back to `.bak` (same for settings), remembers for the rest of the process that the primary was unreadable, and skips rolling it into `.bak` on the very next save (flag clears after that save, so normal rolling backup resumes once the primary is valid again). `LoadGames`/`LoadSettings` expose `GamesLoadWarning`/`SettingsLoadWarning`; `MainViewModel`'s constructor shows a one-time `ModernDialog.ShowWarning` after `LoadLibrary()` if either is set. Release build: 0 warnings, 0 errors. Verified against real `%AppData%\TrayTrigger` data (backed up first, restored after): replaced `games.json` with `{ garbage`, deleted `.bak`, launched `TrayTrigger.exe --minimized` - `games.json.corrupt-*` was created and `debug.log` shows the archive line; a ~6.6s gap in the log between "Initializing ViewModel" and "Initializing MainWindow" is consistent with the warning dialog appearing and blocking startup, but this session has no way to screenshot/drive a native WPF dialog to confirm it was visually correct or to click it, and the process exited on its own a few seconds later for reasons not fully explained (possibly this sandboxed session lacks an interactive window station) - flagging this as something to double check by running the app normally. Separately reproduced the exact double-save data-loss scenario from a small harness against the real files (corrupt primary + valid `.bak`, present): `LoadGames()` recovered all 4 real games from `.bak`; the first `SaveGames()` left `.bak` byte-for-byte unchanged (skipped as intended); the second `SaveGames()` resumed normal rolling backup with `.bak` mirroring the now-valid primary. No games were lost at any point and all real data was restored afterward.
- **Confidence:** CONFIRMED
- **Category:** data loss
- **Files:** `Services/StorageService.cs:134-183` (LoadGames), `226-252` (SaveGames), `254-303` (LoadSettings)
- **What is wrong:** If `games.json` fails to parse and `games.json.bak` also fails (or is missing), `LoadGames` returns an empty list. The next `SaveGames` (any launch, favourite toggle, or the enrichment pass) copies the unreadable primary over `.bak` and then writes the empty list as the new primary. After the *second* save the `.bak` is also the empty list and the original library is gone. `LoadSettings` has the same shape and additionally calls `SaveSettings(defaultSettings)` immediately.
- **Suggested fix:**
  1. When the primary fails to parse, copy it to `games.json.corrupt-yyyyMMdd-HHmmss` before doing anything else (same for settings).
  2. Remember that the primary was unreadable this session and **do not** roll it into `.bak` on the next save (skip the `File.Copy` when the flag is set, then clear the flag).
  3. Surface it: return a flag/result the ViewModel can use to show a one-time `ModernDialog.ShowWarning` ("Your library file could not be read. A copy was kept at ...").
- **How to verify:** Replace `%AppData%\TrayTrigger\games.json` with `{ garbage`, delete `.bak`, start the app, toggle a favourite, restart. The `.corrupt-*` file must exist and the warning must have shown.

### H-04 "Reset Defaults" writes policy values that never existed and hard-coded "defaults" instead of restoring prior state
- [x] Status: Fixed | Resolution: Applied the finding's stated minimum-safe fix (full snapshot/restore was explicitly out of scope): `ResetAllToDefaults` no longer calls `ApplyTweak("hags"/"delivery_opt"/"telemetry_sweeps"/"nagle_disable", false)` (which wrote `HwSchMode=1`, `DODownloadMode=3`, `AllowTelemetry=3`, `TcpAckFrequency=0`/`TCPNoDelay=0`). It now calls new `ResetHagsToDefault`/`ResetDeliveryOptimizationToDefault`/`ResetTelemetryToDefault`/`ResetNagleToDefault` methods that delete those registry values via a new `DeleteHklmValuesBatch` helper (direct `RegistryKey.DeleteValue` when elevated, chained `reg delete ... /f` via UAC otherwise - mirrors the existing `SetHklmValuesBatch` split), restoring "value absent" instead of writing a different fixed number. `timer_resolution` was left untouched: the finding's prose only calls out HAGS/DeliveryOptimization/Telemetry/Nagle, and toggling an individual tweak off elsewhere is a deliberate "force off" that legitimately writes a value, unlike Reset restoring the untouched-machine state. Release build: 0 warnings, 0 errors. Did **not** run the finding's own verification steps (apply a preset, reset, diff real HKLM policy/driver/TCP-interface state, check the Windows Settings "managed by organization" banner) - that requires making live elevated changes to this machine's system registry/policy state, which falls under "modifying system or security settings" that I don't perform even with the file's pre-authorization, and the finding itself says to do this on a test VM. Verified only by static review: the four reset paths now route through the new delete-based helpers instead of the old value-writing `Set*` calls, and the build is clean. Please verify on a disposable VM per the finding's steps before relying on this.
- **Confidence:** CONFIRMED
- **Category:** system side effect / correctness
- **Files:** `Services/SystemTweaksService.cs:448-466` (ResetAllToDefaults), `1286-1290` (SetDeliveryOptimization), `1313-1317` (SetTelemetry), `847-851` (SetHags), `1197-1200` (SetTimerResolution), `1255-1284` (SetNagleDisabled)
- **What is wrong:** Reset does not restore what was there before. It writes fixed values:
  - `HKLM\SOFTWARE\Policies\...\DeliveryOptimization\DODownloadMode = 3` and `...\DataCollection\AllowTelemetry = 3`. On a stock machine these **policy** values do not exist. Creating them makes Windows Settings show "Some settings are managed by your organization", and `DODownloadMode=3` enables internet peer uploads, which is more aggressive than the real Windows default (1 on Pro).
  - `HwSchMode = 1` forces HAGS off; the Windows default is "value absent, driver decides", which on modern GPUs means on.
  - `TcpAckFrequency = 0` / `TCPNoDelay = 0` on every interface; the default is "value absent".
- **Suggested fix:** Before the first change to any tweak, snapshot the original value (or `null` for absent) of every registry value the tweak touches into `%AppData%\TrayTrigger\tweak-snapshot.json`. `ResetAllToDefaults` restores from the snapshot, deleting values that were absent. Until that exists, the minimum safe change is: on reset, **delete** the policy values (`reg delete ... /f`) and the Nagle values instead of writing numbers, and delete `HwSchMode` rather than writing 1.
- **How to verify:** On a test VM, note the registry state, apply the preset, reset, and diff. Windows Settings > Delivery Optimization must not show the "managed by organization" banner.

### H-05 Bulk "Apply Performance Preset" and "Reset Defaults" run with no confirmation
- [x] Status: Fixed | Resolution: `ExecuteApplyPreset`/`ExecuteResetDefaults` in `SystemViewModel` now build the list of tweak names that will actually change (`Tweaks.Where(t => t.CanToggle && t.IsOptimal != target)`, comparing current `IsOptimal` to the target state - `false` for Apply Preset since it turns tweaks *on*, `true` for Reset since it turns tweaks *off*) and call a new `ConfirmBulkAction` helper that shows `ModernDialog.Confirm` naming them and noting admin approval/restart may be needed; the tweaks service call and status update are skipped entirely if the user cancels, or if nothing would change (dialog skipped, since there's nothing to confirm). Release build: 0 warnings, 0 errors. Could not run the manual GUI verification (click each button, confirm the dialog appears, confirm Cancel makes no change) - no tool in this session can drive the native WPF System tab; also did not click through to actually invoke `ApplyRecommendedPerformancePreset`/`ResetAllToDefaults` since those write live elevated registry state, which I don't do outside of an explicit user-approved action. Verified only by code inspection: both execute methods now return before touching the tweaks service when `ConfirmBulkAction` returns false.
- **Confidence:** CONFIRMED
- **Category:** safety / UX
- **Files:** `ViewModels/SystemViewModel.cs:414-430`, `Views/SystemView.xaml:341-343`
- **What is wrong:** One click changes ~16 machine settings, creates a power plan, triggers a UAC prompt, and can end in a reboot prompt. Individual toggles at least show their name; the bulk buttons show nothing first.
- **Suggested fix:** Call `ModernDialog.Confirm` before `ApplyRecommendedPerformancePreset()` / `ResetAllToDefaults()` listing the tweak names that will change (compare current `IsOptimal` to target) and noting that admin approval and a restart may be needed.
- **How to verify:** Click each button; a confirmation appears; Cancel makes no change.

### H-06 Update installer is executed with no integrity check, from a repository string that is never validated
- [ ] Status: Needs human decision | Resolution: Re-verified the problem still exists (`CheckForUpdatesAsync` interpolates the user-editable `GitHubRepository` setting unvalidated into the GitHub API URL; `DownloadAssetAsync` fetches whatever `BrowserDownloadUrl` is with no host/scheme check; `LaunchInstallerAndExit` runs the downloaded file with no signature or hash check). The full suggested fix's integrity check (Authenticode signature, or a `SHA256SUMS` asset otherwise) needs either code-signing the installer or publishing checksums from `.github/workflows/release.yml` - a file I was told not to edit for this range. I asked the user how to scope this given that constraint; they said to skip this finding for now rather than pick a partial fix. No code changed for H-06. Left `Status: Open` in effect (recorded as "Needs human decision" per the file's allowed status values) so a future session picks it up once the user decides how to handle the release-pipeline side (code signing vs. a separately-authorized `release.yml` change vs. accepting a partial fix).
- **Confidence:** CONFIRMED (for missing validation) / PLAUSIBLE (exploitability depends on threat model)
- **Category:** security
- **Files:** `Services/UpdateService.cs:76-82,142-196,201-216`, `ViewModels/SettingsViewModel.cs:303-316`, `Models/AppSettings.cs:33`
- **What is wrong:** `GitHubRepository` is user-editable free text and is interpolated straight into `https://api.github.com/repos/{repo}/releases/latest`. Anything with slashes or `..` changes the endpoint. The chosen asset's `browser_download_url` is downloaded and run via `Process.Start` with no host check, signature check, or hash check. Anyone who can edit `settings.json` (or compromise the configured repo) gets arbitrary code execution with the user's rights on the next update prompt.
- **Suggested fix:**
  1. Validate the repository setting with `^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$` in the setter and in `CheckForUpdatesAsync`; fall back to the default on failure.
  2. Before downloading, require `BrowserDownloadUrl` to be `https` and the host to be `github.com` or `objects.githubusercontent.com`.
  3. Verify the download: if the release exe is Authenticode-signed, check the signature (`X509Certificate.CreateFromSignedFile` + chain) before launching. If it is not signed, publish a `SHA256SUMS` asset from `release.yml` and verify it here.
- **How to verify:** Set the repo setting to `foo/../../rate_limit`; the check must be refused. Corrupt a downloaded installer byte; launch must be refused.

---

## Medium

### M-01 Tweak success is reported optimistically; the UAC path returns "failed" if the prompt takes more than 5 seconds
- [x] Status: Fixed | Resolution: (a) Both elevated-`cmd.exe` batch helpers (`SetHklmValuesBatch` and the `DeleteHklmValuesBatch` added for H-04) now wait up to 120s and check `HasExited` before reading `ExitCode`, instead of reading `ExitCode` on a possibly-still-running process and letting the resulting `InvalidOperationException` get swallowed as "failed". (b) `RunCommandBatch` (used by the power plan tweak) now actually checks the process's exit code instead of ignoring it, and returns `bool`; `ApplyUltimatePlanTweaks` propagates that; `SetPowerPlan` now compares the real active scheme GUID (`GetActivePowerSchemeGuid()`) against the target instead of unconditionally returning `true`. (c) Added `SystemTweaksService.GetTweakState(id)`, calling the same `Check...` method `GetAllTweaks()` uses per tweak; `SystemTweakViewModel.ExecuteToggle` no longer trusts `ApplyTweak`'s return value for `IsOptimal` - it re-reads the real state via `GetTweakState` and sets `IsOptimal`/`StatusText`/the success message from that. Release build: 0 warnings, 0 errors. Note: per the finding's own suggested fix, the UAC wait is now up to 120s of the calling thread blocked in `Process.WaitForExit` (same synchronous pattern as before, just longer) - toggling an HKLM tweak can now make the window appear unresponsive for up to 2 minutes if the user leaves a UAC prompt open, versus 5s before; making this asynchronous was not in scope for this finding. Verified: build clean; a read-only harness (referencing the built DLL, no registry writes) called `GetTweakState` for every tweak id and confirmed each matches `GetAllTweaks()`'s own `IsOptimal` with no exceptions. Did **not** run the finding's own manual verification (toggle an HKLM tweak as non-admin and wait 10s before accepting UAC) - that requires making a live elevated registry change and clicking through a UAC prompt, which I don't do outside an explicit user-driven action, and no tool in this session can drive that dialog anyway.
- **Confidence:** CONFIRMED
- **Category:** correctness
- **Files:** `Services/SystemTweaksService.cs:1398-1408` (elevated `cmd.exe` + `WaitForExit(5000)` + `ExitCode`), `1137-1153` (RunCommandBatch ignores exit codes), `923-955` (SetPowerPlan always returns true), `ViewModels/SystemViewModel.cs:93-101` (sets `IsOptimal = targetState` without re-checking)
- **What is wrong:** `proc?.WaitForExit(5000)` returns after 5 s if the user is still looking at the UAC dialog; reading `ExitCode` on a running process throws `InvalidOperationException`, which the `catch` turns into `false`, so the UI says "Failed" while the change is applied moments later. Chaining with `&` means the exit code is that of the *last* `reg add` only. `RunCommandBatch` (power plan) never checks results, and `SetPowerPlan` returns `true` regardless. The ViewModel then trusts the return value instead of re-reading the system.
- **Suggested fix:** Use `WaitForExit()` with a generous timeout (120 s) and check `HasExited` before `ExitCode`. Replace the `&`-chained `reg add` with a generated `.reg` file applied via a single elevated `reg import`, or check each command. After any `ApplyTweak`, re-run the matching `Check...` and set `IsOptimal` from the real state (expose a `GetTweakState(id)` on the service).
- **How to verify:** As a non-admin, toggle an HKLM tweak and wait 10 s before accepting UAC. The UI must show the correct final state.

### M-02 Deleting a game removes a poster file shared by other entries with the same Steam AppId
- [x] Status: Fixed | Resolution: `DeleteCachedArtwork` now checks, per file (icon and cover independently), whether any other entry still in `Games` has the exact same path before deleting it, via a new `IsArtworkPathStillReferenced` helper; if another live entry references the file, the delete for that file is skipped (the `SteamMetadataService.InvalidateCache` call is unaffected, since that's keyed by AppId and only affects the in-memory lookup cache, not the shared file). Both call sites (the immediate cleanup of a superseded pending-undo entry, and the timer-driven cleanup once the undo window expires) go through this same method, and by the time either runs the entry being cleaned up has already been removed from `Games`, so "still referenced" correctly means "referenced by a surviving entry." Release build: 0 warnings, 0 errors. Could not run the manual GUI verification (link two games to the same AppId via "Add Anyway", delete one, wait for the undo toast, confirm the other keeps its poster) - this needs the native WPF grid and context menu, which no tool in this session can drive. Verified by code inspection only.
- **Confidence:** CONFIRMED
- **Category:** correctness
- **Files:** `ViewModels/MainViewModel.cs:1461-1494`
- **What is wrong:** Steam posters are cached at `Covers/{appId}.jpg`, so two library entries linked to the same AppId (duplicates added with "Add Anyway", or a Steam entry plus a local exe entry) share one file. `DeleteCachedArtwork` deletes it when either is removed, blanking the other's poster.
- **Suggested fix:** Before deleting, skip the file if any remaining `Games` entry has the same `CoverImagePath` (or the same `SteamAppId`). Same guard for `IconPath`.
- **How to verify:** Link two games to the same AppId, delete one, wait for the undo toast to expire; the other keeps its poster.

### M-03 Deleting the last game in a category leaves an empty grid with "All Games" highlighted
- [x] Status: Fixed | Resolution: In `RebuildCategories`, the branch that falls back to `"All"` when the previously-selected category no longer exists now also sets `_settings.LastCategoryFilter = "All"` and calls `FilteredGames.Refresh()`, matching what the normal `SelectedCategory` setter does (which this code path bypasses by writing the backing field directly). Traced `FilterGameItem` (the view's filter predicate): it reads `SelectedCategory`, which returns the now-corrected `_selectedCategory`, so the `Refresh()` re-evaluates every card against `"All"` and un-hides them. Release build: 0 warnings, 0 errors. Could not run the manual GUI verification (create a "Test" category, select its tab, delete the game, confirm the grid shows all games) - no tool in this session can drive the native WPF category tabs/grid. Verified by tracing the filter predicate logic only.
- **Confidence:** CONFIRMED
- **Category:** correctness / UX
- **Files:** `ViewModels/MainViewModel.cs:2146-2175`
- **What is wrong:** `RebuildCategories` resets `_selectedCategory` to `"All"` when the previous category disappears, but does not call `FilteredGames.Refresh()` or update `_settings.LastCategoryFilter`. Items that were filtered out stay hidden until something else refreshes the view, and the stale category is saved.
- **Suggested fix:** When the selection changes inside `RebuildCategories`, also set `_settings.LastCategoryFilter = "All"` and call `FilteredGames.Refresh()`.
- **How to verify:** Put one game in category "Test", select the Test tab, delete the game. The grid must show all games.

### M-04 Renaming a game does not re-sort the grid
- [x] Status: Fixed | Resolution: Added `FilteredGames.Refresh()` to `ApplyRename` (`ViewModels/MainViewModel.cs`) and to `OnRequestEditGameDialog`'s success path (`MainWindow.xaml.cs`, after a successful Edit dialog), matching the sibling `ApplyCategory` method which already did this. Went with the finding's first suggested option (explicit `Refresh()` calls) rather than enabling `IsLiveSorting`, since it's the smaller, more targeted change. Traced that `FilteredGames.SortDescriptions` stays populated by `ApplySort()` for the life of the view, so a manual `Refresh()` re-filters *and* re-sorts against the current sort option, not just re-filters. Release build: 0 warnings, 0 errors. Could not run the manual GUI verification (rename "Alpha" to "Zulu" in A-Z sort, confirm it jumps to the end immediately) - no tool in this session can drive the native WPF grid/rename dialog. Verified by tracing the collection-view refresh/sort behavior only.
- **Confidence:** CONFIRMED
- **Category:** UX
- **Files:** `ViewModels/MainViewModel.cs:1343-1351` (ApplyRename), `MainWindow.xaml.cs:199-205` (after Edit dialog)
- **What is wrong:** `ListCollectionView` does not re-sort when an item's property changes unless live sorting is enabled. Neither path calls `FilteredGames.Refresh()` / `ApplySort()`.
- **Suggested fix:** Call `FilteredGames.Refresh()` in `ApplyRename` and after a successful Edit dialog. Alternatively enable `IsLiveSorting` with `LiveSortingProperties` = Name / Game.IsFavorite / Game.LastPlayed / Game.CumulativePlaytimeMinutes once in the constructor.
- **How to verify:** Rename "Alpha" to "Zulu" in A-Z sort; it moves to the end immediately.

### M-05 Edit dialog "Cancel" does not undo cover changes made by "Fetch by Steam ID" / "Refresh Poster"
- [x] Status: Fixed | Resolution: Added a private `_fetchedCoverPath` field, staged the same way `CustomCoverPath` already is. `ApplyFetchedCover` (called by both `FetchBySteamIdAsync` and `RefreshPosterAsync`) now sets `_fetchedCoverPath` and updates the preview instead of writing `SourceGame.CoverImagePath` directly; `Save()` gained an `else if` branch that commits `_fetchedCoverPath` to `SourceGame.CoverImagePath` only when neither a manual custom-cover pick nor an explicit removal took precedence; `UpdateCoverPreview()` checks `_fetchedCoverPath` first so the preview still updates live; the `CustomCoverPath` setter clears `_fetchedCoverPath` so a manual "Change..."/"Remove" after a fetch correctly wins. As the finding notes, the downloaded file overwriting `Covers/{appId}.jpg` on disk during fetch is unavoidable and left as-is - only the `GameEntry.CoverImagePath` *reference* is now staged. Release build: 0 warnings, 0 errors. Verified with a reflection-based harness against the built DLL (real `GameEditViewModel`/`GameEntry`, no GUI): `ApplyFetchedCover` no longer mutates `SourceGame.CoverImagePath`, `Cancel()` leaves the original path in place, and `Save()` on a fresh dialog after a fetch correctly commits the fetched path - all three checks passed. Did not run the manual GUI verification (Fetch by ID, Cancel, re-open, confirm original cover) since no tool here can drive the native dialog or hit the real Steam API, but the harness exercises the same code paths those clicks would trigger.
- **Confidence:** CONFIRMED
- **Category:** correctness
- **Files:** `ViewModels/GameEditViewModel.cs:590-600` (ApplyFetchedCover writes `SourceGame.CoverImagePath` directly), `491-541`, `550-586`
- **What is wrong:** The dialog edits a copy of every field except the cover, which is written straight onto the shared `GameEntry`. Cancel leaves the new cover in place and it is persisted by the next save.
- **Suggested fix:** Store the fetched cover in a private `_fetchedCoverPath` field, show it in the preview, and only assign `SourceGame.CoverImagePath` inside `Save()`. (Overwriting the on-disk `Covers/{appId}.jpg` during fetch is unavoidable and acceptable.)
- **How to verify:** Open Edit, Fetch by ID, Cancel, re-open Edit: the original cover path is unchanged.

### M-06 Live telemetry runs a WMI query every 3 seconds on the UI thread
- [x] Status: Fixed | Resolution: Split `GetRamInfo()` into `GetRamUsage()` (`GlobalMemoryStatusEx` only) and `ApplyRamSpeedInfo(ram)` (the `Win32_PhysicalMemory` WMI query); `GetRamInfo()` now just calls both in sequence so `GetFullHardwareReportAsync()` is unaffected, while `GetQuickTelemetry()` (the dispatcher timer's 3s poll) calls `GetRamUsage()` directly and never touches WMI. Release build: 0 warnings, 0 errors. Verified with a harness against the built DLL: 20 consecutive `GetQuickTelemetry()` calls all completed in 0ms (previously this path ran a WMI query every call, which the finding measured at tens-to-hundreds of ms), and a full `GetFullHardwareReportAsync()` call still returned real RAM speed data (5600 MHz on this machine), confirming the split didn't drop that data from the full report. Could not run the manual GUI verification (open the System tab, drag the window, confirm no periodic hitch) - no tool in this session can drive the native window - but the timing measurement directly confirms the root cause (per-tick WMI overhead) is gone.
- **Confidence:** CONFIRMED
- **Category:** performance
- **Files:** `Services/SystemInfoService.cs:149-154` (GetQuickTelemetry), `370-411` (GetRamInfo includes `Win32_PhysicalMemory` WMI), `ViewModels/SystemViewModel.cs:304-354` (DispatcherTimer tick)
- **What is wrong:** The comment says "less than a millisecond without WMI overhead" but `GetRamInfo` issues a WMI query each call. WMI queries take tens to hundreds of ms and the tick runs on the dispatcher, so the System tab stutters every 3 s.
- **Suggested fix:** Split RAM into `GetRamUsage()` (GlobalMemoryStatusEx only) and `GetRamSpeedInfo()` (WMI, called once in the full report). `GetQuickTelemetry` uses only the former.
- **How to verify:** Open the System tab and drag the window; no periodic hitch.

### M-07 Startup probes hardware and tweak state synchronously, including spawning `powercfg.exe` on the UI thread
- [x] Status: Fixed | Resolution: Removed `LoadTweaks()` and `_ = LoadHardwareSpecsAsync()` from `SystemViewModel`'s constructor entirely - hardware specs already had a "first visit" lazy-load in `MainViewModel.CurrentSection`'s setter (`if (SystemVM.Report.Drives.Count == 0) _ = SystemVM.LoadHardwareSpecsAsync();`); added the same pattern for tweaks (`if (SystemVM.TotalTweakCount == 0) _ = SystemVM.LoadTweaksAsync();`). Converted `LoadTweaks()` (private, synchronous) to `LoadTweaksAsync()` (public, `await Task.Run(() => _tweaksService.GetAllTweaks())` then populate `Tweaks` on the UI thread), mirroring how `LoadHardwareSpecsAsync()` already wraps `GetFullHardwareReportAsync()`. Now neither the ~20 registry reads nor the `GetActivePlanFriendlyName`/`CheckUltimatePlanActive` `powercfg.exe` fallbacks nor the hardware/network probe run at all until the System tab is opened, and when they do run it's off the UI thread. Did **not** implement the finding's secondary suggestion (resolving `@dll,-id` resource strings via `SHLoadIndirectString` instead of calling `powercfg`) - the `Task.Run` move already stops it from blocking the UI thread, and the `SHLoadIndirectString` rewrite is a separate, riskier native-interop change not needed to fix the stated defect (startup blocking). Confirmed only the Refresh/"Check Now" button (user-initiated, not startup) still calls `GetAllTweaks()` synchronously - left as-is since it's out of scope for a startup finding. Release build: 0 warnings, 0 errors. Verified by launching the real built exe with `--minimized` (backed up nothing, no data touched) and watching `debug.log`: no hardware/tweak/powercfg-related lines appeared in the ~6s after startup, and the process stayed alive and idle in the tray (force-killed afterward since there's no way to click Exit from this session). Could not independently instrument that zero `powercfg.exe` processes were spawned (no process-creation auditing available here) - relying on code tracing (the only call sites are now behind the System-tab-visit gate) plus the clean log as corroboration.
- **Confidence:** CONFIRMED
- **Category:** performance / startup
- **Files:** `ViewModels/SystemViewModel.cs:311-314` (ctor runs LoadTweaks + LoadHardwareSpecsAsync), `ViewModels/MainViewModel.cs:210`, `Services/SystemTweaksService.cs:76-344` (GetAllTweaks), `984-1019` (GetActivePlanFriendlyName), `554-584` (CheckUltimatePlanActive), `Services/SystemInfoService.cs:728-760` (ping 8.8.8.8 in the report)
- **What is wrong:** `SystemViewModel` is constructed inside `MainViewModel`'s constructor at every launch, even with `--minimized`. `LoadTweaks` reads ~20 registry keys synchronously, and for the built-in Balanced plan the `FriendlyName` starts with `@` so `GetActivePlanFriendlyName` falls through to `RunPowercfg`, spawning a process and blocking up to 5 s; `CheckUltimatePlanActive` can spawn a second one. The hardware report (WMI for CPU, RAM, disks, baseboard, BIOS, plus a network ping) also starts at every launch although it runs on a worker.
- **Suggested fix:** Remove the two calls from the constructor; the `CurrentSection` setter already loads specs on first visit. Load tweaks the same way (first visit, inside `Task.Run`, marshal results back). Resolve `@dll,-id` resource strings with `SHLoadIndirectString` instead of calling powercfg.
- **How to verify:** Launch with `--minimized`; `debug.log` shows no hardware/tweak activity and no `powercfg` spawn until the System tab is opened.

### M-08 Library load decodes every icon and cover and calls `File.Exists` per game synchronously during startup
- [x] Status: Fixed | Resolution: Added a `deferHeavyInit` constructor parameter to `GameCardViewModel` (default `false`, so every other call site - imports, undo, folder scan, etc. - is unaffected) that skips the constructor's `CheckIsMissing()`/`ReloadIcon()`/`ReloadCover()` calls. Added `ComputeHeavyState()` (returns the missing flag + both decoded, already-frozen `BitmapImage`s - safe off the UI thread since `File.Exists` is thread-safe and `IconExtractorService.LoadBitmapSafely` freezes what it returns) and `ApplyHeavyState(...)` (sets the three UI-bound properties - must run on the UI thread). `MainViewModel.LoadLibrary()` now builds all cards with `deferHeavyInit: true`, then calls a new `LoadCardHeavyStateInBackground()` that snapshots the cards, computes every card's heavy state on `Task.Run`, and applies all the results in one dispatcher hop - so the constructor loop that used to block the UI thread for the whole library now returns immediately and the window can show while artwork/missing-status streams in behind it. Release build: 0 warnings, 0 errors. Verified with a real before/after timing test: backed up the real library, swapped in a synthetic 150-game `games.json` (each entry pointing at a real icon PNG and a real 175 KB cover JPG so genuine decode work happens), launched `--minimized`, and read `debug.log`'s "Initializing ViewModel" -> "Initializing MainWindow" gap - **606 ms before the fix** (`git stash` of this change, rebuild, run) vs **73 ms after** (fix restored, rebuild, run) for the same 150-game library. Real AppData was fully restored afterward and the synthetic files deleted. Did not verify with an actually-slow/disconnected drive (the finding's worst case), since I couldn't safely simulate that here.
- **Confidence:** CONFIRMED
- **Category:** performance / startup
- **Files:** `ViewModels/MainViewModel.cs:696-715` (LoadLibrary), `ViewModels/GameCardViewModel.cs:92-94,121-124,185-196`
- **What is wrong:** For every game the card constructor runs `File.Exists` (can block for seconds on a disconnected network or sleeping drive) and decodes two bitmaps on the UI thread. Startup time grows linearly with library size and the cloaked main window stays hidden for the whole duration.
- **Suggested fix:** Make `IconImage`/`CoverImage` lazy: decode on first get via `Task.Run` + `Dispatcher` set, or use a shared background loader queue. Move `CheckIsMissing` for all cards into one `Task.Run` after load and apply results on the dispatcher.
- **How to verify:** Time from process start to window visible with 100+ games before and after.

### M-09 `RefreshProperties()` reloads both bitmaps from disk every time it is called
- [x] Status: Fixed | Resolution: `ReloadIcon()`/`ReloadCover()` now track the last-loaded path and `File.GetLastWriteTimeUtc` and skip re-decoding when both are unchanged, so `RefreshProperties()` (called on launch, playtime updates, and enrichment) is a near no-op for the common case where the file hasn't actually changed. Kept the same method names/call sites (`RefreshProperties` still calls `ReloadIcon`/`ReloadCover` unconditionally) rather than adding a separate "force reload" method, since the poster-refresh and icon-fetch flows always overwrite the cached file in place at the same fixed path (`Covers/{appId}.jpg`, `Icons/{id}.png`) - a real refresh changes the write time and naturally still triggers a reload, while a launch/playtime tick that touched nothing correctly skips it. Extended `ComputeHeavyState`/`ApplyHeavyState` (added for M-08) to compute and record the same write-time tracking off the UI thread, so the startup batch-load doesn't leave a "cold" cache that forces every card to reload again the moment enrichment calls `RefreshProperties()` shortly after launch. Release build: 0 warnings, 0 errors. Verified with a harness against the built DLL (real `GameCardViewModel`, no GUI): calling `ReloadIcon()` again with the file untouched returned the exact same `BitmapImage` instance (no re-decode); after changing the file's last-write time, the next `ReloadIcon()` call produced a new instance (real reload) - both as intended, in place of the finding's suggested "temporary log line" check.
- **Confidence:** CONFIRMED
- **Category:** performance
- **Files:** `ViewModels/GameCardViewModel.cs:198-219`; callers throughout `MainViewModel.cs` (launch, playtime update, poster refresh loop, enrichment)
- **What is wrong:** A launch or a playtime tick re-decodes the 368 px cover and icon even though nothing changed.
- **Suggested fix:** Track the last loaded path + `File.GetLastWriteTimeUtc` per image and skip reload when unchanged; keep an explicit `ReloadCover()` for callers that know the file changed (poster refresh).
- **How to verify:** Add a temporary log line in `ReloadCover`; launch a game; the line must not appear.

### M-10 `SaveLibrary()` writes two files synchronously on the UI thread and triggers a full tray-menu rebuild, often twice per action
- [ ] Status: Open | Resolution:
- **Confidence:** CONFIRMED
- **Category:** performance
- **Files:** `ViewModels/MainViewModel.cs:2111-2116` (SaveLibrary fires LibraryUpdated), `1366-1423` (DeleteGame fires it again at 1422), `1425-1453`, `1498-1624`, `App.xaml.cs:349-555` (UpdateTrayContextMenu rebuilds every MenuItem and icon)
- **What is wrong:** Every save serialises the whole library and settings and then rebuilds the entire tray menu (all icons, all submenus). Most callers also invoke `LibraryUpdated` explicitly right after `SaveLibrary`, so the menu is built twice.
- **Suggested fix:** Remove `LibraryUpdated?.Invoke()` from `SaveLibrary` (callers already raise it) **or** remove the explicit calls, not both. Debounce the tray rebuild in `App` with a 300 ms `DispatcherTimer`. Consider writing the JSON on a background thread (serialise on UI, write via `Task.Run`, keep the existing lock).
- **How to verify:** Add a counter log in `UpdateTrayContextMenu`; deleting one game must rebuild once.

### M-11 Games with no Steam match are re-searched online at every startup, forever
- [ ] Status: Open | Resolution:
- **Confidence:** CONFIRMED
- **Category:** performance / network
- **Files:** `ViewModels/MainViewModel.cs:1281-1341` (candidate filter includes any card with empty `SteamAppId`), `1126-1182`
- **What is wrong:** Non-Steam games that legitimately never match (indie, emulators, tools) qualify as enrichment candidates on every launch, costing two Steam searches each, every time.
- **Suggested fix:** Add `DateTime? LastEnrichmentAttemptUtc` to `GameEntry`; set it after an attempt; skip candidates attempted within the last 7 days unless the user explicitly refreshes.
- **How to verify:** Add a non-Steam exe, restart twice; `debug.log` shows the search only once.

### M-12 Folder scanning runs synchronously on the UI thread
- [ ] Status: Open | Resolution:
- **Confidence:** CONFIRMED
- **Category:** performance
- **Files:** `ViewModels/MainViewModel.cs:1672-1676,1630-1649`, `Services/FolderScannerService.cs:105-246`
- **What is wrong:** `ScanFolderOrLibrary` scans every subfolder to depth 5, reading `FileVersionInfo`, PE headers and file sizes for each exe, and it runs on the dispatcher. Dropping a large library folder freezes the window for many seconds.
- **Suggested fix:** Wrap the scan in `Task.Run`, show `StatusMessage = "Scanning..."` and a busy indicator, then continue on the UI thread with the result.
- **How to verify:** Drop `C:\Games` with 50+ subfolders; the window stays responsive.

### M-13 `Process.Exited` is subscribed after `EnableRaisingEvents`, so fast-exiting launchers miss the handler and leak the Process
- [ ] Status: Open | Resolution:
- **Confidence:** CONFIRMED
- **Category:** correctness / resource leak
- **Files:** `Services/ProcessLauncherService.cs:184-206`
- **What is wrong:** If the process exits between `EnableRaisingEvents = true` and `process.Exited +=`, the event fires with no subscriber, the `Dispose()` in the handler never runs, and playtime is not recorded.
- **Suggested fix:** Subscribe to `Exited` first, then set `EnableRaisingEvents = true`. After that, if `process.HasExited` is already true, handle it inline.
- **How to verify:** Launch a stub exe that exits immediately; no leaked handle, log shows the session-ended line.

### M-14 HTTP timeouts are too short for image and installer downloads
- [ ] Status: Open | Resolution:
- **Confidence:** CONFIRMED for posters / PLAUSIBLE for the installer download
- **Category:** robustness
- **Files:** `Services/SteamMetadataService.cs:21-28,405-470`, `Services/SteamGridDbService.cs:19-26`, `Services/UpdateService.cs:63-66,165-192`
- **What is wrong:** The shared `HttpClient.Timeout` of 5 s covers a 600x900 2x poster download; on a slow link it fails silently, falls to a lower-quality tier, and that result is cached and never revisited. `UpdateService` uses a 10 s timeout on the same client that streams a ~60 MB installer with `ResponseHeadersRead`; on some runtimes the timeout CTS also governs the content stream, which would abort slow downloads with "task canceled".
- **Suggested fix:** Keep short timeouts for JSON calls but use per-request `CancellationTokenSource(TimeSpan)` instead of `HttpClient.Timeout`; for image and installer downloads use 30-60 s (or `Timeout.InfiniteTimeSpan` on a dedicated client and rely on the passed token).
- **How to verify:** Throttle the network to 1 Mbit/s; posters still arrive as official art; the update download completes.

### M-15 `.url` shortcuts to non-Steam launchers (Epic, GOG Galaxy, Ubisoft) are accepted but can never launch
- [ ] Status: Open | Resolution:
- **Confidence:** CONFIRMED
- **Category:** correctness
- **Files:** `Services/ShortcutService.cs:113-161`, `Services/ProcessLauncherService.cs:69-98`, `ViewModels/MainViewModel.cs:1569-1578`
- **What is wrong:** The file filter accepts `*.url`. For a non-`steam://` URL the resolution stores the URL as `ExecutablePath` with `IsSteamGame=false`; the launcher then does `File.Exists(url)`, marks the game missing, and offers "Locate...".
- **Suggested fix:** In `ProcessLauncherService.LaunchGame`, if `Uri.TryCreate(ExecutablePath, Absolute)` succeeds with a non-file scheme, launch it with `UseShellExecute = true` (same branch as Steam) and skip the exists check. `GameCardViewModel.CheckIsMissing` needs the same exemption.
- **How to verify:** Drop an Epic Games desktop shortcut; it launches instead of reporting missing.

### M-16 Icons are extracted at 32x32, then displayed at 64 px and in the tray
- [ ] Status: Open | Resolution:
- **Confidence:** CONFIRMED
- **Category:** quality
- **Files:** `Services/IconExtractorService.cs:45-68`
- **What is wrong:** `new Icon(path)` and `Icon.ExtractAssociatedIcon` return the default small frame. The cached PNG is upscaled everywhere, so icons look blurry in Compact Icons view.
- **Suggested fix:** Extract the largest frame: for `.ico` iterate frames via `new Icon(path, new Size(256,256))`; for executables use `SHDefExtractIcon` with `nIconSize = 256` (or `IShellItemImageFactory::GetImage`). Save at native size, let `DecodePixelWidth` downscale.
- **How to verify:** Compare a known 256 px game icon in Compact Icons view before and after.

### M-17 Background update check can pop a modal dialog during gameplay, and it re-prompts daily with no snooze
- [ ] Status: Open | Resolution:
- **Confidence:** CONFIRMED
- **Category:** UX
- **Files:** `ViewModels/MainViewModel.cs:295-318,484-490`, `Views/UpdateDialog.xaml.cs:163-174`
- **What is wrong:** The 24 h timer calls `UpdateDialog.ShowUpdateDialog` even when the main window is hidden. The dialog is owner-less, centred on screen and calls `Activate()`, stealing focus from a running full-screen game. "Remind later" is forgotten the next day.
- **Suggested fix:** From the background path, only show the dialog when the main window is visible; otherwise update the badge and raise a tray balloon (`TaskbarIcon.ShowNotification`). Persist `SkippedUpdateVersion` / `RemindAfterUtc` in settings and honour them.
- **How to verify:** Publish a fake newer release in a test repo, start minimised; no window appears, the tray notification does.

### M-18 "Start with Windows" is never reconciled with the registry
- [ ] Status: Open | Resolution:
- **Confidence:** CONFIRMED
- **Category:** correctness
- **Files:** `ViewModels/SettingsViewModel.cs:220-234`, `Services/StartupManager.cs:13-77` (`IsStartupEnabled` and `IsStartupMinimized` are never called; `OpenSubKey(...,true)` returning null is a silent no-op)
- **What is wrong:** The toggle reflects `settings.json`, not reality. Disabling the entry in Task Manager, a portable copy moved to another folder, or a failed write all leave the UI wrong. Task Manager disables via `StartupApproved\Run`, which is not checked at all.
- **Suggested fix:** On startup (and when the Settings page opens) set `_settings.StartWithWindows` from `IsStartupEnabled()` combined with the `HKCU\...\Explorer\StartupApproved\Run` byte flag; use `CreateSubKey` for the Run key; when enabled and the stored path differs from `Environment.ProcessPath`, rewrite it.
- **How to verify:** Disable TrayTrigger in Task Manager > Startup, reopen Settings; the toggle shows off.

### M-19 Title normalisation maps `X` to `10` and `I` to `1`, so distinct games collide
- [ ] Status: Open | Resolution:
- **Confidence:** CONFIRMED
- **Category:** correctness (matching heuristic)
- **Files:** `Services/SteamSearchService.cs:422-450`
- **What is wrong:** `NormalizeForMatching` rewrites any standalone `x` as `10` and `i` as `1`. "Mega Man X" and "Mega Man 10" normalise identically and score 1.0; "I Am Alive" becomes "1 am alive". Because both sides are normalised the same way, wrong candidates can outscore the right one.
- **Suggested fix:** Only convert roman numerals `II`-`IX` (two or more letters) and treat single `I`/`X` as ambiguous: leave them alone, or convert only when the token is the final token and the other string has a digit in the same position.
- **How to verify:** Unit test: `CalculateSimilarity("Mega Man X", "Mega Man 10") < 0.9`.

### M-20 `IsAcceptableTitle` rejects real game titles containing "microsoft", "windows", "unreal" or "engine"
- [ ] Status: Open | Resolution:
- **Confidence:** CONFIRMED
- **Category:** correctness (naming heuristic)
- **Files:** `Services/GameNameExtractor.cs:21-26,291-306`
- **What is wrong:** The `GenericKeywords` substring check throws away the exe's own `FileDescription` for titles such as "Microsoft Flight Simulator" or "Unreal Tournament", falling back to a worse stem-based name.
- **Suggested fix:** Apply the keyword rejection only when the title has 2 words or fewer, or when it also contains a utility word (setup, redist, prereq, crash, updater). Add unit tests for the two examples.
- **How to verify:** `IsAcceptableTitle("Microsoft Flight Simulator")` returns true; `IsAcceptableTitle("Microsoft Visual C++ Redistributable")` returns false.

### M-21 `CleanStaleRegistrations` deletes the tray registration of any other TrayTrigger copy
- [ ] Status: Open | Resolution:
- **Confidence:** CONFIRMED
- **Category:** robustness
- **Files:** `Services/TrayPromotionService.cs:17-67` (called at every startup and on every `TrySetAlwaysShow`)
- **What is wrong:** Matching is by tooltip text or exe name, so a portable copy and an installed copy repeatedly delete each other's `NotifyIconSettings` entry, including the "always show" promotion the user set.
- **Suggested fix:** Only delete entries whose `ExecutablePath` no longer exists on disk. Leave other live installs alone.
- **How to verify:** Run the installed copy, then the portable copy; the installed copy's promotion survives.

### M-22 Opening Game Details silently binds a fuzzy Steam match to the game and persists it
- [ ] Status: Open | Resolution:
- **Confidence:** CONFIRMED
- **Category:** correctness
- **Files:** `ViewModels/GameDetailsViewModel.cs:277-286`, `ViewModels/MainViewModel.cs:780-783` (saves after the dialog closes)
- **What is wrong:** For a game with no AppId, merely opening the details dialog runs `FindBestMatchAsync` at the configured threshold (default 0.60) and writes `Game.SteamAppId`. A 60 % match is often wrong, and from then on the wrong poster and genre stick.
- **Suggested fix:** Require the "decisive" threshold used elsewhere (`>= 0.85`) before persisting from this path; otherwise show the match in the dialog with a "Use this match" button.
- **How to verify:** Open details for a game whose best match scores between 0.6 and 0.85; `SteamAppId` stays null.

### M-23 Steam import can pick a multi-megabyte hero image as the game "icon"
- [ ] Status: Open | Resolution:
- **Confidence:** CONFIRMED
- **Category:** quality / disk usage
- **Files:** `Services/SteamScannerService.cs:210-241` (falls back to `*.*` in the `librarycache\{appId}` folder), `Services/IconExtractorService.cs:53-59` (converts it to a full-size PNG under `Icons`)
- **What is wrong:** `subIcons[0]` from `*.*` may be `library_hero.jpg` (3840 px). It is re-encoded as a large PNG in the icons cache and used as a 24 px tray icon.
- **Suggested fix:** Restrict candidates to names containing `icon` or `logo`, or `.ico`; if none, fall back to the exe. In `ExtractAndCacheIcon`, downscale any raster source larger than 256 px before saving.
- **How to verify:** Import a Steam game; `%LocalAppData%\TrayTrigger\Icons\{id}.png` is <= 256 px.

### M-24 Steam import decodes every game icon synchronously on the UI thread
- [ ] Status: Open | Resolution:
- **Confidence:** CONFIRMED
- **Category:** performance
- **Files:** `ViewModels/SteamImportViewModel.cs:20-25,106-132`
- **What is wrong:** `SteamImportItemViewModel` calls `LoadBitmapSafely` in its constructor, inside the UI-thread `foreach` after the scan. Large Steam libraries make the dialog hang after "Scanning...".
- **Suggested fix:** Mirror `BatchGameItemViewModel`: load the icon in `Task.Run` and set the property via the dispatcher.
- **How to verify:** Import dialog with 100+ games populates without a freeze.

---

## Low

### L-01 Logging and storage are initialised twice at startup
- [ ] Status: Open | Resolution:
- **Files:** `App.xaml.cs:61-71,215`, `ViewModels/MainViewModel.cs:162,174`
- **What:** A throwaway `StorageService` (which also runs legacy migration) and `LoggingService.Initialize` run in `App`, then again in `MainViewModel`. Pass the first instance into the ViewModel and drop the second `Initialize`.

### L-02 Tray icon resource stream is never disposed
- [ ] Status: Open | Resolution:
- **Files:** `App.xaml.cs:286-299`
- **What:** `new System.Drawing.Icon(resInfo.Stream)` copies the data; wrap the stream in `using`.

### L-03 `RunPowercfg` redirects stderr but never reads it
- [ ] Status: Open | Resolution:
- **Files:** `Services/SystemTweaksService.cs:1155-1180`
- **What:** If stderr fills its pipe buffer the child blocks and `WaitForExit(5000)` times out. Either read stderr asynchronously (`BeginErrorReadLine`) or set `RedirectStandardError = false`.

### L-04 Nagle "revert" writes `0` rather than removing the values
- [ ] Status: Open | Resolution:
- **Confidence:** PLAUSIBLE (`TcpAckFrequency=0` is outside the documented 1-255 range)
- **Files:** `Services/SystemTweaksService.cs:1255-1284`
- **What:** Delete `TcpAckFrequency` and `TCPNoDelay` on revert. Overlaps with H-04; can be done together.

### L-05 Log writer opens and closes the file per line and only rotates at startup
- [ ] Status: Open | Resolution:
- **Files:** `Services/LoggingService.cs:61-92,115-140`
- **What:** With verbose logging the scan/enrichment paths write hundreds of lines; each is a full open/append/close under a lock. Keep a `StreamWriter` with `AutoFlush`, and check size for rotation every N writes.

### L-06 SteamGridDB API key is stored in plain text
- [ ] Status: Open | Resolution:
- **Files:** `Models/AppSettings.cs:26`, `ViewModels/SettingsViewModel.cs:653-665`
- **What:** Protect with `ProtectedData.Protect(..., DataProtectionScope.CurrentUser)` and store base64; decrypt on load; keep a migration path for existing plain values.

### L-07 `async void` command lambdas let exceptions escape to the global handler
- [ ] Status: Open | Resolution:
- **Files:** `ViewModels/MainViewModel.cs:275,282`, `ViewModels/SettingsViewModel.cs:200-215`, `ViewModels/SystemViewModel.cs:293`
- **What:** `new RelayCommand(async () => await X())` compiles to `async void`. Add an `AsyncRelayCommand` that awaits inside a try/catch and logs, or wrap each body.

### L-08 Dead code and an unreachable feature
- [ ] Status: Open | Resolution:
- **Files:** `App.xaml.cs:327-347` (`OpenSearchFlyout` has no caller, so `Views/TraySearchFlyout.*` is unreachable), `Services/TrayPromotionService.cs:167-197` (`IsCurrentlyPromoted`), `Services/GameNameExtractor.cs:234-245` (`ResolveGameNameAsync`), `Services/SteamScannerService.cs:265-268` (`LaunchGame`), `ViewModels/MainViewModel.cs:786-803` (`LaunchGameEntry`), `Services/StartupManager.cs:13-47` (see M-18 before deleting), `Views/TraySearchFlyout.xaml.cs:121-125` (`SetSearchQuery`), `App.xaml.cs:125-132` (class-level `ApplyDarkTitleBar` handler is redundant now that every window calls `PrepareForFirstShow`).
- **What:** Either wire the search flyout to a tray action / hotkey (it looks finished) or remove it. Delete the rest. Decide with the owner before removing the flyout: **Needs human decision** on that one item only.

### L-09 Restart prompt gives a 10 s countdown with no abort hint
- [ ] Status: Open | Resolution:
- **Files:** `ViewModels/SystemViewModel.cs:119-129`
- **What:** Use `shutdown /r /t 30 /c "TrayTrigger: restarting to apply performance settings"` and mention `shutdown /a` in the confirmation text.

### L-10 Two separate update-check implementations
- [ ] Status: Open | Resolution:
- **Files:** `ViewModels/MainViewModel.cs:461-558`, `ViewModels/SettingsViewModel.cs:334-375`
- **What:** Same API call, different status strings and dialogs. Keep one (`MainViewModel`), have Settings bind to its status properties.

### L-11 Duplicated heuristic tables
- [ ] Status: Open | Resolution:
- **Files:** `Services/SteamSearchService.cs:37-69`, `Services/GameNameExtractor.cs:38-78`
- **What:** `EditionPhrases`, `CommonSpellingCorrections`, release-group lists exist twice and have already diverged (`GOG`/`Steam` only in one). Move to one `static class TitleHeuristics`.

### L-12 Import pipeline is implemented four times
- [ ] Status: Open | Resolution:
- **Files:** `ViewModels/MainViewModel.cs:1498-1624,1736-1847,1851-1931,1996-2089`
- **What:** "resolve name -> new GameEntry -> extract icon -> enrich -> add -> rebuild/save/hotkeys/sort/status" is copy-pasted. Extract `Task<GameEntry> BuildEntryAsync(...)` and `void CommitAdded(IEnumerable<GameEntry>, string status)`. Do this **after** M-10/M-11 so the fix lands once.

### L-13 `MainViewModel` (2246 lines) and `MainWindow.xaml` (2336 lines) are god objects
- [ ] Status: Open | Resolution:
- **Files:** `ViewModels/MainViewModel.cs`, `MainWindow.xaml`
- **What:** Split the XAML into `LibraryView`, `SettingsView`, `AboutView` user controls (SystemView already is one). Split the ViewModel into `LibraryViewModel` (CRUD/filter/sort), `ImportCoordinator` (drops, folders, Steam import, enrichment) and `UpdateCoordinator`. Large change; schedule after the functional fixes.

### L-14 Thirty pass-through settings properties
- [ ] Status: Open | Resolution:
- **Files:** `ViewModels/MainViewModel.cs:591-613`
- **What:** Bind XAML to `SettingsVM.X` directly and delete the forwarders (check `App.DevDiagnostics.cs` for DEBUG usages first).

### L-15 Magic strings for categories and sort options
- [ ] Status: Open | Resolution:
- **Files:** throughout `MainViewModel.cs`, `App.xaml.cs:442-451`, `SettingsViewModel.cs:88-94`, `GameEditViewModel.cs`
- **What:** `"Uncategorized"`, `"Steam"`, `"Favorites"`, `"All"` and the sort option labels are compared as literals in many places. Centralise in a `static class LibraryConstants`; consider an enum for sort options with a display-name converter.

### L-16 Owner-window lookup expression repeated 15+ times
- [ ] Status: Open | Resolution:
- **Files:** `MainViewModel.cs`, `SettingsViewModel.cs`, `ModernDialog.cs`, `UpdateDialog.cs`
- **What:** `Application.Current?.MainWindow is { IsVisible: true } w ? w : null` -> `WindowHelper.ActiveOwner()`.

### L-17 Empty setters on computed display properties
- [ ] Status: Open | Resolution:
- **Files:** `Models/HardwareModels.cs` (`ClockSpeedDisplay`, `SpeedDisplay`)
- **What:** `set { }` hides binding mistakes; make them get-only.

### L-18 Details dialog cover image is decoded on every property read, and the remote fallback can never work
- [ ] Status: Open | Resolution:
- **Files:** `ViewModels/GameDetailsViewModel.cs:123-157`
- **What:** `DisplayCoverImage` decodes the file each time WPF reads it. The `HeaderImageUrl` branch calls `Freeze()` on a `BitmapImage` that is still downloading, which throws and is swallowed, so the fallback returns null. Cache the decoded image in a field; for the remote case either skip `Freeze()` or download bytes via `HttpClient` first.

### L-19 No automated tests
- [ ] Status: Open | Resolution:
- **What:** Add `TrayTrigger.Tests` (xUnit). Pure functions worth covering first: `HotkeyManager.ParseHotkey`, `GitHubReleaseInfo.ParsedVersion`, `SteamSearchService.CalculateSimilarity/NormalizeForMatching/SanitizeSearchQuery`, `GameNameExtractor.CleanFolderName/CleanExecutableStem/IsAcceptableTitle`, `SteamMetadataService.CleanHtmlText` (make internal + `InternalsVisibleTo`), `FolderScannerService.IsDisqualified`. Several findings above include test cases.

### L-20 Inconsistent fallback for tray item counts
- [ ] Status: Open | Resolution:
- **Files:** `App.xaml.cs:374,410`, `ViewModels/SettingsViewModel.cs:396,441`, `Models/AppSettings.cs:18,21`
- **What:** Defaults are 5, fallbacks are 3 or 5 depending on the file. Use one constant.

### L-21 Shell-link buffers are fixed at 260 characters
- [ ] Status: Open | Resolution:
- **Files:** `Services/ShortcutService.cs:172-185`
- **What:** Long-path targets are truncated silently. Use 1024 (or `32767`) for path buffers.

### L-22 `GetOsInfo` unboxes a registry value without a type check
- [ ] Status: Open | Resolution:
- **Files:** `Services/SystemInfoService.cs:616-621`
- **What:** `(int)val` throws for a non-DWORD value and the outer catch abandons power plan and boot time. Use `val is int i && i != 0`.

### L-23 Settings defaults exist in two places
- [ ] Status: Open | Resolution:
- **Files:** `ViewModels/SettingsViewModel.cs:797-823`, `Models/AppSettings.cs`
- **What:** `ResetSettingsToDefaults` re-lists every default. Build `var d = new AppSettings();` and copy fields (preserving the API key), so a new setting cannot be forgotten.

### L-24 Global dispatcher exception handler swallows everything silently
- [ ] Status: Open | Resolution:
- **Files:** `App.xaml.cs:98-123`
- **What:** Keeping the tray app alive is right, but a swallowed exception is invisible to the user. Show a non-modal toast / status message ("An error was logged") so bugs get reported.

### L-25 Delisted or region-locked Steam apps show "check internet connection"
- [ ] Status: Open | Resolution:
- **Files:** `Services/SteamMetadataService.cs:140-147`, `ViewModels/GameDetailsViewModel.cs:321-324`
- **What:** `appdetails` returns HTTP 200 with `success:false` for those; the details are null and the UI blames the network. Distinguish "Steam has no store data for this AppId" from a transport error.

---

## Verified OK - do not change

These were examined and found correct. Follow-up models should not "fix" them.

- `StorageService.SaveGames/SaveSettings` temp-file + move pattern and the `Lock` reentrancy (`RemapLegacyPaths` calling `SaveGames` under the same lock is fine; `System.Threading.Lock` is reentrant).
- `WindowThemeService.PrepareForFirstShow` cloak/uncloak sequence and the reflection-based render flush (added in v1.0.7); the fallback timer and idempotency guards are intentional.
- `GameDetailsViewModel.ExecuteOpenUrl` scheme whitelist.
- `ProcessLauncherService.IsSameExecutable` returning `true` on `MainModule` access failure: documented, deliberate trade-off.
- `SteamMetadataService.GetAppDetailsAsync` per-AppId semaphore and "only cache successful lookups" rule.
- `MainWindow.Window_Drop` deferring `HandleFileDrop` via `Dispatcher.BeginInvoke` (documented WPF reentrancy workaround) and `DropZone_Drop` setting `e.Handled`.
- `HandleFileDropAsync` processing folders outside the `_isImportInProgress` guard (documented, required so nested calls are not rejected).
- `SystemTweaksService.SetVisualFx` SPI parameter marshalling (`SPI_SETDROPSHADOW` takes the BOOL as the pointer value; `SPI_GETDROPSHADOW` takes a real pointer).
- `.gitignore` coverage: `.bak`, `test_*.png`, `*original*.txt`, `publish/` are not tracked (76 tracked files, all source/assets).

## Not reviewed

- `App.DevDiagnostics.cs` (DEBUG only, excluded by request).
- Visual styling in `App.xaml`, `MainWindow.xaml`, `Views/*.xaml` beyond bindings and commands.
- `setup.iss` Pascal script and `.github/workflows/release.yml` beyond confirming the local build steps match.
