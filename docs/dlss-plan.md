# DLSS Override

Built and verified on hardware 2026-09-19/20, then cut back the same day. This describes the
feature as it is. What changed along the way, and why, is in **Corrections** and **Simplified** at
the end - worth reading before changing anything here, because most of it was learned the expensive
way.

## The design rule

**Size the machinery to what happens when it fails.** If the override does not take, the game runs
its own DLSS file, as it always did. So nothing here guards against "it did not work". What is
guarded is the one thing that is not benign: a setting left in NVIDIA's driver with nothing able to
take it out again. Easy to turn on, easy to remove, and exact when removed.

## What it does

An **NVIDIA DLSS Override** card in Edit Game, on the Performance tab, that puts a game on the DLSS
files installed with the GeForce driver instead of the older ones inside the game.

```
NVIDIA DLSS Override                                          [ Restore ]

  Runs this game on the DLSS files installed with your GeForce driver,
  instead of the older ones inside the game. Nothing in the game folder
  changes.

  [x] Enable DLSS Override for this game            310.1.0 -> 310.9.0
      Applies to game-supported DLSS features: Super Resolution, Ray
      Reconstruction, and Frame Generation.
      Last run: loaded 310.9.0 from NVIDIA.

  Learn more
```

- **The switch is the status.** On means TrayTrigger holds an override for this game: it was
  ticked and has not been unticked or restored. It does not re-read the driver - NVIDIA App turns
  Frame Generation off for a game that has none (Doom Eternal), and that is not the user switching
  the override off. Last run says what actually loaded. There is no confirmation text when a
  change works. A single line in the warning colour appears only when a change did not work,
  because then the switch drops back and something has to say why.
- **The version pair** is the oldest version the game ships and the newest the driver holds *for
  that same feature*. It is a prediction, not a reading.
- **Last run** is the reading: what the game actually loaded the last time it ran with the override
  on. On a line of its own, and "N/A" until the game has been played - always there, so a
  missing line is never mistaken for a fault.
- A game that ships no DLSS DLL keeps the card, with the switch greyed out and "No DLSS files found in this game" where the versions would be, so a missing card is never mistaken for a fault. On a PC with no NVIDIA driver the card is hidden.

Two things are not per game, and live on the **NVIDIA DLSS** card in Settings > Launch &
Performance (hidden on a PC with no NVIDIA driver):

- **DLSS Override: On for N games**, with **Restore All**.
- **Show the NVIDIA DLSS Indicator in games** - NVIDIA's on-screen overlay, behind a UAC prompt.

They were first rows on the System page, among the Performance Tweaks. Neither is a tweak - one
only holds a button, the other is a debug overlay - and an informational row's ON badge read as a
system-wide switch for the override.

Help: `Help/dlss/override.md` (linked from both cards) and `Help/dlss/indicator.md`, in a Help
section of their own.

**The wording rule:** keep the terms NVIDIA itself prints - *DLSS Override*, *DLSS Indicator*,
*DLSS Preset*, the three feature names, and *GeForce driver* - because they are what a user
recognises from NVIDIA App and what they can search. Drop the generic nouns that only sound
official: *runtime*, *libraries*, *HUD*, *model preset*, and *driver* used as a bare noun for two
different things.

It works entirely through the NVIDIA driver's per-game profile database. **No file in any game
folder is written, ever.** That single decision removes downloads, caches, backups, the NVIDIA
redistribution question, collisions with Steam's "verify integrity", file locks, and the whole
class of failures caused by replacing a game's files - everything DLSS Swapper and RHI carry.

The promise that is strictly true, and the only one worth making, is **"TrayTrigger does not modify
game files."** Everything stronger is a probability.

## How it works

