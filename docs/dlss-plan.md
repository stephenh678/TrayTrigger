# DLSS model management: design plan

Status: design agreed in discussion 2026-09-19. Not yet scheduled; no branch, no code.
Target: a release after 1.4.6. NVIDIA only.

## Summary

A **DLSS** card in Edit Game that lets a player put a game on NVIDIA's current DLSS model with one
click. It works entirely through the NVIDIA driver's per-game profile: TrayTrigger writes seven
driver settings and the driver loads its own current DLSS runtime for that game instead of the one
the game shipped with.

**No file in any game folder is read for anything but its version, and none is ever written.** That
single decision removes the anti-cheat risk, the download and cache machinery, the backup and
restore machinery, the NVIDIA redistribution question, collisions with Steam's "verify integrity of
game files", file-permission and in-use locks, and the "my game won't launch" support class - all
of which the file-swapping tools (DLSS Swapper, RHI) carry.

Because the driver decides whether an override applies, and NVIDIA warns that some games disallow
it, **verification is part of the feature, not an extra**: TrayTrigger reads the running game's
loaded modules - or, for anti-cheat games that block that, NVIDIA's own NGX logging - and reports
in the app which DLSS actually loaded, and from where. An optional per-game overlay, off by
default, additionally shows the active preset while that game runs.

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
| Applied by default | No. Per game, explicit, opt-in. Nothing changes until the user acts. |
| Preset choice | One option, "Latest". Specific letters are not exposed in v1. |
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

- **Anti-cheat.** Nothing to detect. Anti-cheat validates integrity three ways: hashing files on
  disk inside the game directory, watching for injection (`CreateRemoteThread`, `VirtualAllocEx`,
  unsigned modules mapped into the process), and checking signatures of loaded modules. The driver
  path passes all three - no file in the game directory changes, nothing is injected, and the
  runtime the driver loads is an Authenticode-signed NVIDIA binary mapped by the OS loader as a
  trusted image. This is also NVIDIA's own shipping feature, used by NVIDIA App on
  hundreds of games including protected multiplayer titles, with no warning published anywhere.
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
| The seven setting IDs and their values | **Documented** | `CustomSettingNames.xml`, which carries NVIDIA's own descriptions |
| DLL override works on DLSS 2.0+, presets need 3.1 | **Documented** | NVIDIA's setting description: "Only DLSS2+ games support the override" |
| `0x00634291` gates whether presets apply | **Documented** | NVIDIA's description of that setting |
| Overrides shipped in driver 572.16 | **Documented** | NVIDIA release notes / support article |
| NVIDIA App enforces its allowlist, the driver does not | **Documented** | Third-party analysis of `ApplicationStorage.json` / `fingerprint.db`; consistent with Profile Inspector working on unlisted games |
| **Driver-path overrides carry no realistic anti-cheat risk** | **Inferred** | No files change, nothing is injected, the loaded module is NVIDIA-signed, it is NVIDIA's own shipped feature on protected titles, and no vendor publishes a warning. Independently agreed by a second reviewer citing zero documented bans - but that is still absence of evidence, not a test. |
| **The 3.1 floor is about the game's API integration, not the model** | **Inferred** | Fits the observed cutoff; NGX's evaluated-feature exports hold C-ABI compatibility across 2.x/3.x, which would allow a newer runtime to serve an older integration. NVIDIA does not document the reason. |
| **Writing settings pre-launch defeats NVIDIA App reverting them** | **Inferred** | Reviewer reports NVIDIA App reconciles on its own startup, driver update or library scan, and does not hook process creation. Plausible and matches observed behaviour; untested by either party. |
| **`NvAPI_DRS_SaveSettings` needs elevation** | **Inferred** | Reported access-denied issues and the ProgramData location; RHI claims some settings work unelevated |
| **Combining DLL override + preset works on a DLSS 2.x game** | **Untested** | The single highest-value unknown. The spike settles it. |
| Module enumeration reveals the loaded DLL's path and version | **Verified** | `Process.Modules` read against a live process; not yet against a real DLSS module |
| Anti-cheat blocks module enumeration | **Documented** | Standard EAC/BattlEye/Vanguard behaviour; not tested here |
| The overlay renders on anti-cheat titles | **Documented** | It is an NVIDIA-signed swapchain hook, not third-party injection; reviewer-supplied, untested here |
| The overlay shows the game's own version when the override is declined | **Documented** | Reviewer-supplied; makes layer 4 a complete check. Confirm in the spike. |

Nothing in this plan has been tested by actually writing a driver setting. **The entire mechanism is
believed, not demonstrated** - which is what the spike before step 3 exists to fix.

## What can and cannot be read back

This constrains the UI and must not be papered over.

| Value | Readable | How |
|---|---|---|
| DLSS version per feature | Yes | `FileVersionInfo` on the game's `nvngx_dlss*.dll` |
| Whether an override is set, and its value | Yes | `NvAPI_DRS_GetSetting`, which also reports whether the value is user-set or NVIDIA's predefined default |
| **The preset a game actually uses when no override is set** | **No** | Chosen by the game's runtime per quality mode. Nothing on disk records it. |

