# Roadmap

Started 2026-10-05. The previous roadmap is at
[archive/roadmap-2026-10-05.md](archive/roadmap-2026-10-05.md), including what shipped up to 1.5.0.

**Nothing below is decided.** The owner asked for the research to be redone without the earlier
decisions. Declines and the "TrayTrigger is a launcher" scope rule in the archived roadmap are
history, not constraints, until the owner restates them. This file holds the evidence and a
proposed roadmap; decisions get recorded here as they are made, with a date.

## How this was researched (2026-10-05)

- **Read directly:** the code, GitHub star counts and issue trackers, vendor forums, tech press.
- **Reddit:** cannot be read directly (web search and both browsers refuse reddit.com). It was
  searched through the gemini-search MCP server. Those findings are Gemini's summaries of threads,
  not first-hand reads, and carry no vote counts. Several summaries leaned on YouTube and blog
  sources rather than Reddit; those are marked "thin" below.
- **Searches that returned nothing:** refresh rate on its own (3 tries), playtime tracking and
  stats (2 tries), whether users refuse unsigned tools (1 try, 1 thin retry). No result is not
  evidence of no demand.

## Demand by GitHub stars (read 2026-10-05)

Stars measure interest in a job, not in a feature TrayTrigger would build the same way.

| Job | Tool | Stars |
|---|---|---|
| Windows debloat and tweaks | WinUtil / Win11Debloat / Atlas OS / Optimizer | 63.6k / 58.0k / 21.8k / 18.3k |
| Game streaming host | Sunshine / Apollo | 41.9k / 11.2k |
| Fan curves | Fan Control | 21.0k |
| Laptop and handheld control | G-Helper / Universal x86 Tuning Utility / Handheld Companion | 15.5k / 2.9k / 1.7k |
| Window scaling | Magpie | 15.3k |
| Library front end | Playnite / Heroic | 14.1k / 12.3k |
| Upscaler swap and override | OptiScaler / DLSS Swapper | 11.6k / 7.6k |
| Audio volume and device | EarTrumpet / SoundSwitch | 11.4k / 3.4k |
| Monitor brightness | Twinkle Tray | 9.1k |
| NVIDIA per-game driver settings | NVIDIA Profile Inspector | 7.3k |
| Borderless window | Borderless Gaming (last push Sept 2025) | 6.6k |
| Save backup | Ludusavi | 6.4k |
| Games into Steam as shortcuts | Steam ROM Manager / BoilR | 2.6k / 1.9k |
| Frame-time measurement | PresentMon / CapFrameX | 2.6k / 1.3k |
| Suspend a game | Nyrna | 1.3k |
| Display mode for streaming sessions | ResolutionAutomation / MonitorSwapAutomation | 1.0k / 0.6k |
| Per-app display, HDR and audio | AutoActions (last release March 2025) | 0.8k |

## Gaps in existing tools, by priority (2026-10-05)

Ranked by how strong the evidence of demand is and how badly existing tools serve the need. This
is a ranking of gaps, not a build order. The last column maps each gap to the proposed roadmap
below. As of 2026-10-05 the owner has not decided what to work on next.

