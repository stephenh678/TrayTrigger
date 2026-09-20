# DLSS model management

Built and verified on hardware, 2026-09-19/20. This describes the feature as it is. What changed
along the way, and why, is in **Corrections** at the end - worth reading before changing anything
here, because most of it was learned the expensive way.

## What it does

A **DLSS** card in Edit Game, on the Performance tab, that puts a game on NVIDIA's current DLSS
model with one click.

```
DLSS                    [ Undo TrayTrigger changes ]  [ Verify ]  [ Use recommended ]
  Super Resolution   310.1.0   Observed runtime 310.9.0 from NVIDIA's driver store
  Ray Reconstruction 310.1.0   Observed runtime 310.9.0 from NVIDIA's driver store
  Frame Generation   310.1.0   Settings saved
  Your driver holds DLSS 310.9.0.
  No game files are changed - the driver supplies the model.
  [ ] Show DLSS overlay while this game runs
```

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
`app_E658700` has an explicit entry per feature. So **the version an override will load is readable
before the game launches** - a fact, not a hope.

### Nothing needs elevation

Reads and writes both work unelevated, verified at a trust level below the filtered token a
UAC-enabled administrator account gets. The card reads, applies and undoes with **no UAC prompt**.
The one exception is the overlay, which writes HKLM.

### The three layers

DRS resolves a setting through application profile → global profile → base profile → driver
default. This matters constantly: on the development machine four DLSS settings already sat on the
**Global** profile, written by another tool days earlier, so a per-game "use recommended" was
partly a no-op that would still have reported success. The card always reads the global layer.

## The code

| | |
|---|---|
| `Services/NvApi.cs` | NVAPI DRS interop. No import library exists, so every entry point comes through `nvapi_QueryInterface` by published id. Struct versions are computed as `sizeof \| (ver << 16)` rather than hard-coded, so a layout mistake cannot pass the driver a plausible-looking number. |
| `Services/IDrsBackend.cs` | The seam. `NvApiDrsBackend` in production, `FakeDrsBackend` in tests. **Not** on `ISystemTweakBackend` - that is the performance-profile surface and has no shape for a session lifetime. |
| `Services/NgxModelStore.cs` | The driver's model store, `nvngx_config.txt`, and the **one** table of the three DLSS features. |
| `Services/DlssProbeService.cs` | Read-only: shipped versions, DRS state, loaded modules, NGX registry. Also resolves the rendering executable. |
| `Services/DlssOverrideService.cs` | Apply, undo, and the pre-launch re-apply. The ownership rules live here. |
| `Services/DlssVerificationService.cs` | Loaded modules → one observation per feature. |
| `ViewModels/DlssCardViewModel.cs` | The card. `Project()` is pure, so every display rule is testable without a driver. |
| `App.DlssDiagnostics.cs` | `--test-dlss`, DEBUG only. Read-only report, for when someone says DLSS is not doing what they expect. |

## The ownership record

The part everything else depends on. A single "we enabled it" flag is not enough, because it cannot
tell a value the user chose from one TrayTrigger wrote. Per setting, `GameEntry.DlssSettings` holds
the previous value, **its origin**, what was written, and where.

**Undo restores the captured value, and only while the driver still reports what TrayTrigger
wrote.** Anything else means somebody else changed it: leave it alone and say so.

| Previous origin | Undo | Why |
|---|---|---|
| `UserSet` | Write the captured value back | A choice TrayTrigger replaced |
| `Inherited` | **Delete** | The setting was never on this profile. Writing the inherited value pins it, so it stops following the Global profile |
| `Predefined` | **Delete** | Writing NVIDIA's own number back converts it into a user-set value |
| `Absent` | **Delete** | Nothing was there |

Matching is stricter than "same number": a TrayTrigger write is a *user-set value on the game's own
profile*, so undo requires the value **and** that origin. The same number arriving from the Global
profile belongs to someone else.

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
discarding names that are never renderers - a 60 MB game against a 260 KB crash reporter. It is a
heuristic, and verification is what catches it being wrong.

## Verification

Three layers. Each reports **observations, never causation**: a loaded runtime shows what the
process has open, not that TrayTrigger put it there, and seeing the game's own DLL does not
establish that the game refused the override.

**Layer 1 - write-back.** After saving, reopen the database and confirm each value is there and
user-set. A second session is required: DRS sessions do not merge, so reading through the writing
session only shows its own in-memory copy.

