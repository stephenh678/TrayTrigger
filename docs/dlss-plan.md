# DLSS model management: design plan

Status: design agreed 2026-09-19; **both halves of the mechanism proven by spike the same day** -
runtime substitution and preset selection (see Spike results). Not yet scheduled; no branch, no
code.
Target: a release after 1.4.6. NVIDIA only.

## Summary

A **DLSS** card in Edit Game that lets a player put a game on NVIDIA's current DLSS model with one
click. It works entirely through the NVIDIA driver's per-game profile: TrayTrigger writes seven
driver settings and the driver loads its own current DLSS runtime for that game instead of the one
the game shipped with.

**No file in any game folder is read for anything but its version, and none is ever written.** That
single decision removes the download and cache machinery, the backup and restore machinery, the
NVIDIA redistribution question, collisions with Steam's "verify integrity of game files",
file-permission and in-use locks, and the class of failures caused by replacing a game's files -
all of which the file-swapping tools (DLSS Swapper, RHI) carry. It substantially lowers, but does
not eliminate, anti-cheat risk and "my game won't launch" reports: substituting a runtime can still
introduce a compatibility problem even when no file changed.

**The promise that is strictly true and worth making is "TrayTrigger does not modify game files."**
Everything stronger than that is a probability, not a guarantee.

Because the driver decides whether an override applies, and NVIDIA warns that some games disallow
it, **verification is part of the feature, not an extra**: TrayTrigger reads the running game's
loaded modules - or, for anti-cheat games that block that, NVIDIA's own NGX logging - and reports
in the app which DLSS actually loaded, and from where. An optional per-game overlay, off by
default, additionally shows the active preset while that game runs.

## Spike results, 2026-09-19 - the mechanism is proven

Run on the development machine: RTX 5080, driver 616.64, **007 First Light** (no anti-cheat), DLSS
DLLs first rolled back in RHI to the game's own 310.7.128.

Settings were applied incrementally in Profile Inspector, with a restart and a module read at each
step. **The order matters and is the most interesting part of the result:**

| Run | Settings set | Observed |
|---|---|---|
| 1 | `0x10E41E01` SR override | `nvngx_dlssg` **310.7.128, game folder**. No SR module loaded. |
| 2 | same, DLSS enabled in-game, no restart | identical - same process, so no effect possible |
| 3 | same, **clean restart** | still **310.7.128, game folder**. Still no SR module. |
| 4 | `0x10E41E01` **+ `0x10E41E03`** FG override, restart | **everything from the NGX store at 310.9.0** |
| 5 | `0x10E41E01` only again (FG override **off**), restart | SR **310.9.0 store**, RR **310.9.0 store**, Streamline **2.14.0 store**, FG **310.7.128 game folder** |

Run 4's module list:

```
LOADED FROM THE DRIVER'S NGX STORE
  310.9.0   NVIDIA Deep Learning SuperSampling   ...\models\dlss\versions\20318464\
  310.9.0   NVIDIA DLSS-G MFGLW                  ...\models\dlssg\versions\20318464\
  310.9.0   NVIDIA DLSS Ray Reconstruction       ...\models\dlssd\versions\20318464\
  2.14.0    NVIDIA Streamline, six plugins       ...\models\sl_dlss_0, sl_dlss_g_0, ...

STILL FROM THE GAME FOLDER
  2.12.128  sl.interposer.dll    the entry point - expected
  32.0.16   _nvngx.dll           the driver's own loader, from DriverStore
```

The game ships 310.7.128. It ran **310.9.0**, loaded from `C:\ProgramData\NVIDIA\NGX\models\`,
with **no file in the game folder modified**. The on-screen indicator independently read 310.9.0.

### What this establishes

1. **The core mechanism works.** Previously the plan's central claim was believed, not
   demonstrated. It is now demonstrated on real hardware with a real game.
2. **The settings are genuinely per-feature.** Run 5 settles this. With the FG override off and
   the SR override on, frame generation loaded from the **game folder at 310.7.128** while SR, RR
   and Streamline all came from the NGX store at 310.9.0. Each override controls its own feature,
   exactly as the names promise.

   An intermediate hypothesis - that the FG override was a master switch and `0x10E41E01` did
   nothing in a Streamline game - was **disproved** by run 5. It is recorded here because it
   looked convincing after run 4 and would have reshaped the recipe wrongly.

   **Run 3's null result is explained by the game, not the settings.** No SR module was loaded in
   runs 1-3 from *either* source, so DLSS Super Resolution was not active in-game and the override
   had nothing to substitute. The working model:

   > The **game's own feature toggles** decide which runtimes load.
   > The **override settings** decide where each one loads from.

   A consequence for the product: a null observation means "the feature was not in use", which is
   not the same as "the override failed". The four reported states already draw that distinction;
   this is empirical support for keeping it.

3. **Ray Reconstruction was substituted without `0x10E41E02` being set.** Only the SR override was
   on in run 5, yet RR came from the NGX store. Either the SR override covers RR, Profile
   Inspector writes more than one value behind that label, or RR follows the Streamline
   substitution. **Open** - if one setting covers both, the recipe needs fewer settings than
   specified. Check what the profile actually contains before finalising the recipe.
4. **Streamline is substituted too**, 2.12.128 to 2.14.0, six plugins, from
   `models\sl_*_override_0`. The driver handles that layer itself. This is decisive support for
   never touching `sl.*` files: RHI's separate Streamline swap column solves a problem the driver
   already solves.
5. **Settings apply only at process start.** Changing them mid-session did nothing; the game had
   to be restarted. Confirms the pre-launch write step is necessary, not merely prudent.
6. **A verification defect was caught before it shipped** - see below.

### The filename trap - a real defect in the earlier design

The substituted runtime is **not** named `nvngx_dlss.dll`. In the NGX store it is a hashed
`.bin`: `160_E658700.bin`. All three features use that same filename in different directories.

The first spike run matched modules on `nvngx_*` / `sl.*` filenames and therefore reported
**"not substituted" on a system where the override was working perfectly**. That is a false
negative that would have shipped and told users the feature had failed.

**Match on path and `ProductName`, never on filename:**

| Signal | Use |
|---|---|
| `FileName` contains `\NVIDIA\NGX\models\` | The definitive substitution test |
| `FileVersionInfo.ProductName` | Identifies the feature: "NVIDIA Deep Learning SuperSampling", "NVIDIA DLSS-G MFGLW", "NVIDIA DLSS Ray Reconstruction", "NVIDIA STREAMLINE PRODUCTION" |
| `FileVersionInfo.FileVersion` | The version, comma-separated (`310,9,0,0`) |
| `ModuleName` | **Unreliable.** Hashed and identical across features. |

`ProductName` is what distinguishes SR from RR from FG, since all three are `160_E658700.bin`.
The per-feature observation model in Data depends on it.

### Run 6: the preset settings work

`0x00634291` = `1` (Recommended) and `0x10E41DF7` = `5` (Preset E) were written, and the game
restarted. The overlay changed from **`Render Preset F`** - Ray Reconstruction's own default - to
**`Render Preset E`**, NVIDIA's "Latest transformer model".

Module reads at the same moment were unchanged and healthy: SR and RR at 310.9.0 from the NGX
store, FG still on the game's 310.7.128 (its override remained off), Streamline 2.14.0.

**This closes the last major unknown.** Both halves of the recipe are now demonstrated:

| Half | Effect | Status |
|---|---|---|
| DLL override (`0x10E41E01`, `0x10E41E03`) | Substitutes the runtime from the driver's store | **Verified** |
| Preset settings (`0x00634291`, `0x10E41DF7`) | Changes which model that runtime uses | **Verified** |

**One nuance, deliberately not over-claimed.** Both settings were written together, so what is
proven is that the *pair* works. Whether `0x00634291` is actually required - the reviewer claim
that it gates preset overrides - was not isolated. Writing `0x10E41DF7` alone might suffice. This
matters only for minimising the recipe, not for whether it works, and can be settled whenever
convenient by clearing `0x00634291` and restarting.

### Confound found afterwards: the Global profile already had four of the settings

An export of the **Global** profile, taken just after the baseline, shows these were already
present before the spike began - almost certainly written by RHI, which advertises writing DLSS
presets directly to NVIDIA driver profiles:

| Setting | Value | Meaning |
|---|---|---|
| `0x10E41E01` Enable DLSS-SR override | `1` | The exact setting applied per-game in run 1 |
| `0x10E41E02` Enable DLSS-RR override | `1` | Never set per-game during the spike |
| `0x10E41DF3` Override DLSS-SR presets | `0x00FFFFFF` | "Use recommended preset" |
| `0x10E41DF7` Override DLSS-RR preset | `0x00FFFFFF` | "Use recommended preset" |

**This closes the RR question.** RR was substituted without `0x10E41E02` being set per-game because
it was already enabled **globally**. Not a hidden coupling between features - just a second layer
nobody had looked at.

**It also confounds runs 1-5 for SR.** With `Enable DLSS-SR override` already global, SR would have
been substituted whatever the 007 profile said. Those runs cannot distinguish "the per-game setting
worked" from "global was doing it".

**What survives.** The per-feature conclusion still holds, because frame generation has **no**
global entry: FG stayed on the game's own 310.7.128 while its per-game override was off, and
substituted when it was on. That remains a clean result.

**Run 6 is unaffected and proves something extra.** Global set the RR preset to `0x00FFFFFF`
("recommended"), which rendered as **Preset F**. Setting Preset E on the *007 profile* changed the
overlay to **E**. So **a per-game setting overrides a global one** - verified, and not previously
established.

#### The global DLSS settings were three days old

A Profile Inspector backup from **2026-09-16** was compared against the 2026-09-19 export: 34
settings each, **nothing added, removed or changed**. All four DLSS overrides were already present
on the 16th.

So they were not a side effect of the spike, and the confound above is established rather than
suspected. More importantly, this is a **realistic user, not an artifact**: a tool set them days
earlier and the owner had forgotten. That is exactly who TrayTrigger will meet - global DLSS
overrides already active from RHI or NVIDIA App, invisible unless something looks for them. For
that user a per-game "Use recommended" may be a **complete no-op that still reports success**,
which is the strongest argument in this document for reporting observations rather than causation.

#### Reading the DRS store: check `nvdrssel.bin` first

The store alternates between two database files and a selector picks the live one:

```
nvdrsdb0.bin    1,924,968      the stale copy
nvdrsdb1.bin    1,925,512      the live one
nvdrssel.bin    1 byte, value 1  ->  nvdrsdb1 is active
```

During cleanup verification this produced a false alarm: comparing against `nvdrsdb0.bin` showed
576 bytes missing and suggested the revert had removed more than it should. Reading the file
`nvdrssel.bin` actually points at showed the store within 32 bytes of baseline - a clean restore.

TrayTrigger reads settings through NVAPI, not the files, so this does not affect the feature. It
matters for **diagnostics, backups and support**: any tool that inspects or copies the DRS store
must consult `nvdrssel.bin` first, or it will confidently report on data the driver is not using.

#### Precedence, confirmed

DRS resolves a setting **application profile -> global profile -> base profile -> driver default**.
NVIDIA's DRS reference exposes all three tiers (`NvAPI_DRS_GetBaseProfile`,
`NvAPI_DRS_GetCurrentGlobalProfile`, per-application profiles) and `NVDRS_SETTING` carries
`isCurrentPredefined` / `isPredefinedValid` / `predefinedValue` to express inheritance - but it
does not state the ordering outright.

**Run 6 demonstrates it directly**, which is better evidence than the documentation offers: the
Global profile held RR preset `0x00FFFFFF` ("recommended"), which rendered as Preset F; a per-game
Preset E rendered as E. The application profile wins.

This is what makes the feature viable at all - TrayTrigger can override a game whatever RHI or
NVIDIA App wrote globally. The cost is the trap below.

#### Consequence for the design: there are three layers, not two

The plan's ownership model assumes a setting is either TrayTrigger's or absent. In reality a value
can come from the **global profile**, the **per-game profile**, or NVIDIA's **predefined default**.
That has direct consequences:

- **Clearing a per-game override does not mean "no override".** If global has one, the user still
  gets substitution. "Undo TrayTrigger changes" must say so rather than implying a return to stock.
- **A per-game write can be a silent no-op.** A user whose global profile already enables SR
  override gets substitution regardless; TrayTrigger would claim credit for something already
  happening. Another reason verification reports observations, not causation.
- **Never touch the global profile.** It is shared by every game and is where other tools write.
  TrayTrigger writes per-game only, and its ownership record must not treat a global value as a
  previous value it may restore.
- **Detect and surface it.** If a global override is active, say so on the card. It changes what
  the per-game control actually does.

### The overlay, read on-screen during run 5

Two overlay lines were captured, and both cross-validate the module reads exactly:

```
NVIDIA DLSSG v310.7.128 CL 38277099 - endpoint - Release - DD v616.64
  - SL v2.14.0-rc2 - D3D12 - Output 3440x1440 - MVec 229...