| # | Gap | Where existing tools fall short | Evidence | TrayTrigger today | Covered by the proposed roadmap? |
|---|---|---|---|---|---|
| 1 | Per-game HDR that puts itself back | Windows has no per-game switch. Games leave HDR on after closing. AutoActions is unmaintained and crashes. Special K risks bans. Colour profiles break after a toggle on 24H2/25H2. | Strongest found | Partly: turns HDR on and restores it, but only for games with the option ticked | **Yes.** 1.6: finish per-game HDR (restore after any game, colour-profile fix, Auto HDR per game) |
| 2 | Settings that apply however the game is started | Each launcher only handles its own games. Automation tools react after the game is running, too late for HDR, resolution or audio. Playnite needs hand-written scripts. | Strong (structural, plus Reddit asks for Stream Deck and shortcut launching) | No: only games started from TrayTrigger | **Yes for Steam, GOG and shortcuts; partly for the rest.** 1.6: launch-in and desktop shortcuts, Steam/GOG wrapper. 2.0: late attach, with a reduced profile, for games started from Epic, EA, Ubisoft, Battle.net or Xbox |
| 3 | Display and audio set up for a game or a TV, then restored | AutoActions fails to return to native resolution. DisplayFusion is paid. Older NirSoft scripts fail on newer Windows. Sunshine leaves the display changed after a stream. | Strong for resolution, audio device and TV; none found for refresh rate alone | No | **Mostly.** 1.7: display mode per game, audio device per game, Play on the TV with a Sunshine/Apollo recipe. Not covered: turning other monitors off |
| 4 | Changes that undo themselves, including after a crash | Razer Cortex leaves services stopped and PCs frozen. Process Lasso rules persist and get forgotten. Tweak scripts are permanent. | Strong | Yes: this is the core of the app | **Already filled, and strengthened.** 1.6: "Restore everything now". Engineering track: the snapshot extended to display mode and audio device before 1.7 |
| 5 | Honesty about anti-cheat | Process Lasso gets blocked or kicked with no explanation. AutoHotkey stops some games launching. OptiScaler and Special K risk bans. Handheld Companion's driver is flagged by Defender. | Strong | Partly: Suspend refuses protected games; nothing reports a blocked change | **Yes.** 1.6: anti-cheat reporting, Vanguard and Ricochet detection, the detector consulted for profiles |
| 6 | Proof that a tweak helped | Boosters are called placebo. No tool ties FPS and 1% lows to the settings in use. CapFrameX is manual. | Medium: the community insists on 1% lows; demand for automatic comparison is inferred | No | **Partly.** 2.0: FPS and 1% lows per session, only when PresentMon is installed. Not covered: an automatic before-and-after comparison |
| 7 | One light place for every store, no plugins or logins | Playnite takes 30 seconds to 5 minutes to start with themes and extensions. GOG Galaxy logins expire. Launchers stay running after the game. | Strong | Yes | **Already filled; no new feature.** Engineering track: measured start-up and memory, and the Steam poll replaced by an event |
| 8 | Per-game GPU driver settings in one place | NVIDIA splits them across two tools, hides some, and they need re-checking after driver updates. AMD users have no safe automation for FSR 4. | Medium (the NVIDIA summary cited mostly YouTube) | Partly: DLSS Override and global NVIDIA settings | **Yes for NVIDIA, no for AMD.** 2.0: per-game NVIDIA settings. AMD parity is cut until it can be tested |
| 9 | Multi-monitor comfort | The cursor escapes to the second screen. No built-in way to dim other screens. Borderless Gaming is stale. | Medium to strong for the cursor; medium for dimming | No | **Yes.** 1.8: keep the cursor in the game, dim other monitors (both built on the foreground watcher). Not covered: a borderless window per game |
| 10 | Save backup inside the launcher | Many games have no cloud saves. Ludusavi is a separate tool. GameSave Manager is slow and closed-source. | Medium to strong | No | **Yes, if Ludusavi is installed.** 2.0: save backup through Ludusavi |
| 11 | Hardware profiles per game | MSI Afterburner can't switch profiles per game. Fan curves live in another tool. | Medium | Partly: Tools start with games, but with the same arguments for every game | **Yes, for tools that take a command-line profile.** 1.7: per-game arguments for Tools |
| 12 | Power behaviour by battery or mains, without a driver | Handheld Companion needs a flagged kernel driver. Armoury Crate is called bloat. | Medium | No | **Partly.** 1.6: battery rule for profiles. Not covered: TDP control, which is cut |
| 13 | Core assignment without the confusion | Process Lasso's CPU Sets are hard to set up, and the free version nags. | Medium and shrinking: Process Lasso added a one-click button, and Windows 11 24H2 scheduling improved | Yes | **Already filled; no new feature.** 1.6's anti-cheat reporting will say when a core setting was blocked |
| 14 | Mute other apps while playing | EarTrumpet and SoundSwitch are manual. | Weak: asserted by Gemini and the Antigravity review, not found in the searches | No | **Yes.** 1.8: mute other apps while the game has focus |
| 15 | Background apps closed for a game and brought back after | Razer Cortex is distrusted and leaves PCs frozen. Scripts close apps but nothing brings them back after a crash. Windows Update downloads mid-game are a named stutter complaint. | Medium: found in both research rounds; the second leaned on blogs | Partly: a bundled script, with no crash recovery | **Not in the four releases.** See "Additions" below |