**Layer 2 - module enumeration.** The primary method. A loaded module's full path is the answer:
from `\NVIDIA\NGX\models\` means the override worked, from the game folder means it did not.
Polled every 20 seconds during a session, stopping at the first runtime seen or after ~5 minutes -
a game loads its runtime when it first builds the renderer, which can be minutes in. Anti-cheat
titles refuse enumeration; that is reported as `Unable to verify` with the refusal as its reason,
never as failure.

Two matching rules, each easy to get wrong:

- **Store runtimes match by folder, game runtimes by file name.** All three substituted runtimes
  are called `160_E658700.bin`; only `models\dlss\` vs `dlssd\` vs `dlssg\` separates them.
- **`nvngx_dlss` is a prefix of `nvngx_dlssd` and `nvngx_dlssg`**, so the game-folder match compares
  the whole stem.

A store runtime's version comes from its `versions\<n>` directory, not the file - the hashed `.bin`
has no version resource.

**Layer 4 - the on-screen overlay.** `ShowDlssIndicator` = `0x400`, per game, off by default. The
only thing that shows the active **preset**. Reference-counted across the sessions that asked for
it, not "first wins" like other machine-wide tweaks: the overlay was rejected as a global toggle
because it draws over every DLSS game, so a second game must not inherit it.

(There was a layer 3, NGX log parsing. It was dropped: it depended on logs being attributable to a
session, which was never established, and the overlay covers the same protected titles and also
gives the preset.)

Observations persist on `GameEntry.DlssObservations`, because they can only be learned by playing:

| Changed | Effect |
|---|---|
| The override (apply or undo) | Cleared - describes a setup that no longer exists |
| The game's DLSS version (a patch) | Cleared |
| The driver | Kept, marked stale - what was seen was still seen |

## Lifecycle

**Pre-launch re-apply.** NVIDIA App reverts overrides on games it does not list, whenever it
starts. TrayTrigger launches the game, so it writes the settings again first. The three-way rule:

| Found | Action |
|---|---|
| What TrayTrigger wrote | Nothing to do |
| What TrayTrigger captured (reverted) | Re-apply silently |
| Anything else | **Write nothing**, mark the game conflicted, stop re-applying, tell the user |

Everything is read before anything is written: one conflicting setting means something else is
managing this game, and writing the rest would be the overwrite the rule exists to prevent.

**Not handled yet.** Two library entries can share one executable, and therefore one override. A
game removed from the library keeps its override. Uninstalling TrayTrigger leaves driver settings
behind with nothing left to explain them - the installer should offer to clear them.

## What is verified, and what is not

Proven on hardware (RTX 5080, driver 616.64, Cyberpunk 2077):

- The override works: the game ships 310.1.0 and ran **310.9.0** on all three features, no game
  file changed.
- Apply and undo round-trip exactly, unelevated, restoring `Inherited`/`absent` as captured.
- The in-session poller recorded all three features automatically, 52 seconds after a GOG launch.
- Reads and writes need no elevation.

Not established:

- **Whether the override works on older DLSS.** The only test game ships 310.x. NVIDIA says DLSS
  2.0+ for the DLL override and 3.1 for presets, so there is no hard floor in the code - the driver
  decides and verification reports the truth.
- **Anti-cheat.** No game files change and the loaded runtime is NVIDIA-signed, and this is
  NVIDIA's own shipped feature used on protected titles - but that is an argument for low risk, not
  a test. Module enumeration is expected to be refused on protected titles.
- **The overlay end to end.** `0x400` was confirmed to draw on a retail game, and the registry
  lifecycle is unit-tested, but not the two together.
- **Creating a profile for a game NVIDIA has never seen.** The code path exists and is unit-tested;
  no real unlisted game has been tried.

## Remaining work

Help topic and CHANGELOG. Automatic application to newly added games was deferred and is still
deferred. The lifecycle gaps above.

## Corrections

Six things this design got wrong, each found by testing on hardware rather than by reasoning.

**`0x00634291` does not exist.** It was in the recipe as "DLSS - Forced Model Preset Profile" and
described as the gate everyone misses. It is not a DRS setting on any driver:
`NvAPI_DRS_GetSettingNameFromId` does not know the id and `SetSetting` refuses it. The preset
letters work without it. `IDrsBackend.GetSettingName` now gates every write, so an id NVIDIA
retires later is stepped over quietly rather than looking like a fault.

**Applying targeted the launcher, not the renderer.** Every apply before this wrote to
`REDprelauncher.exe`'s profile - a process that never renders a frame. No error, and the write-back
check passed, because the settings really were written, just somewhere useless. Every GOG, Epic, EA
and Ubisoft game would have been affected.

**Deleting is right for more than "absent".** The plan said to delete only when the previous state
was absent. Following that would have detached this machine's games from its Global profile
settings on undo. See the ownership table.

**Removing the profile guard applied unrelated tweaks.** Letting an overlay-only game past
`PerformanceProfile == Off` also applied the power plan, HDR and Do Not Disturb. Every tweak had
relied on that early return to gate on the tier, three screens away, with nothing in their own code
saying so.

**Matching DLSS modules by file name gave the wrong answer.** The substituted runtime is a hashed
`.bin`, not `nvngx_dlss.dll`. An earlier check filtered on the DLL names and reported "not
substituted" on a machine where substitution was plainly working.

**Three times, a report hid a real defect.** The harness printed only write-back failures and
missed an outright refusal; it read the launcher's profile while the code wrote the renderer's and
declared success; it labelled a Windows DriverStore file as the game's. Each time the code was
wrong and the report said fine. A diagnostic that overstates is worse than none.

## References

Recorded on the development machine, 2026-09-19, RTX 5080, driver 616.64:

- Driver NGX store `C:\ProgramData\NVIDIA\NGX\models\` - 402 MB, `dlss`/`dlssd`/`dlssg` all at
  310.9.0 plus a legacy catalogue back to 1.0.108.
- Driver profile database `C:\ProgramData\NVIDIA Corporation\Drs\` - ~1.9 MB, roughly 11,000
  executables including anti-cheat wrapper variants. `nvdrssel.bin` names the active file of
  `nvdrsdb0.bin`/`nvdrsdb1.bin`; reading the wrong one gives a stale answer.
- No `nvngx_dlss.dll` exists outside game folders. The driver's copy is the `.bin` in the store.
- `HKLM\SOFTWARE\NVIDIA Corporation\Global\NGXCore` has `FullPath` and `Installed` only -
  `ShowDlssIndicator` is absent, so the overlay creates that value rather than changing one.

NVIDIA's diagnostic `.reg` files are in [`utils/`](https://github.com/NVIDIA/DLSS/tree/main/utils)
of their public DLSS repo. Preset letter meanings are in `nvsdk_ngx_defs.h`: A-D removed, E/F
deprecated, K the default for DLAA/Quality/Balanced, L for Ultra Performance, M for Performance.
These contradict Profile Inspector's labels; the headers are correct.