The driver keeps its own current DLSS runtimes in `C:\ProgramData\NVIDIA\NGX\models\`, signed and
up to date. Six settings in NVIDIA's driver settings database (DRS) tell it to load those instead
of the copy the game shipped:

| Setting | Value | |
|---|---|---|
| `0x10E41E01` | `1` | DLSS - enable DLL override |
| `0x10E41DF3` | `0x00FFFFFF` | DLSS - forced preset letter (`0x00FFFFFF` = NVIDIA's recommended) |
| `0x10E41E02` | `1` | DLSS-RR - enable DLL override |
| `0x10E41DF7` | `0x00FFFFFF` | DLSS-RR - forced preset letter |
| `0x10E41E03` | `1` | DLSS-FG - enable DLL override |
| `0x10E41DF1` | `0x00FFFFFE` | DLSS-FG - forced preset letter (**different sentinel**) |

`nvngx_config.txt` in the model store maps NGX app ids to versions, and the override pseudo-app
`app_E658700` has an explicit entry per feature. TrayTrigger does not read it - the newest version
folder per feature is what the card shows - but it is the file to look at if the two ever disagree.

### Nothing needs elevation

Reads and writes both work unelevated, verified at a trust level below the filtered token a
UAC-enabled administrator account gets. The card reads, applies and undoes with **no UAC prompt**.
The one exception is the Indicator, which writes HKLM.

### The layers

DRS resolves a setting through application profile → global profile → base profile → driver
default. On the development machine four DLSS settings already sat on the **Global** profile,
written by another tool. Two consequences:

- A game on such a PC gets the driver's DLSS whether the per-game switch is on or off. TrayTrigger
  leaves settings it did not write alone; the Help topic says so.
- The capture has to record *where* a previous value came from, not just the number - see the
  ownership record.

The card does not read the Global profile. `--test-dlss` does.

## The code

| | |
|---|---|
| `Services/NvApi.cs` | NVAPI DRS interop. No import library exists, so every entry point comes through `nvapi_QueryInterface` by published id. Struct versions are computed as `sizeof \| (ver << 16)` rather than hard-coded, so a layout mistake cannot pass the driver a plausible-looking number. `nvapi64.dll` is loaded from System32 only. `Session.LastStatus` is how "not found" is told from a failure. |
| `Services/NvApi.Diagnostics.cs` | DEBUG only: the Global profile and setting enumeration, which nothing but the report reads. |
| `Services/IDrsBackend.cs` | The seam. `NvApiDrsBackend` in production, `FakeDrsBackend` in tests. **Not** on `ISystemTweakBackend` - that is the performance-profile surface and has no shape for a session lifetime. A null result with no error means "not there"; null *with* an error means the read failed. |
| `Services/NgxModelStore.cs` | The driver's model store, and the **one** table of the three DLSS features. |
| `Services/DlssProbeService.cs` | Read-only. What the card is built from (shipped versions, driver-store versions, whether a driver is there), the rendering executable, the module scan, and `ReadLastRun`. |
| `Services/DlssOverrideService.cs` | Apply, undo, the pre-launch re-apply, `RestoreAll`. The ownership rules live here. |
| `ViewModels/DlssCardViewModel.cs` | The card. `Project()` is pure, so the display rules are testable without a driver. |
| `ViewModels/DlssSettingsViewModel.cs` | The Settings card: the game count, Restore All, the Indicator switch. |
| `Services/SystemTweaksService.cs` | `SetDlssIndicator` / `IsDlssIndicatorOn` - here for the elevated HKLM write and the prior-value capture, not as a tweak row. |
| `App.DlssDiagnostics.cs` | `--test-dlss`, DEBUG only. Read-only report, for when someone says DLSS is not doing what they expect. It does its own reading, so none of it is in a release build. |

The fake session is a private copy of the database that only `Save` writes back, as NVAPI behaves,
and it refuses to *delete* a predefined setting - so the tests prove undo uses the restore call.

## The ownership record

The part everything else depends on. A single "we enabled it" flag is not enough, because it cannot
tell a value the user chose from one TrayTrigger wrote. Per setting, `GameEntry.DlssSettings` holds:
the previous value and **its origin**, what was written, the profile and executable name, the
renderer's **full path**, and whether TrayTrigger **created the profile**.

**Undo restores the captured state, and only while the driver still reports what TrayTrigger
wrote.** Anything else means somebody else changed it: leave it alone.

| Previous origin | Undo | Why |
|---|---|---|
| `UserSet` | Write the captured value back | A choice TrayTrigger replaced |
| `Predefined` | **Restore the default** (`NvAPI_DRS_RestoreProfileDefaultSetting`) | NVIDIA's own value was there. Writing the number back makes it user-set; deleting is for a value that was never there |
| `Inherited` | **Delete** | The setting was never on this profile. Writing the inherited value pins it, so it stops following the Global profile |
| `Absent` | **Delete** | Nothing was there |

Other rules, each closing a way the driver could be left changed:

- **Matching is stricter than "same number".** A TrayTrigger write is a *user-set value on the
  game's own profile*, so undo requires the value **and** that origin. The same number arriving
  from the Global profile belongs to someone else. A driver-default reading is "absent", not a
  value.
- **A failed read is not "absent", and a failed lookup is not "no profile".** Only
  `NVAPI_SETTING_NOT_FOUND` (-160) and `NVAPI_EXECUTABLE_NOT_FOUND` (-166) are answers. Anything
  else stops the operation: apply creates nothing and captures nothing for that setting, undo keeps
  every record, re-apply writes nothing.
- **Profiles are looked up by full path**, falling back to the file name for a game that has moved.
  NVIDIA's own entries can be qualified by folder or launcher, and a bare `game.exe` can match
  another game's profile.
- **A profile TrayTrigger created is removed again** once undo has emptied it - not one of
  NVIDIA's, no settings left, no application beyond the one it was made for. Otherwise every game
  ever overridden leaves one behind.
- **Applying again over TrayTrigger's own values keeps the original capture.** "The state before
  this write" is then TrayTrigger's value, and capturing it would make undo restore the override.
  A value a stranger has set since *is* captured - taking over means undo gives theirs back.
- **Already back as captured** (something reverted the write) undoes to nothing and the record is
  dropped.

NVAPI supplies the origin directly - `NVDRS_SETTING.settingLocation` plus `isCurrentPredefined` -
so this is recorded, not inferred.

## Targeting the right executable

Driver profiles key on the executable that **renders**, which for a launcher-based game is not the
one TrayTrigger launches:

```
Launched  ...\Cyberpunk 2077\REDprelauncher.exe        -> profile "RED Launcher Service"
Renders   ...\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe -> profile "Cyberpunk 2077"
```

`ResolveRenderingExecutable` uses the strongest available signal: **the renderer sits beside the
game's DLSS DLLs**, because that is how NGX finds them. Among those the largest wins, after
discarding names that are never renderers - a 60 MB game against a 260 KB crash reporter.

**Games launched by link.** A Steam import's executable path is `steam://rungameid/...`, and the
same goes for any platform launched by link, so there is no folder to derive from it. The install
folder the importer records as the entry's working directory is searched instead. Without this the
card never appeared for most of a library. A link that resolves to nothing is refused, never
written to.