Render Preset F: voracious_chowchow/weights_avg_00067-00070.pth
DLSS RR2 v310.9.0 DX12 Cubin: sm120 Res: (2293x960 -> 3440x1440), PerfQual: 2
```

| Overlay says | Module read said | Agrees |
|---|---|---|
| `DLSSG v310.7.128` | FG 310.7.128, game folder | yes - FG override was off |
| `DLSS RR2 v310.9.0` | RR 310.9.0, NGX store | yes |
| `SL v2.14.0-rc2` | Streamline 2.14.0, NGX store | yes |
| `DD v616.64` | driver 616.64 | yes |

**The overlay shows the preset letter.** This was the whole justification for keeping layer 4 and
it is now verified rather than assumed.

**`Render Preset F` corroborates NVIDIA's headers over Profile Inspector's labels.**
`nvsdk_ngx_defs_dlssd.h` records `RayReconstruction_Hint_Render_Preset_F = 6 // Default model RR2`,
and the overlay independently reads `DLSS RR2`. The header semantics are real driver behaviour.

**Two traps for anyone reading the overlay**, each capable of producing a wrong conclusion:

1. **Every line names its feature.** `DLSSG`, `DLSS RR2` and plain `DLSS` are different things.
   Reading `310.7.128` off the DLSSG line while the SR override is on would look like total
   failure when only the deliberately-disabled feature had reverted.
2. **Ray Reconstruction does the upscaling when it is active**, so a game running RR may show no
   separate SR line at all even though the SR runtime is loaded. Absence of an SR line is not
   absence of SR.

### Still not established

- ~~Whether the preset settings do anything.~~ **Settled - run 6, they work.** See below.
- Whether the override works **below DLSS 3.1**. 007 First Light ships 310.7.128, so this run says
  nothing about old games. Rainbow Six Extraction remains the test, and its DLSS could not be
  enabled - itself possibly because a 2021 runtime does not recognise an RTX 5080.
- Whether NGX logs can be attributed to a session.
- ~~Whether `NvAPI_DRS_SaveSettings` needs elevation.~~ **Settled: it does not.** Neither reads
  nor writes need elevation; see the round trip.

## The in-app probe, 2026-09-19 - build order step 1, and what it settled

The hand-run PowerShell spike is now replaced by code in the product, so the numbers a user sees
and the numbers a spike produces come from one implementation:

| File | Role |
|---|---|
| `Services/NvApi.cs` | Read-only NVAPI DRS interop. No `SaveSettings`; there is no write path to reach. |
| `Services/NgxModelStore.cs` | Reads the driver's model store and `nvngx_config.txt`. |
| `Services/DlssProbeService.cs` | Combines driver, profile, game folder, loaded modules and registry into one result. |
| `App.DlssDiagnostics.cs` | `--test-dlss [exe]`, DEBUG only, prints the lot. |

Read-only was a deliberate stopping point: the plan makes the ownership record a prerequisite for
writing (step 3), and shipping a write path before it is the one sequencing mistake the build order
exists to prevent.

### Settled: the override's target version is knowable before launch

`C:\ProgramData\NVIDIA\NGX\models\nvngx_config.txt` is an INI file mapping NGX app ids to runtime
versions. The `sl_*_override_0` sections all key on the pseudo-app `app_E658700`, and so do the
three feature sections:

```
[dlss]  app_E658700 = 310.9.0
[dlssg] app_E658700 = 310.9.0
[dlssd] app_E658700 = 310.9.0
```

That is the same id as the substituted module observed in run 5 (`160_E658700.bin`), which is what
ties the file to the override path rather than to some other mapping.

**Consequence for the UI.** An earlier section said TrayTrigger can only report what loaded, never
promise what will. For the *version* that is now too weak: the driver states its answer in a text
file before anything launches. The UI may say "this would load 310.9.0" as a read fact. It still
may not promise a **preset**, which remains observable only at runtime. Verification does not
become optional - it moves from establishing the version to confirming the driver did not decline.

Version folder names decode as `(major << 16) | (minor << 8) | patch`; 20318464 is 310.9.0, 65644
is 1.0.108. Confirmed against the versions `nvngx_config.txt` spells out for the same entries, and
unit-tested.

### Settled: "is this game in NVIDIA's database" is one call

`NvAPI_DRS_FindApplicationByName` returns `NVAPI_EXECUTABLE_NOT_FOUND` (-166) for an executable no
profile covers. So the unlisted-game case needs no heuristic and no allowlist of our own - the
driver answers directly. Observed against a made-up executable name on the development machine.

Creating the profile that this case needs is still **untested**: `NvAPI_DRS_CreateProfile` +
`CreateApplication` are writes, and belong after step 3.

### Settled: origin comes from NVAPI, it does not have to be inferred

`NVDRS_SETTING.settingLocation` reports which layer a value came from - application profile, Global
profile, base profile, or driver default - and `isCurrentPredefined` separates NVIDIA's own value
from one a user or tool wrote. The plan's four-state origin model maps onto these two fields
exactly, so the ownership record can record a fact rather than a guess.

Cyberpunk 2077, unmodified, on the development machine:

```
0x10E41E01  DLSS - Enable DLL Override          0x00000001  [Inherited (Global profile)]
0x00634291  DLSS - Forced Model Preset Profile  Absent      (NVAPI_SETTING_NOT_FOUND)
0x10E41DF3  DLSS - Forced Preset Letter         0x00FFFFFF  [Inherited (Global profile)]
0x10E41E03  DLSS-FG - Enable DLL Override       Absent      (NVAPI_SETTING_NOT_FOUND)
```

A game nobody has touched already inherits half the recipe. This is the Global-profile confound
from the spike, now **detected automatically for any user** rather than found by accident three
days later - and it is why the probe always reads the Global layer, not just the per-game one.

### Structure layouts are self-checking

NVAPI versions each struct as `sizeof(struct) | (version << 16)`. Rather than hard-code NVIDIA's
constants, `NvApi.cs` computes them with `sizeof`, so a layout mistake cannot hand the driver a
plausible-looking version number. The returned setting names and values match the user's exported
`.nip` profile field for field, which is the evidence the layouts are right.

### Settled: reads need no elevation

Run again at reduced trust (`runas /trustlevel:0x20000`, which reports `Elevated: NO`), every read
came back byte for byte identical to the elevated run: the application profile, each setting's
value and origin layer, the Global profile's contents, and the `HKLM\...\NGXCore` values.

That trust level is *more* restricted than the filtered token a UAC-enabled admin account gets from
Explorer, so it covers the ordinary user as well.

**Consequence for the UI.** The DLSS card can show a game's complete current state - versions,
which overrides are set, and which layer each comes from - with **no UAC prompt**. Elevation
becomes a cost paid only at apply time, not at display time, which makes the read-only first step
worth shipping on its own.

A trap worth recording: on a UAC-enabled administrator account, processes are *not* elevated by
default - Windows hands out a filtered token, and the absence of a UAC prompt means the process was
unelevated, not that it was silently granted admin. An elevated terminal shows no prompt either,
for the opposite reason, which is what made this take three attempts to measure.

### Not settled by the probe

- **Writes.** Nothing above exercises `SaveSettings`, `CreateProfile` or `CreateApplication`.
  Whether *those* need elevation is still open, and is now the only part of the elevation question
  that remains.
- Everything under "Still not established" above stands.

## Context for a reader outside the project

**TrayTrigger** is a Windows tray-first game launcher and launch-control tool. It scans Steam, GOG,
EA, Epic, Ubisoft Connect, Xbox/Game Pass and Battle.net, presents one library, and launches games
with optional per-game behaviour: performance profiles, pre-launch and post-exit scripts, hotkeys,
a tray menu with search, and system tweaks applied at launch and reverted at exit.

- **Stack:** C# / .NET 10 / WPF, `win-x64`, MIT licence, warnings-as-errors. **Two** NuGet packages
  total (`H.NotifyIcon.Wpf`, `System.Management`); a third needs owner approval, which is why this
  plan uses direct P/Invoke rather than `NvAPIWrapper.Net`.
- **Distribution:** code-signed releases through SignPath, with signed update checksums. This
  matters: TrayTrigger carries more legal and reputational exposure than an anonymous GPL hobby
  tool, which is part of why file swapping was rejected.

Existing pieces this plan reuses, all already in the codebase:

| Component | What it already does |
|---|---|
| `ISystemTweakBackend` | The single seam for every registry write, power-plan call and system tweak. Has a fake implementation for tests. |
| `PerformanceProfileSessionSnapshot` | Capture-apply-restore for session tweaks, including crash recovery if the app dies mid-session. |
| `PerGameProfileSnapshot` | Per-executable settings with a `WasPreExisting` flag so restore never undoes something the user set themselves. |
| `SystemInfoService` | GPU detection; already converts Windows driver strings to NVIDIA form (`32.0.16.1664` to `616.64`). |
| `ProcessPathResolver` | Resolves what actually launched, versus what was configured. |
| `GameEntry` | Per-game model, persisted to `games.json`. |

Relevant conventions: shared styles and brushes live in `App.xaml` (no new inline hex); a control's
name must match its Help topic title in `Help/**/*.md`; user-facing changes get a `CHANGELOG.md`
bullet written for players; one work item per commit.

## Decisions