Gap 15 was added after the ranking; by evidence it would sit around tenth.

**What the proposed roadmap leaves open**

- AMD graphics users (gap 8): nothing until someone can test on AMD hardware.
- Games started from a non-Steam launcher's own Play button (gap 2): they get only the reduced
  late-attach profile, and not until 2.0.
- Turning other monitors off (gap 3).
- A borderless window per game (gap 9) and built-in background apps (gap 15): not in the four
  releases, but listed under "Additions" with a suggested place.
- Before-and-after comparison (gap 6): the numbers are recorded per session; comparing them is
  left to the user.
- TDP control (gap 12): cut because it needs a kernel driver.

## Proposed roadmap (2026-10-05, awaiting the owner's decisions)

Worked out in three rounds between Claude (with the code open) and Gemini (`gemini_ask`,
gemini-3.1-pro-preview, reasoning only), starting from the research above and a second review the
owner ran in Antigravity. Both planners agree on everything in this section. It is a proposal:
several items reverse the archived scope rule, and those are listed under "Decisions for the owner".

### The launch model

Today a session exists only for a game started from TrayTrigger's own tray menu, hotkeys or
window. The proposal is three ways in, in order of preference:

1. **Launch-in:** `TrayTrigger.exe --launch <game>`, and a "Create desktop shortcut" command that
   uses it. Covers Stream Deck, Playnite, Big Picture, Sunshine and the Start menu.