So an untouched game shows its version and the word `Game default`. **Never display a preset letter
we cannot verify.** Inventing one would be worse than saying less.

## Driver settings written - the "force latest" recipe

Values below are taken from `nvidiaProfileInspector/CustomSettingNames.xml` in
[Orbmu2k/nvidiaProfileInspector](https://github.com/Orbmu2k/nvidiaProfileInspector), group
"05 - Upscaling and Frame Generation", which carries NVIDIA's own setting descriptions. This is the
authoritative list; earlier drafts of this plan were incomplete.

**Two things are needed, not one.** Forcing the newest runtime and forcing the newest model are
separate settings, and there is a third that gates whether the preset is honoured at all.

| Setting ID | Name | Value | Purpose |
|---|---|---|---|
| `0x10E41E01` | DLSS - Enable DLL Override | `1` | Load the newest **installed** DLSS-SR runtime instead of the game's |
| `0x00634291` | DLSS - Forced Model Preset Profile | `1` = Recommended | **Gates whether preset overrides apply at all** |
| `0x10E41DF3` | DLSS - Forced Preset Letter | `0x00FFFFFF` = Use recommended preset | Which SR model |
| `0x10E41E02` | DLSS-RR - Enable DLL Override | `1` | Ray Reconstruction runtime |
| `0x10E41DF7` | DLSS-RR - Forced Preset Letter | `0x00FFFFFF` | Which RR model |
| `0x10E41E03` | DLSS-FG - Enable DLL Override | `1` | Frame Generation runtime |
| `0x10E41DF1` | DLSS-FG - Forced Preset Letter | **`0x00FFFFFE`** | Which FG model |

### Traps

- **`0x00634291` is the one everyone misses.** NVIDIA's description: *"If 'Forced Preset Letter' has
  no effect, this setting may need to be changed for the game to apply the custom preset."* Without
  it the preset settings can silently do nothing. Values: `0` N/A, `1` Recommended, `2` Custom. Use
  `1`, which is also NVIDIA's own per-GPU, per-quality-mode selection logic - **this resolves the
  earlier open question about reproducing "Recommended" outside NVIDIA App.**
- **Frame generation's "recommended" is `0x00FFFFFE`, not `0x00FFFFFF`.** Different by one from
  every other preset setting. Getting this wrong writes a nonsense value.
- **Preset letters are not a single scale across features.** For SR, `0x4` is Preset D (CNN) and
  `0xA`/`0xB` are J/K (Transformer Gen 1), `0xC` is L (Gen 2). For **RR**, `0x4` is Preset D but it
  is *Transformer Gen 1*, and `0x6` is F (Gen 2). Never share a letter-to-value map between
  features.

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

In .NET this is `Process.Modules`, giving `ModuleName`, `FileName` and `FileVersionInfo`. Confirmed
working on the development machine (read against a live process; no game was running at the time to
read a real DLSS module).

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

### Layer 3: NGX log parsing (fallback, one game session)

NVIDIA's NGX runtime can log what it loaded. All three keys live under
`HKLM\SOFTWARE\NVIDIA Corporation\Global\NGXCore`, and NVIDIA ships the `.reg` files for them in
`utils/` of their public DLSS repo:

| Value | Type | Meaning |
|---|---|---|
| `LogLevel` | dword | `1` on, `2` verbose, absent/`0` off |
| `EnableLogPathOverride` | dword | `1` to redirect logs |
| `LogPath` | string | Where to write them |

Log files are named for what loaded - `nvngx_dlss_310_3_0.log`, with `dlssd*` for Ray Reconstruction
and `dlssg*` for Frame Generation - **so the loaded version is in the filename** and no parsing of
log contents is needed for the basic answer.

`EnableLogPathOverride` + `LogPath` means TrayTrigger can point logs at its own folder rather than
polluting NVIDIA's, and clean up afterwards.

This is a **session tweak**, unlike the DLSS settings themselves: turn logging on before launch,
turn it off when the session ends, read the log, report. That is exactly the
`PerformanceProfileSessionSnapshot` capture/apply/restore pattern already in the codebase,
including crash recovery - if TrayTrigger dies mid-session, logging must still be turned back off
on next start.

`ISystemTweakBackend.WriteHklmDword` already exists and is the right seam. `LogPath` is a string, so
`WriteHklmString`/`DeleteHklmValue` cover the rest. No new backend concepts.

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
- **Machine-wide while it is on.** A second DLSS game running concurrently also shows the overlay.
  A real but narrow edge case, and the profile service's existing "first wins" handling of
  machine-wide singletons is the precedent to follow.
- **Needs elevation**, like the other HKLM writes.
- **Diagnostic, not a feature.** Word it as one, keep it off by default, and do not promote it in
  the CHANGELOG as an image-quality option.

### Where the result is shown

In the DLSS card itself, as a status line under the versions - not a dialog, not an overlay. The
card always shows the current versions; after a verified session it also shows what actually
loaded.

Before any session:

```
DLSS                                  [ Use latest ]  [ Verify ]
  Super Resolution     310.2.1      Game default
  Frame Generation     3.5.0        Game default
  No game files are changed - the driver supplies the latest model.
```

After a verified session where it worked:

```
DLSS                            [ Restore default ]  [ Verify ]
  Super Resolution     310.2.1  ->  loaded 310.9.0
  Frame Generation     3.5.0    ->  loaded 310.9.0
  Verified on 19 Sep. The driver supplied a newer model, as configured.
```

After a verified session where it did not:

```
DLSS                            [ Restore default ]  [ Verify ]
  Super Resolution     310.2.1  ->  loaded 310.2.1
  The driver did not substitute - this game appears to disallow the override.
```

That last message is the reason verification is mandatory rather than optional. NVIDIA's own
description warns that "certain games may also disallow using it", so failure is an expected
outcome, not a bug. The product must say so plainly rather than leave the user believing a setting
did something it did not.

`Verify` turns NGX logging on for one session, launches the game, and reads the log on exit. The
result is stored per game so the card can show it without re-running.

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
DLSS                                              [ Use latest ]
  Super Resolution     310.2.1      Game default
  Frame Generation     3.5.0        Game default
  No game files are changed - the driver supplies the latest model.
```

**After applying, before verifying:**

```
DLSS                            [ Restore default ]  [ Verify ]
  Super Resolution     310.2.1      Latest (driver)
  Frame Generation     3.5.0        Latest (driver)
  [ ] Show DLSS overlay while this game runs
  Set by TrayTrigger. Play the game once and press Verify to confirm.
```

**Old game** - offered, not excluded (see The version floor):

```
DLSS                                  [ Use latest ]  [ Verify ]
  Super Resolution     2.3.7        Game default
  This game's DLSS predates preset support. The override may still work - Verify will say.
```

**No DLSS found:** the card is not shown.

See Verification for the two verified states.

Rules:

- One action plus `Verify`. No version dropdown, no per-feature toggles, no preset letters in v1.
- The line "No game files are changed" is load-bearing. It is the sentence that reassures a user who
  has heard swapping DLLs is risky.
- `Restore default` clears the settings; it does not write an "off" value.
- If an override is present that TrayTrigger did not set (NVIDIA App, Profile Inspector), say so and
  do not silently overwrite it. Follow the `DefenderExclusionWasPreExisting` discipline already used
  for Defender exclusions.

## Settings

One entry under Settings > General, off by default:

- **Apply the latest DLSS model to new games automatically** - when a game is added and qualifies,
  apply without asking. Off by default; the per-game control is the primary path.

No global "apply to all" button in v1. It is a large, silent, hard-to-undo change across a whole
library.

## Data

On `GameEntry`:

```
DlssOverrideEnabled    bool      TrayTrigger set the override for this game
DlssVerifiedVersion    string?   What actually loaded, from the NGX log ("310.9.0")
DlssVerifiedUtc        DateTime? When that was observed
DlssVerifiedDriver     string?   Driver version at the time ("616.64")
DlssShowOverlay        bool      Session-scoped diagnostic overlay for this game (default false)
```

Versions on disk and the override values are read live - from `FileVersionInfo` and from the driver
respectively - so neither is cached. Only the verification result needs persisting, because it can
only be learned by playing the game and the card should not go blank between sessions.

Clear the verification fields whenever the override is changed or cleared, or the game's own DLSS
version changes: a stale "verified" line is worse than none. When only the **driver** has changed
since, keep the result but mark it stale - the finding is still informative, it is just no longer
current evidence about what "latest" resolves to.

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
| A user blames TrayTrigger for an unrelated graphics problem | Medium | Support load | `Restore default` is one click and the card states exactly what was changed |
| Anti-cheat flags it despite the reasoning | **Low** | **Severe** | Nothing on disk changes; this is NVIDIA's own feature. Accepted risk, documented as inferred. |

The bottom row is the one worth arguing about. It is the only entry whose impact would be serious,
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
- **No GPU, no card.** Hide the card entirely on a machine with no NVIDIA GPU.

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

1. Detection and read-only display. The card shows versions and `Game default`, with no action
   button. Verifiable on its own and useful for deciding whether the rest is worth it.
2. NVAPI DRS interop behind `ISystemTweakBackend`, with the fake for tests. No UI.
3. Apply and clear, wired to the button, with the pre-existing-override check and the layer 1
   write-back check.
4. Re-apply in the pre-launch step.
5. **Verification: NGX logging as a session tweak, and the `Verify` action.** Do this before
   shipping, not after - overriding non-validated games is only defensible if the product can tell
   the user when it did not work.
6. The per-game overlay toggle, reusing the layer 3 session-tweak plumbing.
7. The Settings entry for new games.
8. Help topic and CHANGELOG bullet.

Steps 1 and 2 are independent and can be done in either order. Step 5 depends on 3.

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

Answers produced: does the override work at all; does it work below 3.1; is the `0x00634291`
recipe right; does anti-cheat block enumeration; and which indicator value works on retail builds.

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