| Topic | Decision |
|---|---|
| Mechanism | Driver profile settings via NVAPI DRS only. No DLL swapping, ever. |
| Vendor | NVIDIA only. No AMD FSR, no Intel XeSS. |
| Games below DLSS 3.1 | **Try anyway.** NVIDIA's own setting descriptions say the DLL override works on DLSS 2.0+, not 3.1+. See The version floor. |
| Frame generation | Driver setting only, like the rest. Never a file swap (see Streamline, below). |
| Card visibility | Visible by default on any game where a DLSS DLL is found. No setting to enable it. |
| Applied by default | No. Per game, explicit, opt-in. Nothing changes until the user acts. **Automatic application to newly added games is deferred** until the ownership and verification models are proven. |
| Preset choice | One option, **"Use recommended"** - not "latest". NVIDIA's selection is per quality mode (K for DLAA/Balanced/Quality, M for Perf, L for Ultra Perf), so there is no single latest-and-best. Letters are not exposed in v1. |
| Anti-cheat gate | **Not needed.** Nothing on disk changes. |
| When settings are written | At apply time *and* again in the pre-launch step (see NVIDIA App conflict). |
| Elevation | Required for `NvAPI_DRS_SaveSettings`. Reuses the existing "needs administrator" flow. |
| Games NVIDIA has not validated | **Override them.** Owner decision, 2026-09-19. Requires the verification feature below. |
| Verifying it worked | Live module enumeration first (no registry, reads the loaded DLL's path); NGX logging as the fallback for anti-cheat games that block it; both reported **in the app**. Plus an optional per-game, session-scoped on-screen overlay, off by default - the only way to see the active preset. |

## Why the driver path, not file swapping

The game never opens its DLSS file itself. It calls `nvngx.dll` - which belongs to the driver, not
the game - and asks it to load DLSS. By default `nvngx.dll` picks up `nvngx_dlss.dll` from the game
folder. When a DLL override is set in the game's driver profile, it loads the driver's own copy
instead, and the game's file is never opened.

The driver's copies live in `C:\ProgramData\NVIDIA\NGX\models\`, one directory per version, with a
`.bin` extension and a content-hash name. They are complete, signed DLSS runtimes, not model
weights. Verified on the development machine, 2026-09-19:

```
C:\ProgramData\NVIDIA\NGX\models\dlss\versions\20318464\files\160_E658700.bin
  Magic         MZ  (PE DLL)
  InternalName  DLSS
  ProductName   NVIDIA Deep Learning SuperSampling
  FileVersion   310.9.0
  Size          70.8 MB
  Signature     Valid
```

`dlssd` (Ray Reconstruction) and `dlssg` (Frame Generation) hold 310.9.0 in the same layout. The
store also keeps a back catalogue (1.0.108 through 2.3.4) and `nvngx_config.txt` maps individual
applications to versions.

So the driver already holds, signed and current, exactly what DLSS Swapper would otherwise download
and copy into the game folder. Using it is the same outcome by a mechanism that cannot fail a
file-integrity check.

### Consequences

- **Anti-cheat: lower risk, not zero risk.** Among the mechanisms anti-cheat is known to use -
  hashing files on disk in the game directory, watching for injection (`CreateRemoteThread`,
  `VirtualAllocEx`, unsigned modules mapped into the process), and checking signatures of loaded
  modules - the driver path does not trip any: no file in the game directory changes, nothing is
  injected, and the runtime the driver loads is an Authenticode-signed NVIDIA binary. That list is
  not exhaustive and no vendor publishes its full detection model, so this argues for **low risk**,
  not "nothing to detect". Supporting it: this is NVIDIA's own shipping feature, used by NVIDIA App
  on hundreds of games including protected multiplayer titles, with no warning published anywhere,
  and no reviewer found an authoritative report of a ban caused by it.
  NVIDIA even ships driver profiles for the anti-cheat wrapper executables
  (`fortniteclient-win64-shipping_eac_eos.exe`, `r6-extraction_be.exe`), so protected games are
  clearly expected to use these settings.
- **"Latest" tracks the installed driver**, not the newest build in existence. At the time of
  writing the driver holds 310.9.0 while NVIDIA's public repo is at 310.9.1. This is desirable: the
  feature updates itself with the driver and TrayTrigger ships no version list to maintain.
- **It cannot add DLSS to a game.** The override changes which DLSS runs, never whether it runs. The
  game's own DLSS toggle must be on.

## The version floor

**This section corrects an earlier assumption in this plan.** The working belief was a hard DLSS 3.1
floor. NVIDIA's own description of the DLL override settings says otherwise:

> "Only DLSS2+ games support the override, certain games may also disallow using it."

So the two halves have **different floors**:

| | Floor | Why |
|---|---|---|
| DLL override (`0x10E41E01`) | **DLSS 2.0** | Substitutes the runtime before the game's own code matters |
| Preset letter (`0x10E41DF3`) | DLSS 3.1 | Needs a runtime that reads presets from the profile database |

And the two compose. If the DLL override loads a 310.9 runtime into a DLSS 2.x game, the *loaded*
runtime does honour presets - so the combination may work on games well below 3.1.

The mechanism that makes this plausible is that NGX's evaluated-feature exports
(`NVSDK_NGX_D3D12_EvaluateFeature`, `NVSDK_NGX_VULKAN_EvaluateFeature`) hold C-ABI backwards
compatibility across DLSS 2.x and 3.x, so a newer runtime can serve an older integration. Plausible
mechanism, not a test - expect engine-specific variance, which is what verification is for.

This means the earlier decision to exclude pre-3.1 games was probably too conservative, and
excluding them would have thrown away the single biggest win available: an old game on the 2021 CNN
model is exactly where the transformer model is most visible.

**Do not hard-code a version floor.** Offer the action on any game where a DLSS DLL is found, let
the driver decide, and let verification report the truth. "Certain games may also disallow using
it" means failure is expected sometimes and must be reported honestly, which the verification
feature already does.

**Test case on the development machine:** Tom Clancy's Rainbow Six Extraction, `nvngx_dlss.dll`
2.3.7, Ubisoft Connect, no anti-cheat markers found. If the override works there, the floor
question is settled in favour of trying. If it does not, fall back to showing the version with a
line saying the driver declined - not a hard-coded exclusion.

Whatever the result, do not link to or recommend DLSS Swapper or any other third-party tool.
TrayTrigger should not route users toward the risk it declined to take.

## Confidence: what is verified, what is not

Every load-bearing claim in this plan, graded. A reviewer should spend their effort on the
**inferred** rows - the verified ones can be re-checked mechanically, and the documented ones are
NVIDIA's own words.

| Claim | Confidence | Basis |
|---|---|---|
| The driver holds complete, signed DLSS runtimes outside game folders | **Verified** | Read the PE header of `160_E658700.bin`: `MZ`, InternalName `DLSS`, 310.9.0, signature Valid |
| `nvngx_dlss.dll` exists nowhere outside game folders | **Verified** | Filesystem sweep of ProgramData, Program Files, DriverStore |
| The driver profile DB holds ~11,000 executables incl. anti-cheat wrappers | **Verified** | UTF-16 string scan of `nvdrsdb0.bin`; count is approximate, the named entries are exact |
| NGX diagnostics are off by default | **Verified** | `NGXCore` key holds only `FullPath` and `Installed`; no NGX logs on disk |
| Games ship Streamline DLLs beside the NGX module, versioned separately | **Verified** | Fortnite: `nvngx_dlssg.dll` 3.5.0 next to `sl.*` 2.2.0 |
| NVIDIA's public repo serves signed, tagged DLSS runtimes | **Verified** | Downloaded `v310.9.1`, 56.2 MB, signature Valid, `CN=NVIDIA Corporation` |
| The seven setting IDs and their values | **Community-sourced** | `CustomSettingNames.xml` - authoritative for Profile Inspector's mapping, not for driver behaviour or NVIDIA authorship |
| DLL override works on DLSS 2.0+, presets need 3.1 | **Documented** | NVIDIA's setting description: "Only DLSS2+ games support the override" |
| ~~`0x00634291` gates whether presets apply~~ | **Refuted** | The id is not a DRS setting at all. See the round trip, 2026-09-19 |
| Overrides shipped in driver 572.16 | **Documented** | NVIDIA release notes / support article |
| NVIDIA App enforces its allowlist, the driver does not | **Documented** | Third-party analysis of `ApplicationStorage.json` / `fingerprint.db`; consistent with Profile Inspector working on unlisted games |
| **Driver-path overrides carry no realistic anti-cheat risk** | **Inferred** | No files change, nothing is injected, the loaded module is NVIDIA-signed, it is NVIDIA's own shipped feature on protected titles, and no vendor publishes a warning. Independently agreed by a second reviewer citing zero documented bans - but that is still absence of evidence, not a test. |
| **The 3.1 floor is about the game's API integration, not the model** | **Inferred** | Fits the observed cutoff; NGX's evaluated-feature exports hold C-ABI compatibility across 2.x/3.x, which would allow a newer runtime to serve an older integration. NVIDIA does not document the reason. |
| **Writing settings pre-launch defeats NVIDIA App reverting them** | **Inferred** | Reviewer reports NVIDIA App reconciles on its own startup, driver update or library scan, and does not hook process creation. Plausible and matches observed behaviour; untested by either party. |
| ~~**`NvAPI_DRS_SaveSettings` needs elevation**~~ | **Refuted** | 2026-09-19: a full apply and undo succeeded at reduced trust, `Elevated: False`. The feature needs no UAC prompt at all |
| **DRS reads need no elevation** | **Verified** | 2026-09-19, `runas /trustlevel:0x20000` (`Elevated: NO`): profile, values, origin layers, Global profile and the NGXCore registry all read back identically to the elevated run |
| **DRS can be read from inside TrayTrigger** | **Verified** | 2026-09-19 probe: profile, per-setting value, origin layer and the Global profile's contents all read back, matching the user's exported `.nip` field for field |
| **An executable absent from NVIDIA's database is detectable** | **Verified** | `NvAPI_DRS_FindApplicationByName` returns `NVAPI_EXECUTABLE_NOT_FOUND` (-166) |
| **The version an override will load is readable before launch** | **Verified** | `nvngx_config.txt` maps the override pseudo-app `app_E658700` to an explicit version per feature |
| **A profile can be created for an unlisted executable** | **Untested** | The code path exists and is unit-tested; no real unlisted game has been tried |
| **Combining DLL override + preset works on a DLSS 2.x game** | **Untested** | Still the highest-value unknown. The 2026-09-19 spike used a 310.7.128 game, so it says nothing about DLSS 2.x. |
| ~~`0x00634291` = 1 reproduces NVIDIA App's selection~~ | **Moot** | There is no such setting to write |
| FG's `0x00FFFFFE` means "latest" rather than "default" | **Disputed** | Profile Inspector labels it "Use recommended preset". One reviewer says NVIDIA defines it as Default and `0x00FFFFFF` as Latest. **Neither sentinel exists in NVIDIA's SDK headers**, whose enum is 0-15. Unresolved until the spike. |
| Preset letter meanings (K/L/M per-mode defaults; A-D removed) | **Documented** | `nvsdk_ngx_defs.h` and `nvsdk_ngx_defs_dlssd.h`, NVIDIA's own headers. These **contradict** Profile Inspector's labels. |
| `ShowDlssIndicator` = 1024 works on retail builds | **Documented** | NVIDIA documents 1024 for release builds; also matches the documented `__NGX_SHOW_INDICATOR=1024`. Stronger than the "community reports" an earlier draft cited. |
| `EnableLogPathOverride` / `LogPath` exist and work | **Unverified** | Not in NVIDIA's `.reg` files, which set only `LogLevel`. Secondary source only. |
| An NGX log can be attributed to a specific game session | **Unverified** | Logging is machine-wide and filenames carry only a version. If false, layer 3 cannot verify anything. |
| Module enumeration reveals the loaded DLL's path and version | **Verified** | Read against a real DLSS game, 2026-09-19. **Must match on path and ProductName, not filename** - the substituted runtime is a hashed `.bin`. |
| Anti-cheat blocks module enumeration | **Documented** | Standard EAC/BattlEye/Vanguard behaviour; not tested here |
| The overlay renders on anti-cheat titles | **Documented** | It is an NVIDIA-signed swapchain hook, not third-party injection; reviewer-supplied, untested here |
| The overlay shows the game's own version when the override is declined | **Documented** | Reviewer-supplied; makes layer 4 a complete check. Confirm in the spike. |

**Updated 2026-09-19.** The core mechanism is no longer believed - it is demonstrated. See Spike
results. The driver substituted SR, RR, FG and six Streamline plugins from its own store, with no
game file modified. What remains untested is the preset half of the recipe, behaviour below DLSS
3.1, log attribution, and the elevation requirement.

| Claim | Confidence | Basis |
|---|---|---|
| **The DLL override substitutes the runtime from the driver's store** | **Verified** | 007 First Light, 310.7.128 to 310.9.0, path under `\NVIDIA\NGX\models\`, 2026-09-19 |
| **Streamline is substituted by the driver too** | **Verified** | Six `sl_*` plugins, 2.12.128 to 2.14.0, same run |
| **Settings take effect only at process start** | **Verified** | Mid-session changes did nothing; a restart was required |
| **The override settings are per-feature** | **Verified** | Run 5: FG override off left FG on the game's 310.7.128 while SR and RR came from the store |
| **A feature the game is not using produces no observation at all** | **Verified** | Runs 1-3 loaded no SR module from either source, because SR was off in-game |
| **RR is substituted without a per-game `0x10E41E02`** | **Explained** | The Global profile already had `0x10E41E02` = 1, pre-existing, probably from RHI |
| **A per-game setting overrides a global one** | **Verified** | Run 6: global RR preset `0x00FFFFFF` rendered as F; per-game Preset E rendered as E. Precedence is application -> global -> base -> default; NVIDIA exposes the tiers but does not document the ordering, so this observation is the primary evidence. |
| **The per-game SR override was what caused SR substitution** | **Confounded** | Global already had `0x10E41E01` = 1, so runs 1-5 cannot attribute SR substitution to the per-game setting |
| **The overlay reports the active preset letter** | **Verified** | `Render Preset F` read on-screen, run 5 - the justification for layer 4 |
| **The overlay and module enumeration agree** | **Verified** | FG, RR, Streamline and driver versions matched across both methods in the same session |
| **NVIDIA's SDK header preset semantics are real driver behaviour** | **Verified** | Header calls preset F "Default model RR2"; the overlay independently reads `DLSS RR2` |
| **Preset settings change the active preset** | **Verified** | Run 6: overlay went from `Render Preset F` (RR default) to `Render Preset E` after writing `0x00634291` and `0x10E41DF7` |
| **`0x00634291` is required for preset overrides to apply** | **Refuted** | It is not a setting. Run 6's preset change came from `0x10E41DF7` alone, which isolates the preset letter after all |

## What can and cannot be read back

This constrains the UI and must not be papered over.

| Value | Readable | How |
|---|---|---|
| DLSS version per feature | Yes | `FileVersionInfo` on the game's `nvngx_dlss*.dll` |
| Whether an override is set, and its value | Yes | `NvAPI_DRS_GetSetting`, which also reports whether the value is user-set or NVIDIA's predefined default |
| **Which layer a value comes from** | **Yes** | `NVDRS_SETTING.settingLocation`, verified 2026-09-19 - see the probe section |
| **Whether NVIDIA has any profile for this executable** | **Yes** | `NvAPI_DRS_FindApplicationByName` returns `NVAPI_EXECUTABLE_NOT_FOUND` (-166) |
| **The version an override would load, before launching** | **Yes** | `nvngx_config.txt` in the driver's model store - see the probe section |
| **The preset a game actually uses when no override is set** | **No** | Chosen by the game's runtime per quality mode. Nothing on disk records it. |

So an untouched game shows its version and the word `Game default`. **Never display a preset letter
we cannot verify.** Inventing one would be worse than saying less.

The third and fifth rows are new, and the fifth changes what the UI may promise: the runtime an
override resolves to is a **fact readable before launch**, not a hope. See below.

## Driver settings written - the "force latest" recipe

Setting IDs and values below come from `nvidiaProfileInspector/CustomSettingNames.xml` in
[Orbmu2k/nvidiaProfileInspector](https://github.com/Orbmu2k/nvidiaProfileInspector), group
"05 - Upscaling and Frame Generation".

**That file is authoritative for Profile Inspector's own mapping, not for driver behaviour.** It is
community-maintained; its descriptions are not necessarily NVIDIA's words and it does not guarantee
what the driver does. Where NVIDIA's SDK headers disagree with it, the headers win - and they do
disagree (see Preset semantics below). Treat every value here as a hypothesis the spike confirms.

**Two things are needed, not one.** Forcing the newest runtime and forcing the newest model are
separate settings, and there is a third that gates whether the preset is honoured at all.

| Setting ID | Name | Value | Purpose |
|---|---|---|---|
| `0x10E41E01` | DLSS - Enable DLL Override | `1` | Load the newest **installed** DLSS-SR runtime instead of the game's |
| `0x10E41DF3` | DLSS - Forced Preset Letter | `0x00FFFFFF` = Use recommended preset | Which SR model |
| `0x10E41E02` | DLSS-RR - Enable DLL Override | `1` | Ray Reconstruction runtime |
| `0x10E41DF7` | DLSS-RR - Forced Preset Letter | `0x00FFFFFF` | Which RR model |
| `0x10E41E03` | DLSS-FG - Enable DLL Override | `1` | Frame Generation runtime |
| `0x10E41DF1` | DLSS-FG - Forced Preset Letter | **`0x00FFFFFE`** | Which FG model |

### Traps

- **`0x00634291` does not exist.** It was in this plan as "DLSS - Forced Model Preset Profile",
  described as the gate that everyone misses, and it is not a DRS setting on any driver:
  `NvAPI_DRS_GetSettingNameFromId` does not recognise the id, and `SetSetting` refuses it with
  `NVAPI_SETTING_NOT_FOUND`. Verified on driver 616.64, 2026-09-19. It has been removed from the
  recipe, which is six settings, not seven. **The preset letters work without it** - see the round
  trip below. Every other id in the recipe the driver names itself, and those names match the
  user's exported `.nip` field for field.

- **Frame generation's sentinel is `0x00FFFFFE`, not `0x00FFFFFF`.** Profile Inspector labels FG's
  `0x00FFFFFE` "Use recommended preset" and SR's `0x00FFFFFF` the same. An earlier draft called the
  difference a "nonsense value" trap; that framing was wrong. **Neither sentinel appears anywhere
  in NVIDIA's SDK headers**, whose preset enum runs 0-15 with `Default = 0`. One reviewer states
  NVIDIA defines `0x00FFFFFF` as Latest and `0x00FFFFFE` as Default; that could not be substantiated
  either. Use the per-setting value Profile Inspector documents, and **confirm both in the spike** -
  if FG's `0x00FFFFFE` means Default rather than Latest, the FG half of the recipe is wrong.
- **Preset letters are not a single scale across features**, and the per-feature meanings differ.

### Preset semantics, from NVIDIA's SDK headers

`nvsdk_ngx_defs.h` and `nvsdk_ngx_defs_dlssd.h` in NVIDIA's public repo are the best available
source, and they **contradict Profile Inspector's labels** - the XML calls `0x1` "Preset A (CNN)"
while NVIDIA records A as removed.

Super Resolution (`NVSDK_NGX_DLSS_Hint_Render_Preset`):

| Value | NVIDIA's own comment |
|---|---|
| `0` Default | "default behavior, may or may not change after OTA" |
| A, B, C, D | **Removed** - "use preset J or K" |
| E (5), F (6) | Deprecated |
| G, H, I, N, O | "Do not use, reverts to default behavior" |
| J (10) | "Preset K is generally recommended over preset J" |
| **K (11)** | "Default preset for DLAA/Balanced/Quality modes that is transformer based. Best image quality preset at a higher performance cost" |
| **L (12)** | "Default for Ultra Perf mode" |
| **M (13)** | "Default for Perf mode" |

Ray Reconstruction (`NVSDK_NGX_RayReconstruction_Hint_Render_Preset`):

| Value | NVIDIA's own comment |
|---|---|
| `0` Default | "may or may not change after OTA" |
| A, B, C | **Removed** - "use preset D or E" |
| D (4) | "Transformer model" |
| E (5) | "Latest transformer model (must use if DoF guide is needed)" |
| F (6) | "Default model RR2" |
| G through O | "Do not use, reverts to default behavior" |

Two consequences:

1. **K, L and M are per-mode defaults, not a quality ranking.** There is no single "latest and
   best" preset - which is why the button says *Use recommended*, not *Use latest*.
2. `Default = 0` already carries the note "may or may not change after OTA", so NVIDIA's own
   default is not frozen. Worth testing whether it alone tracks new models, which would make part
   of this recipe unnecessary.

### Optional, later

| Setting ID | Name | Note |
|---|---|---|
| `0x10E41E06` | DLSS - Enable Streamline Override | Driver 616.56+. The supported way to modernise Streamline, and a further reason never to touch `sl.*` files. |
| `0x10E41E04` / `0x10E41E05` / `0x10E41DF8` | DLSS-NR (Neural Rendering) override, Streamline override, preset | Driver 610.47+. Out of scope for v1. |

Not in scope at all, but noted so nobody thinks they were missed: `0x10AFB768` Forced Quality Level,
`0x10E41DF5` Forced Scaling Ratio, `0x10E41DF4` Override DLSS modes to DLAA, `0x104D6667` MFG frame
count, `0xB0D384C0` Smooth Motion. These are RHI's territory and a separate decision.

### Rules

- Clearing the override **deletes** these values rather than writing zeros, so the game returns to
  NVIDIA's predefined defaults rather than to an explicit "off" that overrides them.
- Only write settings for features the game actually has. No `nvngx_dlssd.dll`, no RR settings.
- Gate the optional settings on driver version; `MinRequiredDriverVersion` is in the XML above.
  Development machine is on 616.64, which supports all of them.

## Driver version dependence

The driver is not incidental to this feature - it *is* the feature. It decides whether the settings
exist, what "latest" resolves to, and whether the result is reproducible between two machines.

### Four distinct dependencies

**1. A hard floor: 572.16.** DLSS overrides shipped with GeForce Game Ready Driver 572.16 (late
January 2025, the RTX 50 launch driver). Below that, writing the settings does nothing.

Note that `MinRequiredDriverVersion` is `0` for all seven core settings in
`CustomSettingNames.xml`. That means "no minimum recorded", **not** "works on any driver". Do not
treat it as a floor.

**2. A moving ceiling: "latest" means latest *installed*.** NVIDIA's own wording for
`0x10E41E01` is "overrides DLSS-SR with the latest global version installed". The development
machine holds 310.9.0; someone on a 2024 driver holds something far older and gets a far smaller
benefit from the same button. The feature's *value* scales with the user's driver, even though its
*behaviour* does not change.

**3. Per-setting gates for the newer settings.** `0x10E41E04/05/DF8` (Neural Rendering) need 610.47;
`0x10E41E06` (Streamline override) needs 616.56. Both are out of scope for v1, but if they are ever
added they must be version-gated individually rather than assumed.

**4. OTA updates, which are the subtle one.** NVIDIA's description of the override settings says:

> "NVIDIA periodically push OTA updates for the override, though it often lags behind the actual
> latest available online."

Those arrive through NVIDIA App's update framework, not only through driver installs - observed on
the development machine at
`C:\ProgramData\NVIDIA Corporation\NVIDIA App\UpdateFramework\ota-artifacts\...\Display.Driver\nvngx_dlssg.dll`.

**So two users on the same driver can hold different models**, depending on whether NVIDIA App is
installed and has run. TrayTrigger cannot control this and should not try. It is another reason the
UI must report what actually loaded rather than promise "latest".

### What the implementation must do

- **Read the driver version and gate the card below 572.16.** `SystemInfoService` already converts
  the Windows driver string to NVIDIA's form (`32.0.16.1664` to `616.64`) at
  `SystemInfoService.cs:326`. Reuse it; do not write a second parser.
- **Below the floor, say why**, rather than hiding the card silently:
  `Needs NVIDIA driver 572.16 or newer. You have 551.86.`
- **Never print the word "latest" as a result.** Show the version that actually loaded. "Latest" is
  acceptable as the name of an *intent*, never as a report of an *outcome*.
- **Record the driver version alongside a verification result.** A driver update changes what
  "latest" resolves to, so a result captured on 616.64 is not evidence about 620.x. Show the
  verified line as stale when the driver has changed since.

## Frame generation: driver only, never files

Frame generation runs through **Streamline**, a second layer with its own versioned DLLs that must
match the NGX module. Observed in a real install on the development machine:

```
nvngx_dlssg.dll   3.5.0     NGX frame-generation module
sl.dlss_g.dll     2.2.0     Streamline frame-generation plugin
sl.interposer.dll 2.2.0     Streamline loader
sl.common.dll     2.2.0
```

Replacing `nvngx_dlssg.dll` alone can leave a mismatched pair. RHI exposes Streamline as a separate
swap column for exactly this reason. Since this plan does no file swapping at all the problem never
arises - recorded here so nobody reintroduces it later.

## NVIDIA App conflict

NVIDIA App maintains its own allowlist of games it will show override controls for (a few hundred),
enforced in its own files (`ApplicationStorage.json`, `fingerprint.db`) as flags such as
`Disable_SR_Model_Override`. **The driver does not enforce this list** - a setting written directly
through NVAPI applies to any game the driver will honour (see The version floor).

But NVIDIA App **reverts** overrides on games not on its list when it starts.

TrayTrigger's advantage is that it launches the game. Write the settings again in the existing
pre-launch step, so they are in place moments before the game reads them, whatever NVIDIA App did
earlier. Without this the feature will appear broken for anyone running NVIDIA App.

**Best-effort, not a guarantee.** No published contract says when NVIDIA App reconciles DRS.
Reapplying just before launch narrows the window; it does not close it.

**Resolving the contradiction.** "Never overwrite an external change" and "always reapply" conflict
whenever the pre-launch step finds a value that is neither what TrayTrigger wrote nor the captured
previous value. The rule:

| Found at pre-launch | Action |
|---|---|
| The value TrayTrigger wrote | Nothing to do |
| The captured previous value (i.e. reverted) | Reapply silently - this is the NVIDIA App case the step exists for |
| Anything else | **Do not overwrite.** Stop managing this game, mark the card as conflicted, and tell the user something else is changing it |

Distinguishing the second row from the third is exactly what the ownership record makes possible,
and is why a single boolean is not enough.

**Verify persistence by reloading DRS after saving**, not by reading back the modified in-memory
session - that would confirm only that the write reached memory.

**Resolve the rendering executable before launch.** `ProcessPathResolver` identifies what actually
launched, which is after the fact; the first launch would be wrong. Resolve the likely executable
up front, including anti-cheat wrapper variants, and correct the record afterwards if it differs.

This also means the settings should not be treated as a session tweak: they are persistent, not
applied-and-reverted. Do not put them in `OptimizedProfileTweakConfig`.

## Verification

Writing a driver setting produces no visible confirmation, and the override applies to games NVIDIA
never validated, so "did this actually do anything?" must be answerable. Four layers, cheapest
first. Layers 2 and 3 are complementary: where one fails, the other works. Layer 4 is opt-in and
is the only one that shows the active preset.

### Layer 1: write-back check (instant, no game needed)

After saving, read the settings back with `NvAPI_DRS_GetSetting` and confirm the values and that
they are user-set rather than predefined. This proves the write landed in the profile database. It
does **not** prove the game honoured it. Run on every apply; surface failure immediately.

### Layer 2: live module enumeration (no setup, no registry, while the game runs)

**This is the primary method.** A loaded DLL is visible in the running process's module list, with
its **full path** and file version. The path is the entire answer:

```
loaded from C:\ProgramData\NVIDIA\NGX\models\...   ->  the override worked
loaded from <game folder>\nvngx_dlss.dll           ->  it did not
```

In .NET this is `Process.Modules`, giving `ModuleName`, `FileName` and `FileVersionInfo`.
**Verified against a real DLSS game on 2026-09-19** - see Spike results.

**Match on path and `ProductName`, never on filename.** The substituted runtime is a hashed `.bin`
(`160_E658700.bin`), identical across all three features, distinguished only by its directory and
its `ProductName`. An earlier version of this plan matched `nvngx_*` filenames and produced a false
"not substituted" on a working system.

Why this is better than logging where it works:

- **No registry writes at all.** No HKLM, no elevation, nothing to restore, no crash-recovery path.
- **No setup and no second run.** The answer is available while the game is running.
- **TrayTrigger already has the process.** Session tracking holds it for playtime; this is one more
  read of something already in hand.
- **The path is proof**, not an inference from a version number. A game whose own DLL happens to
  match the driver's version is still distinguishable.

**Where it fails: anti-cheat.** EAC, BattlEye and Vanguard actively block opening a handle to their
process and enumerating its modules. This is the one place the approach breaks - and it breaks
exactly where the game is protected, which is also where users most want reassurance that nothing
was tampered with.

Handle a failed enumeration as "could not read", never as "override failed". Fall back to layer 3.

### Layer 3: NGX log parsing (fallback, one game session) - weakest layer

NVIDIA's NGX runtime can log what it loaded. `LogLevel` lives under
`HKLM\SOFTWARE\NVIDIA Corporation\Global\NGXCore`, and NVIDIA ships `ngx_log_on.reg`,
`ngx_log_verbose.reg` and `ngx_log_window_on.reg` in `utils/` of their public DLSS repo.

**Correction.** An earlier draft said NVIDIA's `.reg` files document `EnableLogPathOverride` and
`LogPath`. They do not - `ngx_log_on.reg` sets only `LogLevel`. Those two values came from a
secondary source and are **unverified**. Confirm them in the spike before relying on redirection.

| Value | Type | Status |
|---|---|---|
| `LogLevel` | dword | `1` on, `2` verbose, absent/`0` off. **In NVIDIA's own .reg files.** |
| `EnableLogPathOverride` | dword | `1` to redirect. **Unverified - secondary source.** |
| `LogPath` | string | Destination. **Unverified - secondary source.** |

**Hazard: an unwritable log path can make NGX/DLSS initialisation fail.** NVIDIA's programming
guide warns about this. A diagnostic that stops DLSS working is far worse than no diagnostic, so
if redirection is used the directory must be created and proved writable *before* the value is
set, and the setting removed if it cannot be.

**The attribution problem, which is why this is the weakest layer.** Logging is machine-wide and a
filename such as `nvngx_dlss_310_3_0.log` names a version, not a game, process, or launch. Another
DLSS game running at the same time can produce that file, including in a freshly created
directory. A version in a filename is therefore **not** evidence about *this* game.

Before committing to this fallback, the spike must establish whether the logs carry enough to tie
a record to the target process or session - a PID, an executable name, a timestamp that can be
bracketed by the session. **If they do not, report "unable to verify" rather than inferring.** An
unattributable log must never become a "verified" line.

### Layer 4: on-screen overlay, per game, off by default

NVIDIA's `ShowDlssIndicator` draws the loaded version **and the active preset** in a corner of the
game. `HKLM\SOFTWARE\NVIDIA Corporation\Global\NGXCore`, dword.

**It shows something neither other layer can: the preset letter.** Module enumeration gives version
and path; the NGX log filename gives version. Only the overlay confirms that the preset half of the
recipe - `0x00634291` plus `0x10E41DF3` - actually took effect. That makes it the only direct check
on half the settings this feature writes.

**History:** rejected on 2026-09-19 as a global toggle, on the grounds that it draws over every
DLSS game rather than the one being checked. Re-adopted the same day **scoped to one game's
session**, which removes that objection.

#### Per-game, session-scoped

A checkbox on the DLSS card, off by default:

```
[ ] Show DLSS overlay while this game runs
```

On in the pre-launch step, off when the session ends - the same session-tweak pattern as the NGX
logging in layer 3, sharing its `WriteHklmDword` seam, its restore, and its crash recovery. The
value is absent on a clean machine, so restoring means **deleting** it, not writing `0`.

#### Why the registry and not the environment variable

NGX also honours `__NGX_SHOW_INDICATOR=1024`, which is per-process and would need no registry, no
elevation and no restore. It was rejected for two reasons:

1. **`ProcessStartInfo.Environment` requires `UseShellExecute = false`.** TrayTrigger has nine
   launch paths and most set it `true` for shell verbs, URL protocols (`steam://`, `epic://`) and
   UAC elevation. Changing that alters working-directory resolution and elevation behaviour in the
   most critical code in the app - far too much risk for a diagnostic.
2. **It would not work for most games anyway.** With launcher-mediated launches TrayTrigger does
   not start the game; Steam or Epic does. An environment variable cannot reach a process spawned
   by an already-running launcher. It would only work where TrayTrigger is genuinely the parent.

The registry route works for all nine paths because it does not care who starts the process.

#### Value

NVIDIA's own `ngx_driver_onscreenindicator.reg` uses `1`. Community reports say `1` is dev-build
only and **`0x400` (1024)** is what works with retail games, by permitting non-developer DLLs to
draw the overlay - consistent with the environment variable's documented `1024`. **Use `0x400`, and
confirm during the spike.** NGX reads this at initialisation, so it must be set before the game
starts, which the pre-launch step already guarantees.

#### It works where layer 2 does not

The overlay is drawn by `nvngx.dll` hooking the swapchain/present pipeline - an NVIDIA-signed
driver component, not a third-party injector like RivaTuner or an OBS hook. Anti-cheat engines
treat it as trusted. So on a protected game where module enumeration is refused, **the overlay
still renders**, which makes layer 4 the practical companion to layer 3 rather than a luxury.

It also reports the **negative** case: a game that rejects the override shows its own shipped
version in the overlay (`2.3.7 | Preset: A`), not the driver's. That makes layer 4 a complete
verification method on its own, not merely a preset check.

Expected output on a modern runtime, per reviewer feedback:

```
DLSS (v310.9.0) | Render: 1920x1080 -> 2560x1440 | Preset: K
```

#### Caveats

- **It may not render in every game.** Fullscreen-exclusive modes and titles with anti-tamper HUD
  protection can fail to draw it, or flicker on resolution changes. A missing overlay therefore
  means "could not display", never "the override failed" - the same three-state discipline layer 2
  needs.
- **Calling it per-game does not make its effects per-game.** The registry value is machine-wide;
  only its *lifetime* is scoped to a session. Every concurrent DLSS game shows the overlay. The
  existing first-session/last-session restoration pattern does not by itself honour an individual
  game's diagnostic preference, so the behaviour must be specified rather than inherited:

  | Situation | Behaviour |
  |---|---|
  | Second game starts while the overlay is on for the first | Overlay stays on; it will draw on both. Do not promise otherwise. |
  | Second game requests the overlay too | Reference-count; the last session ending restores. |
  | Launch fails after the value was set | Restore immediately; do not wait for a session that never began. |
  | TrayTrigger exits while the game still runs | Restore on exit. The overlay disappears mid-game, which is preferable to leaving it set. |
  | TrayTrigger crashes | Restore on next start from the snapshot, as with every other session tweak. |
  | The value already existed before TrayTrigger touched it | **Restore the captured value, not deletion.** Deletion is correct only when it was genuinely absent. |
- **Needs elevation**, like the other HKLM writes.
- **Diagnostic, not a feature.** Word it as one, keep it off by default, and do not promote it in
  the CHANGELOG as an image-quality option.

### Reported states: say what was observed, not what it means

**An earlier draft of this section overclaimed and has been rewritten.** It said "The driver
supplied a newer model, as configured" on success and "this game appears to disallow the override"
on failure. Neither is supported by the evidence:

- A module path shows a runtime is **loaded**. It does not show that TrayTrigger *caused* it, that
  DLSS is actively rendering, or that the recommended preset is selected. An existing NVIDIA App
  setting or an OTA change could produce the same observation.
- Seeing the game-folder DLL does **not** establish that the game disallows the override. Equally
  consistent: the wrong profile or executable was targeted, something external changed the
  settings, or the observation caught an intermediate loading state.

So the UI reports **observations**, never causation. Four states, per feature:

| State | Meaning |
|---|---|
| **Settings saved** | The seven values are in the profile database and read back correctly. Nothing yet about behaviour. |
| **Runtime observed** | Feature, version, path and observation time. Says what loaded and from where. |
| **Preset observed** | Only when the overlay was on and a preset letter was actually read. |
| **Unable to verify** | Enumeration refused, no log attributable to this session, no DLSS activity yet, or the overlay did not draw. Explicitly *not* the same as "it failed". |

Wording follows the evidence:

```
DLSS                          [ Use recommended ]  [ Verify ]
  Super Resolution   310.2.1      Settings saved
  Frame Generation   3.5.0        Settings saved
  No game files are changed.
```

```
DLSS                        [ Undo TrayTrigger changes ]  [ Verify ]
  Super Resolution   Observed runtime 310.9.0 from NVIDIA's NGX store   19 Sep
  Frame Generation   Unable to verify - no frame-gen activity observed
```

```
DLSS                        [ Undo TrayTrigger changes ]  [ Verify ]
  Super Resolution   Observed runtime 310.2.1 from the game folder      19 Sep
  The driver did not substitute a newer runtime in this session.
```

That last line states the observation and stops. It does not diagnose *why*.

**Per feature, not per game.** SR can be substituted while FG is untouched or simply not in use, so
one result per game cannot represent the truth. The data model carries an observation per feature.

### Ownership: what TrayTrigger may undo

`DlssOverrideEnabled` as a single boolean is **not sufficient**, and an earlier draft of this plan
was wrong to rely on it. Two sequences break it:

1. A user has their own presets, lets TrayTrigger replace them, then clicks Restore. Deleting the
   settings destroys choices TrayTrigger never owned.
2. A user changes a preset in Profile Inspector *after* TrayTrigger applied one. Uninstall cleanup
   must not erase that newer choice.

So record, per setting:

- the **previous value and its origin** (user-set, NVIDIA predefined, or absent - NVAPI's setting
  structure distinguishes current, predefined and inherited state),
- the **value TrayTrigger wrote**,
- the **profile and application** the setting was attached to.

**Undo restores the captured previous value, and only when the current value still matches what
TrayTrigger wrote.** If it does not match, something else changed it: leave it alone and say so.

**Correction, 2026-09-19, found while implementing this.** An earlier draft said deletion is
correct "only where the previous state was genuinely absent". That is too narrow, and following it
would leave the machine changed. Only a value a user or tool explicitly set on the game's own
profile should be written back. The other three cases all need deletion:

| Previous origin | Undo | Why |
|---|---|---|
| `UserSet` | Write the captured value back | It is a choice TrayTrigger replaced |
| `Inherited` | **Delete** | The setting was never on this profile. Writing the inherited value onto it pins it there, so it stops following the Global profile - a different machine state that happens to read the same today |
| `Predefined` | **Delete** | Writing NVIDIA's own number back converts it into a user-set value |
| `Absent` | **Delete** | Nothing was there |

The `Inherited` row is not hypothetical: it is the development machine, where four DLSS settings
live on the Global profile. Under the old rule, undoing a game would silently detach it from them.

Matching is also stricter than "same number". A TrayTrigger write shows up as a user-set value on
the game's own profile, so undo requires the value **and** that origin. The same number arriving
from the Global profile is somebody else's and is left alone.

The action is therefore labelled **"Undo TrayTrigger changes"**, not "Restore default" - it puts
back what was there, which is not always a default.

This is richer than the `DefenderExclusionWasPreExisting` boolean the plan previously cited as
precedent. That pattern is the right *shape*; it is not enough on its own.

Note also that DRS settings live on **profiles**, which contain applications. The collision problem
is therefore wider than two library entries sharing an executable: two different executables can
share one profile, and writing for one changes the other.

## Targeting the right executable

Driver profiles key on an executable name. For protected games the process that actually runs is
often a wrapper - `fortniteclient-win64-shipping_eac_eos.exe`, not
`fortniteclient-win64-shipping.exe` - which is why NVIDIA ships separate profiles for each.

Writing against the wrong executable fails silently: no error, no effect. `ProcessPathResolver`
already resolves what actually launched and is the right foundation, but this specific case needs
verifying - the path stored on a `GameEntry` may not be the process that ends up in memory.

Where no profile exists at all (indie or very new titles), create one:
`NvAPI_DRS_CreateProfile` + `NvAPI_DRS_CreateApplication`. A missing profile is never a blocker.

## UI

One card in Edit Game, on the Performance tab, visible whenever a DLSS DLL is found.

**Supported game, untouched:**

```
DLSS                                              [ Use recommended ]
  Super Resolution     310.2.1      Game default
  Frame Generation     3.5.0        Game default
  No game files are changed - the driver supplies the latest model.
```

**After applying, before verifying:**

```
DLSS                            [ Undo TrayTrigger changes ]  [ Verify ]
  Super Resolution     310.2.1      Latest (driver)
  Frame Generation     3.5.0        Latest (driver)
  [ ] Show DLSS overlay while this game runs
  Set by TrayTrigger. Play the game once and press Verify to confirm.
```

**Old game** - offered, not excluded (see The version floor):

```
DLSS                                  [ Use recommended ]  [ Verify ]
  Super Resolution     2.3.7        Game default
  This game's DLSS predates preset support. The override may still work - Verify will say.
```

**No DLSS found:** the card is not shown.

See Verification for the two verified states.

Rules:

- One action plus `Verify`. No version dropdown, no per-feature toggles, no preset letters in v1.
- The action is **Use recommended**, and undo is **Undo TrayTrigger changes** - it restores captured
  values, which are not always defaults.
- The line "No game files are changed" is load-bearing. It is the sentence that reassures a user who
  has heard swapping DLLs is risky.
- `Undo TrayTrigger changes` restores each captured previous value, and deletes only where the
  setting was genuinely absent before. It never writes an explicit "off".
- If an override is present that TrayTrigger did not set (NVIDIA App, Profile Inspector), say so and
  do not silently overwrite it. Follow the `DefenderExclusionWasPreExisting` discipline already used
  for Defender exclusions.

## Settings

One entry under Settings > General, off by default:

- **Apply the recommended DLSS model to new games automatically** - off by default.

**Deferred past v1** on reviewer recommendation. Applying automatically means writing driver
settings the user never asked for, on games where the outcome is unverified. It should wait until
the ownership record and the verification states are proven in practice.

No global "apply to all" button in v1. It is a large, silent, hard-to-undo change across a whole
library.

## Data

An earlier draft had a single flag and one verification result. Both were too thin: overrides need
an ownership record to be undoable safely, and observations are per feature.

On `GameEntry`, one record per feature (`SR`, `RR`, `FG`):

```
DlssSettings : list of {
    Feature          "SR" | "RR" | "FG"
    SettingId        e.g. 0x10E41E01
    PreviousValue    uint?      null = the setting was absent
    PreviousOrigin   "UserSet" | "Predefined" | "Inherited" | "Absent"
    WrittenValue     uint       what TrayTrigger wrote
    ProfileName      string     the DRS profile it landed in
    ApplicationName  string     the executable it was attached to
    WrittenUtc       DateTime
}

DlssObservations : list of {
    Feature          "SR" | "RR" | "FG"
    State            "SettingsSaved" | "RuntimeObserved" | "PresetObserved" | "UnableToVerify"
    Version          string?    e.g. "310.9.0"
    LoadedFromPath   string?    the NGX store, or the game folder
    Preset           string?    only when actually read from the overlay
    Method           "ModuleEnumeration" | "NgxLog" | "Overlay"
    ObservedUtc      DateTime
    DriverVersion    string     e.g. "616.64"
}

DlssShowOverlay  bool   Session-scoped diagnostic overlay (default false)
```

`PreviousOrigin` matters because NVAPI distinguishes current, predefined and inherited state.
Restoring a value the user never set is as wrong as deleting one they did.

Versions on disk and live setting values are read live, never cached. Observations persist, since
they can only be learned by playing.

Invalidate an observation when the override changes, when the game's own DLSS version changes, or
when the game is removed. When only the **driver** changed, keep it but mark it stale.

## Lifecycle and edge cases

These outlive a single apply, and each one is a way the feature can leave the machine in a state
nobody intended.

**TrayTrigger is uninstalled while overrides are set.** Driver profile settings are not
TrayTrigger's to keep - they live in NVIDIA's database and will persist forever after the app is
gone, with nothing left to explain or undo them. Options: clear all TrayTrigger-set overrides on
uninstall; or leave them and document it. **Recommend clearing**, with the installer asking. This
is the same courtesy as restoring a system tweak, and an orphaned override on a game the user later
has trouble with is untraceable.

**A game is removed from the library, or uninstalled.** Its driver profile entry remains. Clear the
override when a game is removed from TrayTrigger. If the *game* is uninstalled but the entry stays,
leave it - reinstalling to the same path should keep working.

**Two library entries point at the same executable.** Driver profiles key on the executable, not on
TrayTrigger's game id, so the two entries share one override and the second write silently wins.
Detect this and show it rather than pretending they are independent.

**The game's own DLSS file changes** (a game patch). The displayed version is read live so it
self-corrects, but a stored verification result becomes misleading. Clear it - already specified
under Data.

**NVIDIA App or Profile Inspector already set an override.** Do not silently overwrite. Follow the
`WasPreExisting` discipline, say what is there, and require an explicit choice.

**DRS has no session merging.** NVIDIA's own documentation warns that if another process modifies
settings while a session is open, those modifications are lost on save. Open, load, modify and save
in one tight block; never hold a session across UI interaction or a game launch.

**Two TrayTrigger instances, or TrayTrigger plus NVIDIA App, writing at once.** Same root cause as
above. Serialise TrayTrigger's own writes; accept that a concurrent external writer can lose data
and keep the window as small as possible.

## Risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| The mechanism does not work as believed | Medium | Feature is pointless | The spike, before any interop is written |
| Settings applied but game ignores them | **Expected** | User believes a lie | Verification is mandatory, not optional |
| NVIDIA changes setting IDs or semantics | Low, ongoing | Silent breakage | Write-back check catches a rejected write; verification catches a no-op |
| Wrong executable targeted (anti-cheat wrappers) | Medium | Silent no-op | Verification catches it; resolve via `ProcessPathResolver` |
| UAC prompt on every apply | Medium | Annoying enough to abandon | Open question - confirm whether elevation is always needed |
| A user blames TrayTrigger for an unrelated graphics problem | Medium | Support load | `Undo TrayTrigger changes` is one click and the card states exactly what was changed |
| Anti-cheat flags it despite the reasoning | **Low** | **Severe** | Nothing on disk changes; this is NVIDIA's own feature. Accepted risk, documented as inferred. |

| Undo destroys the user's own prior presets | Medium | Data loss, unrecoverable | The ownership record; undo only when the current value still matches what was written |
| Log-based verification reports a result belonging to another game | Medium | A confident lie | Require session attribution or report "unable to verify" |
| A redirected NGX log path is unwritable and breaks DLSS init | Low | Feature actively harms the game | Prove the directory writable before setting the value; remove it on failure |

The anti-cheat row is the one worth arguing about. It is the only entry whose impact would be serious,
and its likelihood rests on an inference rather than a test.

## Testing

- **Unit.** The DLSS backend calls go through `ISystemTweakBackend`, so the fake covers: apply
  writes the right seven values; clear deletes rather than zeroes; pre-existing overrides are not
  clobbered; features absent from the game get no settings; driver below 572.16 writes nothing.
- **Version parsing.** `FileVersionInfo` strings are comma-separated (`310,9,1,0`) on these DLLs,
  not dot-separated. Parse and display accordingly - this bit during research.
- **Log parsing.** Feed recorded NGX log filenames; assert the extracted version. Include the
  absent-log case (game never launched, or logging failed) as a distinct state from "not verified".
- **Module enumeration.** Three outcomes must be distinct and separately tested: loaded from the
  NGX store (worked), loaded from the game folder (did not), and enumeration refused (unknown -
  anti-cheat). Conflating the third with the second would tell protected-game users their override
  failed when it may well have worked.
- **Screenshot harness.** The DLSS card has at least six states (no GPU, driver too old, no DLSS,
  untouched, applied-unverified, verified-worked, verified-failed). Add modes for each, per the
  project's existing `--screenshot-*` convention.
- **Overlay tweak.** Set on pre-launch, deleted on exit, deleted on next start after a simulated
  crash, and never left behind when two games run concurrently.
- **Accessibility.** Automation names on the card's controls, and the status line readable as one
  line, matching the standard set by the 1.4.6 automation-name work.
- **Manual.** The spike doubles as the manual test: two games, logging on, read the logs.

## Effort

Roughly **M**, assuming the spike confirms the mechanism.

| Piece | Size |
|---|---|
| Detection and read-only card | S |
| NVAPI DRS interop + fake + tests | M - the bulk of the work |
| Apply / clear / write-back | S |
| Pre-launch re-apply | S |
| NGX logging session tweak + Verify | M |
| Module enumeration verification | S |
| Per-game overlay toggle (reuses the session-tweak seam) | S |
| Settings, Help, CHANGELOG, screenshots | S |

Step 1 alone is a useful shippable increment: it tells users which of their games are on old DLSS,
with no driver interaction at all.

## Supporting work

- **NVAPI DRS interop.** The needed surface is small: open/load session, find or create profile,
  find or create application, get setting, set setting, delete setting, save, destroy session.
  Direct P/Invoke through `nvapi64.dll`'s `nvapi_QueryInterface` entry point. Do **not** take
  `NvAPIWrapper.Net` as a dependency: the project has two NuGet packages and a third needs owner
  approval, and the surface used here is far smaller than the package.
- **`ISystemTweakBackend`.** Add the DLSS get/set/clear pair here so the fake backend gives test
  coverage, as with every other machine-touching call.
- **Detection.** Scan the game's install folder for `nvngx_dlss.dll`, `nvngx_dlssd.dll`,
  `nvngx_dlssg.dll` and read `FileVersionInfo`. Cache per game; refresh on demand.
- **Help.** One topic, matching the card title, per the control-name/Help-title convention.
- **Gate on GPU capability, not merely on NVIDIA.** NVIDIA documents SR and RR overrides for RTX
  GPUs, and frame-generation upgrades for RTX 40/50 series. A GTX card, or an RTX 20/30 card for
  FG, cannot use these settings even though a DLSS DLL may sit in the game folder. The presence of
  the DLL establishes nothing about hardware support. Hide the card with no NVIDIA GPU; hide or
  disable individual features the installed GPU cannot use, and say which.

## Out of scope for v1

- DLL swapping of any kind, for any feature.
- A *global* on-screen indicator overlay. The per-game, session-scoped version is **in** scope; see
  Verification layer 4.
- **Injecting newer models into the driver's own NGX store.** Considered and rejected,
  2026-09-19. It is technically possible: create a version directory under
  `C:\ProgramData\NVIDIA\NGX\models\dlss\versions\`, place an NVIDIA-signed DLL renamed to the
  internal form (`160_E658700.bin`), and add lines to the `[dlss]` section of `nvngx_config.txt`.
  NGX Core signature-checks what it loads, and DLLs from NVIDIA's public repo pass, so signing is
  not the obstacle. Rejected because: the gain is a point release (310.9.0 to 310.9.1) against the
  driver path's CNN-to-transformer jump; the scope is machine-wide rather than per-game, so the
  undo means restoring driver state; the directory IDs and hashed filenames are undocumented
  internal format that can change with any driver; `nvngx_config.txt` is driver-managed and is
  rewritten by driver installs and OTA pushes; and it reintroduces downloads, backups, restore and
  cleanup - the exact machinery this plan exists to avoid. Never set
  `__NV_SIGNED_LOAD_CHECK=none`.
- AMD FSR, Intel XeSS.
- DLSS-NR (Neural Rendering) settings, and the Streamline override `0x10E41E06`.
- Exposing individual preset letters (K / L / M).
- Downgrading to a specific version.
- A global "apply to every game" action.
- Other driver-profile settings (VSync, Low Latency, G-Sync, ReBAR) - RHI does these; they are a
  separate decision, not a natural extension of this one.

## Build order

1. ~~Detection and read-only display.~~ **Done, 2026-09-19.** `DlssProbeService`, `NgxModelStore`,
   `NvApi`, `--test-dlss`, and the read-only card (`DlssCardViewModel`) on the Performance tab.
2. ~~NVAPI DRS interop behind `ISystemTweakBackend`, with the fake for tests.~~ **Done, 2026-09-19**
   as `IDrsBackend`/`IDrsSession` + `NvApiDrsBackend` + `FakeDrsBackend`. **Not** on
   `ISystemTweakBackend`: that interface is the performance-profile surface (power plans, Defender,
   HDR, timer resolution) and has no shape for a session lifetime. Same pattern, separate seam.
3. ~~**The ownership record**~~ **Done, 2026-09-19** (`DlssSettingRecord` on `GameEntry`,
   `DlssOverrideService.Apply`/`Undo`). Capture previous value and origin per setting, write, and
   undo only while the driver still reports what TrayTrigger wrote.
4. ~~Apply and undo, wired to the button, with the layer 1 write-back check.~~ **Done,
   2026-09-19.** The check runs against a reloaded session, as specified.
5. ~~Re-apply in the pre-launch step, with the three-way conflict rule.~~ **Done, 2026-09-19**,
   together with resolving the rendering executable.
6. ~~**Verification: module enumeration, the four reported states, and the `Verify` action.**~~
   **Done, 2026-09-20.** `DlssVerificationService`, `GameEntry.DlssObservations`, automatic capture
   during a session, and the `Verify` button.
7. NGX logging as a session tweak, *only if* the spike shows logs can be attributed to a session.
8. The per-game overlay toggle, reusing the layer 7 session-tweak plumbing.
9. Help topic and CHANGELOG bullet.

Deferred past v1: automatic application to newly added games.

Steps 1 and 2 are independent. Everything from 4 onwards depends on 3.

**Spike before step 3.** Set the seven settings by hand with NVIDIA Profile Inspector on two games -
Fortnite (310.2.1) and Rainbow Six Extraction (2.3.7) - then, while each is running, read its
loaded modules and record the DLSS module's **path and version**. No registry changes needed for
this; add NGX logging only if Fortnite refuses enumeration, which is the expected result.

### Spike checklist

No code. NVIDIA Profile Inspector and `reg` only. Refined from reviewer feedback.

**Setup**

1. Set `HKLM\SOFTWARE\NVIDIA Corporation\Global\NGXCore\ShowDlssIndicator` = `0x400` (DWORD).
   Note the value is **absent** beforehand, so cleanup means deleting it.
2. Apply the seven DRS settings in Profile Inspector to each test game.

**Game 1 - Rainbow Six Extraction (DLSS 2.3.7, Ubisoft, no anti-cheat markers).**
The version-floor test, and the highest-value unknown in the plan.

- Does the overlay appear at all?
- Does it report **310.9.0** (override worked) or **2.3.7** (driver declined)?
- What preset letter is shown? A CNN letter (A-F) versus a transformer letter (J/K/L/M) is the
  direct answer to whether `0x00634291` = Recommended did anything.
- Read the process's loaded modules and record the DLSS module's **path**. It should corroborate
  the overlay.

**Game 2 - Fortnite (DLSS 310.2.1, Easy Anti-Cheat).**
The fallback test.

- Does reading `Process.Modules` throw `Win32Exception` (access denied)? This confirms layer 2 is
  blocked on protected titles and layer 3/4 is mandatory, not optional.
- Does the overlay still render cleanly, with no anti-cheat complaint? Expected yes, since it is an
  NVIDIA-signed swapchain hook.

**If the overlay never appears on either game:** retry with `ShowDlssIndicator` = `1`, NVIDIA's own
documented value, before concluding the mechanism does not work.

**Cleanup** - do this even if the spike is abandoned midway:

- `reg delete "HKLM\SOFTWARE\NVIDIA Corporation\Global\NGXCore" /v ShowDlssIndicator /f`
- Clear the DRS overrides on both games in Profile Inspector.
- Confirm both games return to their shipped behaviour.

**Additional steps, from reviewer feedback:**

- **Capture the original settings first**, for every one of the seven IDs, including whether each
  is user-set, predefined, inherited or absent. This is the ownership record's first real test.
- **Establish an override-off baseline** for each game before changing anything, so a later
  observation can be compared against something.
- **Test the runtime override alone, then the full recipe.** If `0x10E41E01` by itself already
  loads the newer runtime, the preset settings are doing less than assumed.
- **Compare at least two quality modes** (Quality and Performance). NVIDIA's defaults differ by
  mode - K versus M - so one mode cannot confirm "recommended" behaves correctly.
- **Test whether the FG sentinel means Latest or Default**, since the headers do not define either
  value and the two reviewers disagree.
- **Attribute the logs.** Determine whether an NGX log can be tied to a specific process or
  session. If it cannot, layer 3 can only ever return "unable to verify".
- **Exercise NVIDIA App.** Refresh its library, then restart it, and check whether the settings
  survive. This is the only way to test the pre-launch reapply premise.
- **Verify exact restoration.** Undo, then confirm all seven settings match the captured originals
  - including that absent ones are absent again, not zeroed.
- **Include an RR or FG capable title** if those features ship in v1.

Answers produced: does the override work at all; does it work below 3.1; is the `0x00634291`
recipe right; does anti-cheat block enumeration; which indicator value works on retail builds;
whether logs can be attributed; and whether restoration is exact.

## Open questions

- **Does the override work below DLSS 3.1?** NVIDIA's descriptions say DLSS 2.0+ for the DLL
  override. If true, no version floor is needed at all. Settled by the spike above. This is the
  highest-value unknown in the plan.
- **Does `NvAPI_DRS_SaveSettings` always need elevation?** It writes
  `C:\ProgramData\NVIDIA Corporation\Drs\`. RHI claims some of its per-game settings apply without
  admin. Worth confirming - if a subset works unelevated, the UAC prompt can be avoided for the
  common case.

## Review history

**Gemini, 2026-09-19.** Reviewed the full plan against the four questions below.

Agreed with every decision: driver path over file swapping, no version floor, `0x00634291` = 1,
`0x400` for the indicator, one-button UI, clearing overrides on uninstall, and session-scoped
diagnostics. Raised no objection to anything.

Contributed, and now folded in above:

- The overlay is drawn by `nvngx.dll` hooking the swapchain - an NVIDIA-signed component - so it
  renders on anti-cheat titles where module enumeration is refused. This makes layer 4 the
  companion to layer 3, not a luxury.
- The overlay reports the negative case too (a declined override shows the game's own version), so
  it is a complete verification method rather than only a preset check.
- Fullscreen-exclusive and anti-tamper HUD titles may fail to draw it or flicker - so a missing
  overlay means "could not display", not "failed".
- The three mechanisms anti-cheat actually uses, which is a sharper statement of why the driver
  path is safe than the plan had.
- NGX's C-ABI backwards compatibility as the plausible mechanism behind the DLSS 2.0 floor.
- Steam "verify integrity" collisions as a further cost of file swapping.
- A better-structured spike checklist, adopted above.

**Not promoted on this review.** Gemini marked the anti-cheat reasoning and the pre-launch timing
as settled. Neither moves out of **Inferred** in the Confidence table:

- Its evidence for anti-cheat safety is "zero documented bans", which is the same
  absence-of-evidence argument the plan already makes. It is a good argument; it is not a test.
- Its claim that NVIDIA App reconciles DRS only on its own startup, driver update or library scan -
  rather than hooking process creation - is plausible and matches observed behaviour, but neither
  reviewer nor author has tested it.

A second model agreeing is corroboration, not verification. The plan's safety case still rests on
an inference, and the spike is still the thing that settles it.

**ChatGPT, 2026-09-19.** Read the plan alongside the TrayTrigger services and snapshot models,
and checked its claims against NVIDIA documentation, the NVAPI headers and Profile Inspector's
source. Supported the driver-profile approach; judged the plan ready for a spike but **not yet a
sufficient specification for the full feature**. Found real defects, all of which are now fixed:

| Finding | Resolution |
|---|---|
| Verification reported causation the evidence cannot support ("the driver supplied a newer model, as configured"; "this game appears to disallow the override") | Rewritten as four observation-only states. The UI now says what was observed and stops. |
| One verification result cannot represent SR succeeding while FG is inactive | Observations are now per feature |
| Log filenames cannot attribute a version to a game, process or launch | Layer 3 demoted to weakest layer; must report "unable to verify" unless the spike proves attribution |
| `EnableLogPathOverride` / `LogPath` were presented as documented in NVIDIA's `.reg` files; only `LogLevel` is | Corrected and marked unverified |
| An unwritable log path can make NGX/DLSS initialisation fail | Added as a hazard; the directory must be proved writable first |
| `DlssOverrideEnabled` as a boolean cannot restore a user's own prior presets, and risks erasing a newer external change | Replaced with a full ownership record: previous value, origin, written value, profile and application. Undo only when the current value still matches what was written. |
| Undo mislabelled "Restore default" when it restores captured values | Renamed "Undo TrayTrigger changes" |
| Profile Inspector's XML described as NVIDIA-authoritative | Corrected. NVIDIA's SDK headers added, and they contradict the XML - it labels `0x1` "Preset A" where NVIDIA records A as removed. |
| `0x00634291` = 1 claimed to resolve the "Recommended" question | Downgraded to an untested hypothesis |
| FG `0x00FFFFFF` called a "nonsense value" | Wrong framing, corrected. Recorded as disputed: neither sentinel appears in NVIDIA's headers. |
| "Latest" mislabels a per-mode selection (K/L/M) | Button renamed **Use recommended** |
| Anti-cheat claims were categorical ("nothing to detect", exhaustive three-method model), and file-avoidance was said to remove the "won't launch" class | Downgraded to low-risk-not-zero. The strictly true promise is "TrayTrigger does not modify game files". |
| "Never overwrite external settings" and "always reapply" contradict each other | Resolved with an explicit three-way rule at pre-launch: ours, reverted, or conflicted |
| Write-back read from the in-memory session proves only that the write reached memory | Must reload DRS after saving |
| Resolving the executable after launch cannot fix the first launch | Resolve before launch, correct afterwards |
| "Per-game" overlay does not make its effects per-game | Six concurrency situations specified explicitly |
| Gating on NVIDIA presence rather than GPU capability | Now gates on RTX, and on 40/50 series for frame generation |
| Auto-apply to new games shipped before the foundations are proven | Deferred past v1 |

Both reviewers' recommendation is the same: **run the spike before building.** ChatGPT's expanded
spike checklist is adopted above.

## For reviewers

This plan is being circulated for a second and third opinion. What would be most useful:

**Attack these first.** They are inferences, not facts, and the plan depends on them:

1. **Is the anti-cheat reasoning sound?** The argument is: no files change, it is NVIDIA's own
   shipped feature used on protected titles, and no vendor publishes a warning. That is
   absence-of-evidence. Is there a known case of a driver-level DLSS override being flagged? Is
   there a mechanism by which a module loaded from `C:\ProgramData\NVIDIA\NGX\models\` rather than
   the game folder could be treated differently from one NVIDIA App produced?
2. **Does the DLL override really work below DLSS 3.1?** The whole scope question turns on this.
   NVIDIA's wording says DLSS 2.0+; the plan takes them at their word and lets verification report
   failures. Is that right, or is a floor safer?
3. **Is writing settings in the pre-launch step actually sufficient** to beat NVIDIA App reverting
   them? Could NVIDIA App revert *during* a session, or does its own process order defeat this?
4. **Is `0x00634291` = Recommended correct**, and does it reproduce NVIDIA App's per-GPU, per-mode
   model selection outside the App?

**Also worth challenging:**

- Is "one button, no version choice, no preset letters" too little control, or correctly minimal?
- Is clearing overrides on uninstall right, or is silently modifying driver state at uninstall time
  worse than leaving it?
- Is NGX logging - or the overlay toggle - as a session tweak too invasive for a diagnostic? Each
  is a machine-wide registry write held for the duration of one game session.
- Is the decision to never swap files correct given the owner originally asked for swapping?
- Anything in **Out of scope** that should not be.

**Please do not** re-propose: DLL swapping, a *global* on-screen indicator overlay (the per-game
session-scoped one is already in scope), injecting models into the driver's NGX store, or
supporting AMD/Intel. Each was considered and rejected with reasons
recorded above; challenge the *reasoning* if you disagree, rather than proposing the idea afresh.

**Context you may lack:** see "Context for a reader outside the project" near the top. Note
particularly that TrayTrigger is a code-signed product with a named copyright holder, not an
anonymous hobby tool, which changes the risk calculus versus DLSS Swapper and RHI.

**Status of evidence:** see "Confidence". Nothing here has been tested by actually writing a driver
setting; the mechanism is believed, not demonstrated.

## References

Findings recorded on the development machine, 2026-09-19, RTX 5080, driver 32.0.16.1664:

- Driver NGX store: `C:\ProgramData\NVIDIA\NGX\models\` - 402 MB, `dlss`/`dlssd`/`dlssg` all at
  310.9.0 plus a legacy catalogue, `nvngx_deny_list.txt` present but empty.
- Driver profile database: `C:\ProgramData\NVIDIA Corporation\Drs\nvdrsdb0.bin`, 1.9 MB, roughly
  11,000 distinct executables including anti-cheat wrapper variants.
- No `nvngx_dlss.dll` exists anywhere outside game folders; the driver's copy is the `.bin` in the
  NGX store.
- `HKLM\SOFTWARE\NVIDIA Corporation\Global\NGXCore` exists with `FullPath` and `Installed` only -
  `ShowDlssIndicator` and `LogLevel` are absent, so both diagnostics are off by default and
  TrayTrigger would be creating those values rather than changing existing ones.
- No NGX log files present anywhere, consistent with logging being off.

NVIDIA's own diagnostic `.reg` files, which these settings are taken from, are in
[`utils/`](https://github.com/NVIDIA/DLSS/tree/main/utils) of their public DLSS repo:
`ngx_driver_onscreenindicator.reg`, `ngx_log_on.reg`, `ngx_log_verbose.reg`,
`ngx_log_window_on.reg`, and the matching `_off` variants.

## The read-only card, 2026-09-19

`ViewModels/DlssCardViewModel.cs`, on the Performance tab of Edit Game. It reports and does not
act: there is no apply button, because applying needs the ownership record (step 3).

Three rules it enforces, each with a test, because the card's whole justification is that it states
what was read rather than what we assume:

- **No shipped DLSS, no card.** It never appears on a game that cannot use the feature.
- **No driver profile is not "Game default".** When NVIDIA has no entry for the executable, nothing
  was read about its settings, so the card says `No driver profile`. Claiming a default there would
  assert something the probe never saw.
- **An override set to zero is not an override.** An explicit off reads as `Game default`; saying
  otherwise would tell a user something is happening to their game when nothing is.

Where an override *is* found, the card names the layer it came from - this game, your global
settings, or the driver - and adds a notice that something else set it. TrayTrigger has no write
path, so every override it can currently find is external by definition.

Two things the build turned up:

- **`FileVersionInfo.FileVersion` is the wrong property.** It returns the version resource's own
  string, and NVIDIA writes that comma-separated: Cyberpunk 2077's DLSS reads back as `310,1,0,0`.
  The numeric parts have no such formatting. `DlssProbeService.NormalizeFileVersion` uses them, so
  a shipped runtime prints in the same `310.1.0` form the driver's model store uses.
- **The card loads asynchronously, which the screenshot harness had to learn.** A capture taken
  straight after `Show()` omitted it entirely, so `--screenshot-edit-tab` would have quietly
  misrepresented the tab from here on. `LoadAsync` now returns the same task on every call and the
  harness pumps the dispatcher until it completes. It also picks a game that ships DLSS, rather
  than whichever happens to sort first.

## The ownership record, 2026-09-19

`Models/DlssModels.cs`, `Services/IDrsBackend.cs`, `Services/DlssOverrideService.cs`, and
`DlssSettings` on `GameEntry`. Step 3, and with it the write half of step 2.

The rules are above, in Ownership; what implementing them added is the correction recorded there -
only a `UserSet` value is written back, the other three origins are deleted - and one extra
condition: undo requires the recorded value **and** that it still sits on the game's own profile as
user-set. Both come from the same reasoning, that a value which merely reads the same is not the
same state.

Two further decisions worth keeping:

- **A refused `Save` returns no records.** NVAPI stages writes in memory, so a failed save means
  nothing happened. Returning the records anyway would leave a game claiming an override the driver
  never received - the failure mode that is hardest to notice later. This is also the shape the
  unelevated-write case will take, whenever it is tested.
- **Skipped and failed settings keep their records.** They still describe values TrayTrigger wrote,
  and dropping them would lose the only evidence of what it owns. Only settings actually restored
  or deleted leave the record.

### Verified: the write interop works, and staging is real

`--test-dlss-writecheck` sets a DLSS setting inside a session, reads it back, and disposes the
session **without calling Save**. On the development machine `0x10E41DF3` round-tripped as
`0x0000000B` in memory, and a subsequent `--test-dlss` showed it still `0x00FFFFFF [Inherited]` on
disk.

That separates two failures a real apply would confuse: the interop being wrong, and the driver
refusing. It also confirms NVAPI's staging model, which is what makes "a refused Save changes
nothing" true rather than hoped for.

**`NvAPI_DRS_SaveSettings` is still untested**, and so is elevation for writes. Everything up to
the save is proven.

## Apply and undo, 2026-09-19

The card's two buttons, `DlssOverrideService.Apply`/`Undo` behind them, and the layer 1 check.

**The write-back check uses a second session.** NVIDIA's documentation says DRS sessions do not
merge, so reading through the session that just wrote would only show its own in-memory copy and
confirm nothing. `Apply` therefore writes and saves in one scope, closes it, and reopens to verify.
A value counts as landed only if it is there **and** reads as user-set on the game's own profile -
a matching number arriving from the Global profile is not the write.

When the save succeeds but the values are not there, the card says so rather than reporting
success. Claiming "applied" in that case would be the most misleading thing it could do.

**Applying persists immediately, not on Save Changes.** The driver change has already happened when
the button returns, so deferring the ownership record until the dialog is saved would let Cancel
strand an override TrayTrigger could no longer undo. The records list is the game's own, mutated in
place, and the library is saved straight after any driver change.

### Two problems found while wiring it up

- **The card accused itself.** The "something else has already set an override" notice keyed on
  finding an override on the game's profile - which, one second after pressing Use recommended, is
  TrayTrigger's own. The projection now takes the ownership records, so a setting matching a record
  reads `Set by TrayTrigger`. Before step 4 this could not happen, because there was nothing to own.
- **`AsyncRelayCommand` had no awaitable form.** `Execute` is `async void`, as ICommand requires,
  so a test could only race it. It now exposes `ExecuteAsync`, which is also what lets the apply
  and undo paths be tested end to end rather than only through the service underneath.

### What has still never run

`NvAPI_DRS_SaveSettings` against the real driver. Everything up to and including the staged write
is proven on this machine; the save itself, and whether it needs elevation, are still untested and
will stay that way until someone deliberately applies to a real game.

## The first real write, 2026-09-19 - Cyberpunk 2077 round trip

`--test-dlss-roundtrip` reads the settings, applies, reads, undoes, reads again, and compares the
first and last readings. It is the only thing in the codebase that calls `NvAPI_DRS_SaveSettings`
for real. The ownership records are written to a file **before** the undo, so a crash between the
two still leaves enough to put the machine back by hand.

Run against Cyberpunk 2077 at reduced trust (`Elevated: False`):

```
BEFORE       0x10E41E01 = 0x00000001 [Inherited (Global profile)]   ... FG absent
AFTER APPLY  0x10E41E01 = 0x00000001 [UserSet]                      ... FG 0x00000001 / 0x00FFFFFE [UserSet]
AFTER UNDO   0x10E41E01 = 0x00000001 [Inherited (Global profile)]   ... FG absent
RESULT: every setting is back exactly as it started.
```

### Settled: nothing in this feature needs elevation

The save succeeded unelevated, at a trust level *more* restricted than the filtered token a
UAC-enabled administrator account gets. So the DLSS card reads, applies and undoes with **no UAC
prompt anywhere**. The plan assumed elevation would be needed and said so in three places; all of
them were wrong.

### Settled: the inherited-origin rule is right on real hardware

Four of the six settings were inherited from this machine's Global profile. Apply moved them to
`UserSet` on the game's own profile; undo **deleted** them, so they went back to `Inherited` rather
than being pinned. That is the correction made while implementing step 3, now confirmed against the
driver rather than a fake.

### Refuted: `0x00634291`, "the gate"

The recipe had seven settings. The seventh is not a DRS setting on any driver:

```
0x10E41E01  known as "Enable DLSS-SR override"
0x00634291  NOT KNOWN TO THIS DRIVER
0x10E41DF3  known as "Override DLSS-SR presets"
...
```

`NvAPI_DRS_GetSettingNameFromId` does not recognise the id and `SetSetting` refuses it with
`NVAPI_SETTING_NOT_FOUND`. The other six the driver names itself, and those names match the user's
exported `.nip` field for field.

Three consequences:

1. **The recipe is six settings.** The presets work without the seventh - this round trip set them
   and the write-back check confirmed every one.
2. **Spike run 6 is stronger, not weaker.** It wrote the phantom id and the RR preset letter
   together, which the plan recorded as "the pair is proven but the gate is not isolated". Since
   the phantom write did nothing, the preset letter alone produced the change. It was isolated all
   along.
3. **Profile Inspector's setting list is not the driver's.** A name in its XML is not evidence the
   driver has that setting; the `.nip` export never contained this one either. Ask the driver.

**Ask the driver, in code.** `IDrsBackend.GetSettingName` now gates every write: an id the driver
does not have is stepped over as `NotSupportedByDriver` and never recorded, because there is
nothing to undo. NVIDIA adds and retires setting ids between driver versions, and this should
degrade quietly rather than look like a fault.

### How this hid for so long

The first round trip reported `Apply: OK` and the settings looked right, because the harness only
printed **write-back** failures and this was an outright `Failed`. A setting nobody has set and an
id the driver refuses both read as "absent" through a profile, so the before/after comparison
looked clean too. The harness now prints every outcome that is not a clean `Applied` - a reporting
gap is how a real defect stays invisible.

## The pre-launch re-apply, 2026-09-19

`DlssOverrideService.Reapply`, called from `ProcessLauncherService.BeginSession` before the
performance profile. Deliberately outside the profile: DLSS settings are persistent driver state,
not a session tweak, so they apply even when the performance profile is `Off`.

The three-way rule is implemented as specified. One addition it needed: **everything is read before
anything is written.** A single conflicting setting means something else is managing this game, and
writing the remaining five would be exactly the overwrite the rule exists to prevent. So the pass
evaluates all six, and either writes what was reverted or writes nothing at all.

A conflict sets `GameEntry.DlssConflicted`, which stops further re-applying rather than fighting
every launch, and the card explains it. Applying or undoing clears it - both are the user saying
what they want. The records are kept either way, so undo stays available.

A launch never fails because of this: the whole call is wrapped, and a game with no records - which
is almost every game - costs one list check.

### Resolving the rendering executable: the bug this step actually found

The plan flagged this and it turned out to matter more than it reads. A driver profile keys on the
executable that renders, and for a launcher-based game **that is not the one TrayTrigger launches**:

```
Launched : ...\Cyberpunk 2077\REDprelauncher.exe      -> profile "RED Launcher Service"
Renders  : ...\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe -> profile "Cyberpunk 2077"
```

Every apply before this change wrote to the launcher's profile - a process that never renders a
frame. No error, no effect, and the write-back check passed, because the settings really were
written, just somewhere useless. Every GOG, Epic, EA and Ubisoft game would have been affected.

`DlssProbeService.ResolveRenderingExecutable` uses the strongest signal available: **the renderer
is the executable beside the game's DLSS DLLs**, because that is how NGX finds them. Among those the
largest wins after discarding names that are never renderers - a 60 MB game executable against a
260 KB crash reporter. It is a heuristic, and the module scan on a running game is what catches it
being wrong.

### And the harness hid it, again

The round trip read the launched executable's profile for all three snapshots while `Apply` wrote
to the renderer's. It compared a profile nothing had touched and reported *"every setting is back
exactly as it started"* - which was true, and meaningless. That is the second time in this feature
a reporting gap made a real defect invisible; the report now prints both paths and reads the one
that is written.

## Verification, 2026-09-20

`DlssVerificationService` turns a running game's loaded modules into one observation per feature,
persisted on `GameEntry.DlssObservations`. The four states are implemented as specified, and the
wording states the reading and stops - no "as configured", no "this game disallows it".

Two matching rules that are easy to get wrong and are each covered by a test:

- **Store runtimes are matched by folder, game runtimes by file name.** All three substituted
  runtimes are called `160_E658700.bin`; only `models\dlss\` versus `models\dlssd\` versus
  `models\dlssg\` separates them. Matching on the name would report the same runtime for all three.
- **`nvngx_dlss` is a prefix of `nvngx_dlssd` and `nvngx_dlssg`**, so the game-folder match compares
  the whole stem. A prefix match would report three features from one file.

The version of a store runtime comes from its `versions\<n>` directory, not the file: the hashed
`.bin` carries no version resource, so reading one off it would give nothing or something
misleading.

### Observation is polled, not sampled once

A game loads its DLSS runtime when it first builds the renderer - after a launcher, a shader
compile, a main menu. Looking once at process start would see nothing and record "unable to verify"
for a game that was about to work perfectly. The session observer polls every 20 seconds, stopping
at the first runtime seen, when the process exits, or after about five minutes. It only runs for a
game TrayTrigger has applied to, so it costs nothing for the rest of the library.

On exit with nothing seen it keeps an earlier tick's observation rather than overwriting a real
reading with "the process is gone".

### Freshness has two different answers

| What changed | Effect | Why |
|---|---|---|
| The override (apply or undo) | **Cleared** | The observation describes a setup that no longer exists |
| The game's own DLSS version (a patch) | **Cleared** | Same, and now detectable because the shipped version is recorded with the observation |
| The driver | **Kept, marked stale** | What was seen was still seen; it may just not happen again |
| Nothing recorded (an older observation) | **Kept** | Unknown is not a change; discarding would silently erase what users had |

### What this still cannot do

Layer 2 only. A protected title that refuses module enumeration reports `Unable to verify` with the
refusal as its reason - honestly, but it is still a gap, and layers 3 and 4 (NGX logs, the overlay)
are what close it. `PresetObserved` exists in the model and nothing produces it yet, because only
the overlay can.

### Exercised end to end, 2026-09-20 - Cyberpunk 2077

Applied unelevated, the game played, then observed while rendering:

```
[DRIVER STORE] 160_E658700.bin  ...\NGX\models\dlss\versions\20318464\files\160_E658700.bin
[DRIVER STORE] 160_E658700.bin  ...\NGX\models\dlssg\versions\20318464\files\160_E658700.bin
[DRIVER STORE] 160_E658700.bin  ...\NGX\models\dlssd\versions\20318464\files\160_E658700.bin

SR: Observed runtime 310.9.0 from NVIDIA's driver store
RR: Observed runtime 310.9.0 from NVIDIA's driver store
FG: Observed runtime 310.9.0 from NVIDIA's driver store
```

The game ships 310.1.0 and ran 310.9.0 on all three features, with no game file changed. Undo then
returned every setting to `Inherited`/`absent` exactly as captured.

This is also the first real test of the rule that worried me most: **three modules with the same
file name, distinguished only by their folder**, attributed to the right feature each time. A
name-based match would have reported one feature three times, and every unit test for it used
synthetic paths until now.

Streamline's own plugins load from the store too (`sl_dlss_0`, `sl_dlss_g_0`, `sl_reflex_0` at
2.14.0) and are correctly ignored for feature attribution, since they sit outside the three
feature folders.

**One defect found.** `_nvngx.dll` loads from `C:\Windows\System32\DriverStore`, and the report
labelled anything outside the NGX store as "game folder" - so it claimed a Windows file was the
game's. Nothing depended on the label, but it is the third time in this feature that a report has
stated something false, so it now distinguishes the store, the game folder and elsewhere.

**Still not exercised:** the launcher's in-session polling. The observation path is proven; the
20-second poller that drives it automatically during a real TrayTrigger launch has only been
reasoned about.

## The in-session poller, proven 2026-09-20

The last untested link. `--test-dlss-launch <name>` applies to a real library entry, launches it
through the normal launch path and stays running, so the launcher's own poller does the work.

Cyberpunk 2077, launched through TrayTrigger on the GOG route:

```
00:30:40  [Launcher] Dispatched direct GOG launch for 'Cyberpunk 2077'.
00:31:32  [Dlss] 'Cyberpunk 2077' SR: Observed runtime 310.9.0 from NVIDIA's driver store
00:31:32  [Dlss] 'Cyberpunk 2077' RR: Observed runtime 310.9.0 from NVIDIA's driver store
00:31:32  [Dlss] 'Cyberpunk 2077' FG: Observed runtime 310.9.0 from NVIDIA's driver store
```

52 seconds, unattended - the launcher-to-renderer handoff the GOG route makes, the process handle,
the poller and the persistence all worked. The library entry then held the full record: state,
version, store path per feature, the shipped `310.1.0` for patch invalidation, and the driver
`616.64` for staleness.

This closes steps 1 to 6: **every part of the feature has now been exercised against a real game
on real hardware**, not only unit-tested.

### A gap in the harness, not the product

A second TrayTrigger cannot start - the single-instance mutex is correct and stops it - so
`--test-dlss-launch-undo` has to run after the first instance exits. Worth knowing before using
these two flags: launch, play, quit the game, exit TrayTrigger from the tray, then undo. Killing
the instance instead would strand the performance profile's system tweaks until crash recovery
picks them up on the next start.