2. **Wrapper for Steam and GOG Galaxy:** `"TrayTrigger.exe" --wrap %command%` as the game's launch
   option. Steam hands the game's command to TrayTrigger, which applies the profile, starts the
   game and restores afterwards. This keeps the head start that HDR, display mode and audio device
   need, for games started from Steam itself.
   - The wrapper is a second instance that passes the command to the running one over a named
     pipe, then stays alive until the game exits so Steam shows the game as running.
   - If the pipe breaks (Steam's Stop button), the running instance restores at once.
   - TrayTrigger offers "Copy Steam launch option"; it never edits Steam's files.
   - Epic, EA, Ubisoft, Battle.net and Xbox have no equivalent. They use shortcuts or late attach.
3. **Late attach (optional, last):** notice a library game that was started elsewhere and attach
   a reduced profile. Fixed rules:
   - Off by default; turned on by the user, per game or for all games.
   - One out-of-context `EVENT_SYSTEM_FOREGROUND` hook. Never `EVENT_OBJECT_CREATE`.
   - Applies only what is safe on a running game: power plan, CPU Cores, priority, throttling
     exemption, Do Not Disturb, timer, Tools, mute-others, exit restore, post-exit script.
   - Never changes HDR, display mode or audio device. Never runs a pre-launch script.
   - Activity & History says the session was attached late and what was skipped.

**Refused:** Image File Execution Options (anti-cheats treat a debugger key as tampering), and
anything needing elevation to watch processes (WMI process traces, kernel ETW).

### Releases

Sizes are for one developer: S is days, M is a week or two, L is longer.

**1.6 "Start anywhere, trust it"**

| Item | Size | Wins users of | Acceptance test on one PC |
|---|---|---|---|
| Launch-in and desktop shortcuts | S | Playnite, Stream Deck | A shortcut starts the game with its profile while TrayTrigger is already running, and when it is not. |
| Steam and GOG wrapper | M | Steam-first players | Start from Steam's Play button: profile applies before the game opens, Steam shows it running, restore happens on exit and on Steam's Stop. |
| Finish per-game HDR | M | AutoActions, home-made HDR togglers | A game that turns HDR on by itself leaves the display as it was before launch. Colour profile is correct after the toggle on 24H2/25H2. Auto HDR can be set per game. |
| Anti-cheat reporting | S-M | Process Lasso | An Easy Anti-Cheat game with CPU Cores set: Activity & History names the anti-cheat and the item it blocked. Vanguard and Ricochet are detected. |
| Restore everything now | S | everyone | Tray item and hotkey put back every setting a session changed, mid-game. |
| Battery rule for profiles | S | laptop and handheld owners | On battery (faked in tests), the power plan, timer and max-performance items are skipped or stepped down, and the log says so. |

**1.7 "Screen and sound"**

| Item | Size | Wins users of | Acceptance test on one PC |
|---|---|---|---|
| Display mode per game (resolution and refresh rate as one setting) | M | AutoHotkey + QRes scripts, AutoActions | Game launches at 1080p from a 1440p or 4K desktop; desktop mode returns on exit, after a game crash and after a TrayTrigger crash. Frame cap is worked out from the new rate. |
| Audio output device per game | M | SoundSwitch, Playnite's audio extension | Game plays through the chosen device from its first sound; the previous default returns on exit. A missing device is reported, not fatal. |
| Play on the TV, plus a Sunshine and Apollo recipe | M | DisplayFusion, Monitor Profile Switcher, ResolutionAutomation | With two displays: the game opens on the chosen one with its mode, HDR and audio, and everything returns afterwards. Sunshine runs a copied TrayTrigger command and the desk display is restored when the stream ends. |
| Arguments for Tools, per game | S | MSI Afterburner, Fan Control | A tool starts with a different argument for two games. |

Turning other monitors off is not in 1.7. It is where AutoActions crashes, and it waits until
display mode switching has been through a release.

**1.8 "Focus"**

| Item | Size | Wins users of | Acceptance test on one PC |
|---|---|---|---|
| Foreground watcher (shared plumbing) | M | n/a | Focus gained and lost by the game is logged correctly through alt-tab, with no measurable CPU use while idle. |
| Keep the cursor in the game; dim other monitors while it has focus | M | Dual Monitor Tools, Twinkle Tray | With two displays: the cursor cannot leave the game's screen, the other screen dims, and both undo on alt-tab and on exit. |
| Mute other apps while the game has focus | S | EarTrumpet | A browser playing sound goes silent when the game has focus and returns on alt-tab and on exit. |

Dimming uses a black window on the other screens, so there is nothing to restore after a crash.

**2.0 "Proof and ecosystem"**

| Item | Size | Wins users of | Acceptance test on one PC |
|---|---|---|---|
| FPS and 1% lows per session, when PresentMon is installed | M | CapFrameX | After a session, Activity & History shows average FPS and 1% lows beside the profile that was active. No elevation. |
| Per-game NVIDIA driver settings (Low Latency, V-Sync, frame limit, RTX HDR, Smooth Motion) | L | NVIDIA Profile Inspector | Each setting applies for one game and returns to its previous value on Restore, like DLSS Override. |
| Save backup through Ludusavi | M | GameSave Manager, Playnite + Ludusavi | After exit, Ludusavi backs up that game before the launcher is closed. |
| Late attach | L | AutoActions, Process Lasso | An Xbox game started from the Start menu gets CPU Cores and the power plan, HDR is left alone, and the log says "attached late". |
| Controller chord for suspend, close game and restore | S | couch players | L3+R3 (configurable) suspends and resumes the running game. |
| Launchers in efficiency mode while a game runs | S | Process Lasso | EA app shows as efficiency mode during the game and normal after. |

Late attach was moved from 1.8 to 2.0 at Gemini's suggestion: it changes the session state
machine, so the watcher should first prove itself on the focus features. 2.0 is large and may
need splitting.

**Engineering track (alongside any release)**

- A debug check that records cold-start time, idle private memory and thread count, so footprint
  claims are measured.
- Replace the 2-second Steam registry poll with `RegNotifyChangeKeyValue`.
- Split `ProcessLauncherService` and `SystemTweaksService` (about 2,000 lines each) by launch
  route and by tweak family.
- Before 1.7, extend the session snapshot to hold display mode and audio device, and test
  restore after a TrayTrigger crash for both. Both planners see leftover state as the biggest
  risk in this roadmap: a wrong resolution or sound sent to a TV that is off.

**Cut**

- A separate Native AOT background agent with the WPF window started on demand. Revisit only if
  measured idle memory is over 80 MB or cold start is over 3 seconds.
- TDP control and anything else needing a kernel driver. Anything that injects into a game.
- A controller overlay or couch interface. Big Picture and Playnite Fullscreen own that, and
  launch-in lets them start TrayTrigger sessions.
- Shader pre-warming (not possible from outside a game) and per-game shader cache sizes (the
  driver setting is global, and 1.5.1 already sets it to Unlimited).
- Closing a launcher while its game runs (breaks DRM, cloud saves and achievements).
- Turning monitors off, for now. AMD parity, until someone can test on AMD hardware.
- Input macros, RAM cleaning, network resets, "boost" scores.

### Additions (2026-10-05, after the plan above was agreed)

The owner asked for these to be added after comparing the plan with the archived roadmap. They
were not part of the discussion with Gemini. The placements are Claude's suggestions.

**From the archived roadmap**

| Item | What it is | Suggested place |
|---|---|---|
| Tools started per game | Choose which games a tool starts with, in Edit Tool, Edit Game and the batch menu. The archived roadmap has the full design ("Tools started per game", parked 2026-10-04). | 1.7, merged with "Arguments for Tools, per game": a profile switch for Afterburner needs both. |
| Background apps while you play, built in | Close the apps the user names and reopen them after: shipped in 1.6.0 as Edit Tool's "When I launch a game: Close it" with "Open it again when the game exits". Still open: put apps in efficiency mode instead of closing them, and pause Windows Update and Delivery Optimization for the session (needs administrator rights). Fills gap 15. No RAM cleaning and no "boost" score. | 1.8, beside "mute other apps": both act on other apps for the length of a session. |
| Borderless window per game | Make a windowed game fill the screen without a border. Steady demand in the archived research; the main open-source tool is stale. Tied to gap 3: borderless-only games are the top reason people want resolution switching. | After 1.8. It can use the foreground watcher. |
| Library sync between PCs | Playnite's cloud-sync request has 22 upvotes and LaunchBox has it. Backup is a local zip today. | Unplaced. Cheapest form: let Backup write to a folder the user already syncs, and offer to restore when that copy is newer. |
| Stopping launchers updating games during play | Came up in the archived research and was ruled out under the old scope rule. Never researched. | Unplaced. Research first. |
| Several launch options per game | Mostly DX11 against DX12 today, done with launch arguments. | Unplaced, small. Fits naturally once `--launch` exists (a shortcut per variant). |
| Play stats and Steam playtime import | No evidence found in either round. | Leave out unless evidence appears. |
| "Will it run?" | The archived research found evidence against it. Not re-researched. | Leave out. |

**From earlier in this review**

| Item | What it is | Suggested place |
|---|---|---|
| Riot and Rockstar scanners | The logos are already bundled, and `docs/adding-a-platform-integration.md` describes the steps. Riot depends on Vanguard detection. | After 1.6's anti-cheat work. |
| Check the window on a variable-refresh display | Playnite users report stutter from WPF windows with G-Sync or FreeSync, and on hybrid-GPU laptops. TrayTrigger is also WPF. | Engineering track. A test, not a feature; the developer's 240 Hz display can run it. |
| Tell the people already asking | r/OLED_Gaming threads ask for per-game HDR and accept developer posts for HDR tools. No single tool is recommended for HDR plus closing launchers plus restoring settings. | When 1.6's HDR work ships. |

### Rules every new feature follows

Set by the owner for Activity & History in 1.5.0 and for Backup & Restore in 1.4.7. Restated here
because the plan above adds settings and events that must obey them.

- **Activity & History is a history of important activities, not a log file.** One row per game
  exit. Upkeep and background housekeeping are not recorded.
- **Nothing interrupts a game.** No notifications while a game runs; one summary after the last
  game exits.
- **Anything left changed is a Critical entry with Restore Previous, then FIXED.** This applies to
  display mode, audio device, HDR, and whatever late attach and the wrapper change.
- **A change anti-cheat blocked is a Problem entry**, as is a missing audio device or display.
- **Backup & Restore carries every per-game setting.** A per-game display or audio device will
  not exist under the same ID on another PC; restore has to say so and ask, as it already fixes
  drive letters and launcher folders.

### What the Antigravity review changed

| Its proposal | Verdict | Why |
|---|---|---|
| Passive game detection as the top item | Adopted in a narrower form | Its point about how people start games is right. Process-creation events need elevation, and a foreground hook arrives too late for HDR, display mode and audio. The wrapper gets the same result for Steam with no downside; late attach covers the rest with a reduced profile. |
| Resolution and refresh rate per game | Adopted | 1.7. |
| Turning secondary monitors off "to save GPU memory and remove stutter" | Replaced | No evidence for that benefit. The evidenced needs are dimming and the cursor escaping, in 1.8. |
| Controller chord on the Guide button, radial overlay | Reduced | Windows reserves the Guide button. A chord on ordinary buttons for existing actions is in 2.0; the overlay is cut. |
| TDP automation | Cut | Needs a kernel driver. The battery rule is in 1.6. |
| Per-game audio device | Adopted | 1.7. It needs an undocumented Windows interface, not the one the review named. |
| Muting background apps | Adopted | 1.8. Supported API; TrayTrigger already mutes per process for Suspend. |
| Shader pre-warming and per-game cache policy | Cut | See Cut. |
| Native AOT, WinUI 3, trimmed WPF | Cut | WPF cannot be trimmed or AOT-compiled; WinUI 3 is not lighter. |
| Events instead of polling | Adopted in part | `RegNotifyChangeKeyValue` for Steam and one foreground hook. Not `EVENT_OBJECT_CREATE`, which fires for every window on the system. |
| Frame-time summary after a game | Adopted | 2.0, through PresentMon's service so no elevation is needed. |
| Supervisor for Lossless Scaling and RTSS | Covered | Tools already start them with a game; per-game arguments in 1.7 complete it. |

Two of the review's claims about today's code were wrong and are not repeated here: that
TrayTrigger carries "zero risk" with anti-cheat (no tool can promise that), and that it handles
HDR "without tripping" the 24H2 colour quirks (the help page documents one).

### Decisions for the owner

1. **Launch-in and the wrapper.** They reverse the archived rule that TrayTrigger does not start
   games from outside itself. Everything in 1.7's TV and streaming work depends on them.
2. **Late attach.** Build it at all, and if so, how visible to make it. Both planners would keep
   it off by default and behind the wrapper, so people learn the safer path first.
3. **The undocumented audio interface.** Changing the default device has no supported API. Every
   tool that does it uses the same unofficial one. Accept that, or leave audio device out.
4. **Signing.** Smart App Control blocks unsigned apps with no override. Options: SignPath
   Foundation (free for open source), Azure Trusted Signing (about $10 a month), or a standard
   certificate. A Microsoft Store package is not an option: it redirects registry writes, which
   breaks the system tweaks.
5. **Test hardware.** The battery rule needs a laptop to confirm, and the TV, cursor and dimming
   work needs a second display. AMD parity stays cut without an AMD card.

### To verify before building

- PresentMon's service can be read by a process that is not elevated.
- Whether TrayTrigger's HDR toggle shows the 24H2/25H2 colour-profile bug.
- Steam's overlay and playtime tracking behave normally through the wrapper, and what Steam's
  Stop button does to the wrapper process.
- GOG Galaxy accepts the wrapper as a custom executable.

## What Reddit says about new tools in this space

Praised: one place for every store, light on resources, open source, clear about what it changes.
Criticised: needing elevation without a reason, resource use, feature bloat, ads, and offering
nothing Playnite doesn't. TrayTrigger's answer to the last one is the session layer, not the
library.

## Competitor notes

- **Playnite** (14.1k stars): startup of 30 seconds to 5 minutes and 1.5 GB RAM reported with
  themes and extensions. Playnite 11 alpha (April 2026) stays on WPF and promises better launcher
  handling, so part of TrayTrigger's gap may close.
- **Process Lasso:** still recommended for X3D CPUs, but it added a one-click cache-CCD button in
  March 2026, and some 2026 threads say Windows 11 24H2 scheduling makes it unnecessary.
- **AutoActions:** praised for per-game HDR; open issues show crashes on monitor changes, a
  resolution not put back, and exit actions not firing.
- **Xbox full screen experience:** on all Windows 11 handhelds since November 2025 and reported
  on desktop PCs from April 2026. It replaces the taskbar and tray and defers startup apps.