The scan is recursive, so it refuses folders that are not one game's own (a drive root, Downloads,
the top of Program Files - `ProcessPathResolver.IsUnsafeProcessFolder`), and it survives an
unreadable subfolder. Without the guard an exe sitting loose in `D:\Games` would find a neighbour's
DLSS DLL and the override would land on the neighbour's profile.

It is a heuristic, and every Steam game depends on it. **Last run** is what catches it being wrong.

## Checking that it worked

**The write-back check.** After saving, the database is reopened and each value confirmed present
and user-set. A second session is required: DRS sessions do not merge, so reading through the
writing session only shows its own in-memory copy. It proves the write landed; it says nothing
about whether a game honours it.

**Last run.** While a game with the override on is running, the launcher reads which modules it has
loaded - up to three times, 30 seconds apart, stopping at the first DLSS runtime seen. A game loads
DLSS when it first builds its renderer, which can be a minute in; three ticks is as short as the
window can be while covering the usual case, because this is reading a live game.

The result is one small record on `GameEntry.DlssLastRun`: the versions loaded from NVIDIA's store,
the versions loaded from the game's own files, and the game's own version at the time. It is an
**observation, never causation** - a runtime loaded from the game's files shows what the process
had open, not that the override failed - and the line says what was read and stops.

- Needs no elevation. Anti-cheat titles, and an elevated game under an unelevated TrayTrigger,
  refuse the read; then, as when nothing was seen in 90 seconds, **nothing is recorded**.
- Cleared when the override changes. Ignored once the game ships a different DLSS version.
- Written to `games.json` and logged. Nowhere else.

Matching rules, each easy to get wrong:

- **Store runtimes match by folder, game runtimes by file name.** All three substituted runtimes
  are called `160_E658700.bin`; only `models\dlss\` vs `dlssd\` vs `dlssg\` separates them.
- **`nvngx_dlss` is a prefix of `nvngx_dlssd` and `nvngx_dlssg`**, so the game-folder match compares
  the whole stem.
- **Where both are loaded for a feature the store's wins**: NGX can have the game's DLL open as
  well as the one it substituted.
- A store runtime's version comes from its `versions\<n>` directory, not the file - the hashed
  `.bin` has no version resource. A game DLL's version is normalised; NVIDIA's own string reads
  `310,1,0,0`.

**The DLSS Indicator.** `ShowDlssIndicator` = `0x400` under `HKLM\...\NGXCore`. The only thing that
shows the active **preset**. It is one machine-wide value, so it is one machine-wide switch, in
Settings, opt-in, using the tweak framework's own record-and-restore; absent before means deleted
when unticked, not set to 0. It is not a tweak row, so the preset and Restore Previous Settings
never touch it.

## Lifecycle

**Pre-launch re-apply.** NVIDIA App reverts overrides on games it does not list, whenever it
starts. TrayTrigger launches the game, so it looks first:

| Found | Action |
|---|---|
| What TrayTrigger wrote | Nothing to do |
| What TrayTrigger captured (reverted), or the whole profile gone | Write it again, recreating the profile if need be |
| Anything else | **Write nothing**, this launch or the next, for as long as it stays that way |

Everything is read before anything is written, in one session: one changed setting means something
else is managing this game, and writing the rest would be the overwrite the rule exists to prevent.
Nothing is remembered about it and the card's switch does not move. Unticking, or Restore, puts
back what is still TrayTrigger's and lets go of the rest; ticking again takes the settings over.

**Removing a game** puts its override back when the removal becomes final - where the cached
artwork is deleted, and for the same reason: inside the undo window the game may yet come back. A
setting another library entry still holds a record for is left alone.

**A crash inside that window.** Removed games are written to `pending-removal.json` before the
library is saved without them, and the next start finishes the job. A game that is back in the
library is skipped.

**Restore All / uninstall.** The System row undoes every game's override in one step. The
uninstaller runs the same thing headless, `TrayTrigger.exe --restore-dlss`, before the executable
goes. It reads the library of the account the uninstaller runs as; from a different administrator
account it finds nothing, which is why the button exists as well.

**Still not handled.** Two library entries can share one executable and therefore one override;
each applies and undoes independently, and the second to be undone finds values that are no longer
its own and leaves them.

## What is verified, and what is not

Proven on hardware (RTX 5080, driver 616.64):

- The override works: Cyberpunk 2077 ships 310.1.0 and ran **310.9.0** on all three features, no
  game file changed. After the simplification, a second game shipping 310.7.128 showed
  "Last run: loaded 310.9.0 from NVIDIA." on the card.
- Reads and writes need no elevation.
- `0x400` draws the Indicator on a retail game.

Proven before the ownership changes, **not re-run since**:

- Apply and undo round-tripping exactly, restoring `Inherited`/`absent` as captured.

Not established:

- **Undo of a `Predefined` value.** `NvAPI_DRS_RestoreProfileDefaultSetting` (`0x53F0381E`) and
  `NvAPI_DRS_DeleteProfile` (`0x17093206`) are in use and have never run against a real driver.
  Apply and undo a game whose profile has a predefined DLSS value, and one NVIDIA has no profile
  for, and compare Profile Inspector before and after.
- **A Steam game end to end** - the card appearing, and a Last run line after playing.
- **The uninstaller** actually turning the overrides off.
- **Whether the override works on older DLSS.** Both test games ship 310.x. NVIDIA says DLSS 2.0+
  for the DLL override and 3.1 for presets, so there is no hard floor in the code - the driver
  decides and Last run reports the truth.
- **Anti-cheat.** No game files change and the loaded runtime is NVIDIA-signed, and this is
  NVIDIA's own shipped feature used on protected titles - but that is an argument for low risk, not
  a test.

## Remaining work

Automatic application to newly added games was deferred and is still deferred. The real-driver
checks above.

## Corrections

Things this design got wrong, each found by testing on hardware rather than by reasoning.

**`0x00634291` does not exist.** It was in the recipe as "DLSS - Forced Model Preset Profile" and
described as the gate everyone misses. It is not a DRS setting on any driver:
`NvAPI_DRS_GetSettingNameFromId` does not know the id and `SetSetting` refuses it. The preset
letters work without it. `IDrsBackend.GetSettingName` now gates every write, so an id NVIDIA
retires later is stepped over quietly rather than looking like a fault.

**Applying targeted the launcher, not the renderer.** Every apply before this wrote to
`REDprelauncher.exe`'s profile - a process that never renders a frame. No error, and the write-back
check passed, because the settings really were written, just somewhere useless. Every GOG, Epic, EA
and Ubisoft game would have been affected.

**Deleting is right for more than "absent" - and wrong for "predefined".** The plan said to delete
only when the previous state was absent; following that would have detached this machine's games
from its Global profile settings on undo. The first fix then deleted predefined values too, and a
test blessed the result: NVIDIA's own value gone after undo. See the ownership table.

**Matching DLSS modules by file name gave the wrong answer.** The substituted runtime is a hashed
`.bin`, not `nvngx_dlss.dll`. A check that filtered on the DLL names reported "not substituted" on
a machine where substitution was plainly working.

**Three times, a report hid a real defect.** The harness printed only write-back failures and
missed an outright refusal; it read the launcher's profile while the code wrote the renderer's and
declared success; it labelled a Windows DriverStore file as the game's. Each time the code was
wrong and the report said fine. A diagnostic that overstates is worse than none.

**The card was invisible for Steam games.** It looked for DLSS files beside the executable path,
and a Steam entry's path is a link.

## Simplified

What was cut on 2026-09-20, and why - so it is not built again. This was meant to be an easy add,
and it grew a verification service, a conflict state, a session-scoped registry tweak and a
write-ahead save around a feature whose failure is "the game uses its own DLSS".

- **The per-game DLSS Indicator.** Scoping one machine-wide registry value to a game meant writing
  it at launch and putting it back at exit: an administrator prompt at each end, session counting,
  a crash-recovery snapshot, shutdown deferral, and a profile service that had to track
  overlay-only sessions - which broke the machine-wide tweaks for the next game and left the
  indicator on in three different ways. It is now a System toggle, and `PerformanceProfileService`
  is as it was before.
- **`DlssVerificationService` and per-feature observations** - states, notes, paths, "unable to
  verify" entries nobody was shown, and a driver-version staleness check nothing called. Last run
  is one record and one function.
- **The conflict flag and both notices.** One notice fired for every game on a PC whose Global
  profile carried an override, permanently, even straight after Restore; the other promised a
  take-over the switch could not do.
- **The switch re-reading the driver.** It read off on Doom Eternal, where NVIDIA App had zeroed
  the two Frame Generation settings of a game with no Frame Generation, while the Super
  Resolution override was working. The switch now shows what TrayTrigger holds.
- **Status text for success.** The switch already says it.
- **The write-ahead save**, guarding a crash in the milliseconds between two saves.
- **The `nvngx_config.txt` parser**, the NGX registry read, and the Global-profile walk on every
  Edit Game open - all read only by the DEBUG report, which now does its own reading.

What was *kept* through the same pass is the ownership record and everything in it: that is what
makes removal exact, and removal is the part that is not benign.

## References

Recorded on the development machine, 2026-09-19, RTX 5080, driver 616.64:

- Driver NGX store `C:\ProgramData\NVIDIA\NGX\models\` - 402 MB, `dlss`/`dlssd`/`dlssg` all at
  310.9.0 plus a legacy catalogue back to 1.0.108.
- Driver profile database `C:\ProgramData\NVIDIA Corporation\Drs\` - ~1.9 MB, roughly 11,000
  executables including anti-cheat wrapper variants. `nvdrssel.bin` names the active file of
  `nvdrsdb0.bin`/`nvdrsdb1.bin`; reading the wrong one gives a stale answer.
- No `nvngx_dlss.dll` exists outside game folders. The driver's copy is the `.bin` in the store.
- `HKLM\SOFTWARE\NVIDIA Corporation\Global\NGXCore` has `FullPath` and `Installed` only -
  `ShowDlssIndicator` is absent, so the Indicator creates that value rather than changing one.

NVIDIA's diagnostic `.reg` files are in [`utils/`](https://github.com/NVIDIA/DLSS/tree/main/utils)
of their public DLSS repo. Preset letter meanings are in `nvsdk_ngx_defs.h`: A-D removed, E/F
deprecated, K the default for DLAA/Quality/Balanced, L for Ultra Performance, M for Performance.
These contradict Profile Inspector's labels; the headers are correct.
