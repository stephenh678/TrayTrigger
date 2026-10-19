# Roadmap v2

Written 2026-10-17, after 1.7.0 shipped. It is a ranked idea list, ordered by how much gamers want
each item and how much it would change TrayTrigger, not by effort. It does not replace
[roadmap.md](roadmap.md), which holds the earlier research and the owner's recorded decisions
(including what was declined). Nothing below is scheduled; items move into a release only when the
owner says so.

## Rules this list follows

- **Proven benefits only (owner, 2026-10-17).** A tweak or claim is added only with independent,
  measured evidence. Forum posts, YouTube claims, guides and search summaries are leads, not proof.
  Where evidence is thin, say so in the app and in the notes.
- **Report, don't tweak, unless proven.** Stutter Check rows detect and point; they do not apply
  tweaks of their own.
- **Nothing interrupts a game, and anything left changed is put back.** Unless an item below says it
  is an opt-in exception.
- **Still declined (see roadmap.md, 2026-10-13):** launch-in command, the Steam/GOG wrapper, late
  attach, closing launchers globally, muting other apps, a Clear Shader Cache button.
- **Windows 26H2** (rolled out 2026-09-29 as a 174 KB enablement package): no credible gaming
  benchmark found. Make no performance claim for it.

## How this list was made

Four rounds of searches through the Gemini search connection (launcher complaints, PC gaming pain
points, handhelds, storage, version rollback, privacy and trust), plus direct searches for tweak
evidence. The Gemini results are summaries of web and Reddit pages, not first-hand reads and not a
survey. Treat every size and percentage as a lead until measured.

What gamers say they want: fewer launchers (six to eight are common) and no launcher inside a
launcher; games that run without shader stutter or crashes; updates that do not silently break
things (power plans reset, drivers replaced, GPU settings wiped); control, customization and trust.

## The ranking

### 1. "What changed?" journal - DROPPED (2026-10-18)
Dropped by Stephen: Activity & History already records what TrayTrigger changes and puts back, and a second
page would duplicate it. A lighter idea (quiet Activity entries when the GPU driver, Windows build or a game's
build differs from the last launch) was offered and not taken up.
TrayTrigger records the gaming-relevant state: Windows build and update, GPU driver, power plan,
HAGS, HDR, refresh rate and the game's own build. When a game starts running worse or crashing, it
shows what changed since the last good session. Includes patch watch: a new game build is logged,
with the launcher's own rollback steps.
- **Why:** "It worked yesterday" is the most common complaint and nothing answers it.
- **Fit:** uses data TrayTrigger already collects, and reports facts, not guesses.

### 2. Shader prep before first launch (optional, measured)
Offer an open-source tool such as SCSKiller to precompile shaders before a game starts, and prove on
that game whether it helped.
- **Evidence:** Hardware Unboxed reported about 25 % in Jedi Survivor; some games showed no benefit.
- **Caveat:** third-party and young. Offer it, never bundle it. Supersedes the older roadmap note
  that shader pre-warming was not possible from outside a game.

### 3. Reality Check: an open tweak lab
Opt-in, anonymous A/B results published as an open dataset ("on this GPU and CPU, this setting
changed 1 % lows by X % across N sessions"). Builds on an A/B Tuner that measures each tweak on the
player's own PC.
- **Why:** ends the snake-oil argument; the first evidence-based tweak database built by gamers.
- **Risk:** any data collection can cost trust. Opt-in, anonymous, open source, and visible before
  anything is sent.

### 4. Network etiquette
While a game runs, limit launcher and Windows Update bandwidth with a Windows QoS rule, removed on
exit.
- **Why:** background downloads cause lag spikes in online games.
- **Caveat:** needs an A/B ping-under-load test before it ships.

### 5. Dock mode for handhelds
When an external display or the charger is connected, switch a scene (display, refresh rate, HDR,
audio device, power plan, frame cap) and put it back after. TDP stays out: it needs a kernel driver.
- **Why:** docked and handheld switching is clumsy; Microsoft only fixes it on the ROG Xbox Ally.
  Legion Go, Claw and others get nothing.

### 6. Trust Center
One page listing everything TrayTrigger reads and writes, every network request it makes (update
checks and artwork lookups only), and the signed-release check, with an Export audit button.
- **Why:** gamers distrust closed utilities, telemetry and kernel anti-cheat, and want local-first,
  open-source tools. Cheap to build and a strong PR story.

### 7. PC facts for this game (PCGamingWiki) - complete, untested in the app (1.7.1-beta.1, off by default)
**Status:** built and covered by unit and live-wiki tests, but not yet tried in the running app on real games.
To check at home: tick the setting, open about ten games (one non-Steam), press Refresh with the network off,
and look at the card at 130% text size.

An optional card in Game Details with what PCGamingWiki's structured tables say about the game, read
from the page's wikitext through the wiki's anonymous API. Help: Where game info comes from >
PCGamingWiki. Code: `Services/PcGamingWikiService.cs` (lookup, cache, manners),
`Services/PcGamingWikiParser.cs` (the reader), `ViewModels/PcGamingWikiCardViewModels.cs` (the card).

**What the wiki's API allows (tested 2026-10-17, from the app's own HTTP client):**
- `api.php?action=query` and `action=parse&prop=wikitext` answer anonymously with a descriptive
  User-Agent. The anonymous full-text search finds a page from a Steam App ID, and the page's own
  infobox lists the ID, so a match is verified, not guessed.
- `action=cargoquery` (the structured tables) needs a login; `Special:CargoExport` is behind a
  Cloudflare challenge. Neither is used.
- `api/appid.php` (the redirect from a Steam App ID) answers curl but returns 403 to .NET's
  `HttpClient` however the request is shaped. It is not used.
- The content is CC BY-NC-SA 3.0, so the card names the page it read and links to it.

**What it pulls, and how consistently (86 surveyed pages with facts, the C# reader):**

| Fact | Pages that have it | Notes |
|---|---|---|
| Ultrawide, 120+ FPS, borderless windowed | 100 % | |
| Controller support, key remapping | 99 % | combined with "full controller" into one line |
| 4K | 98 % | |
| Mute on focus lost | 98 % | |
| Graphics API (DirectX, Vulkan, OpenGL) | 97 % | only values that are plain version numbers |
| Steam Cloud | 94 % | |
| Closed captions | 92 % | |
| Subtitles, HDR | 86 % | |
| Multi-monitor | 84 % | |
| Color blind mode | 81 % | |
| Field of view setting | 78 % | "not applicable" (2D games) is left out |
| Save location | 72 % | the rest have no Windows row; shown as text, never opened |
| Upscaling, ray tracing | 66 %, 64 % | |
| PlayStation controllers | 58 % | |
| Online, LAN, local play | 59 %, 56 %, 50 % | a single-player game has no multiplayer template |
| Crossplay | 23 % | only multiplayer games |

Every rating value in those fields was one of the wiki's six words (true, false, hackable, limited,
always on, unknown or n/a), so nothing is guessed. A fact the wiki marks unknown is left out.

**Lookup results (real wiki, 108 lookups, run last on 2026-10-17):** 104 found. The four without a card
are a made-up game and three names the wiki sends to a differently titled page ("DOOM" is the 1993 game,
"Minecraft" is the Java Edition, "Alan Wake 2" is "Alan Wake II"): with no Steam App ID to vouch for the
page they are left out on purpose, because a game of that name could be another edition. With the ID the
same games are found. Short or very common IDs (10, 400) are found through the name, since a text
search for the number is too noisy.

**Left out on purpose:** the wiki's per-fact notes beyond a tooltip, languages, DRM, anti-cheat (the
app detects it itself), VR support, the middleware lists.

### 8. Local-first sync
Library, profiles and themes sync through a folder the player already syncs (OneDrive, Dropbox,
Syncthing). No account, no cloud.
- **Why:** one setup across desktop and handheld; matches the demand for local-first tools.

### 9. Library tiering
TrayTrigger knows playtime, drives, SSD versus HDD and free space. It says which unplayed games to
move off the SSD and opens the launcher's own move option. Advises only; never moves files itself.
- **Why:** games run 100 to 200 GB and SSDs slow down when nearly full.

### 10. Household mode
Per-person profiles with play-time budgets for family PCs, with one clear warning before a game is
closed.
- **Ranks last** because it conflicts with "nothing interrupts a game". It must be strictly opt-in.
  No launcher does this locally with no account.

## Suggested order

Items 1 and 6 first, together: a journal that finds the cause, and a Trust Center that proves
nothing is hidden. Then 2. Items 3 and 4 wait for the measuring tools (the A/B Tuner and PresentMon
support). Items 5 and 7 to 10 can go in any release after that.

## Competitor gaps (2026-10-17)

Features that competing tools have and TrayTrigger does not, from eight Gemini searches on Playnite,
GOG Galaxy, LaunchBox, Heroic, Steam, the NVIDIA, AMD and ASUS apps, Process Lasso and Razer Cortex,
checked against TrayTrigger's code, Help and CHANGELOG. The competitor claims are Gemini's summaries
of web and Reddit pages, not first-hand reads; verify a claim before relying on it.

| # | Gap | Who has it | TrayTrigger today |
|---|---|---|---|
| A | Per-game display (resolution, refresh rate, monitor) and audio output device, restored on exit | Playnite's Display Helper and Audio Switcher; Display Changer X; Monitor Profile Switcher; DisplayFusion triggers; ASUS Armoury Crate scenario profiles | Scripts only; the scripts catalogue has a refresh-rate script |
| B | Per-session performance stats: FPS, 1 % lows, CPU, GPU, RAM, temperatures, power | Playnite's Game Activity; NVIDIA and AMD overlays; Razer Cortex FPS counter; Armoury Crate monitoring | Activity records what TrayTrigger changed, not how the game ran |
| C | Save backup and restore | Ludusavi (19,000+ games) and its Playnite extension; Steam Cloud; GOG cloud saves; Heroic's per-game cloud toggle | None. The 1.4.7 notes say saves are not in a backup, and the Save Backup example script was removed |
| D | Achievements across stores | Playnite's SuccessStory; GOG Galaxy's combined achievements | None (only metadata) |
| E | The whole owned library, with install, update and repair | Heroic; Playnite; GOG Galaxy; Steam | Installed games only, plus a NOT INSTALLED tag for games it once saw |

Notes:

- **C needs an owner decision.** Leaving saves out was deliberate. If it is wanted, integrate Ludusavi
  and do not build a save engine of our own.
- **D and E** need accounts or keys. Start with Steam, using the user's own key like the RAWG and
  SteamGridDB keys, plus the `steam://install` link. No store logins, for trust.
- **Seen, not chosen:** Discord Rich Presence (small, local, Playnite's most-loved; the best quick
  win), game version rollback (TrayTrigger can only guide), HowLongToBeat, mod-manager links
  (Heroic's REDMod, Steam Workshop), emulator libraries, a friends list, game deals. Controller-first
  full screen and Remote Play stay declined.

## Smaller checks already done or queued

- **Done (1.7.1-beta.1):** Stutter Check reports a single memory stick in a board with room for more
  (LaptopMedia measured Far Cry 5 at 58 fps single-channel against 77 fps dual-channel).
- **Done, untested in the app (1.7.1-beta.1):** the PCGamingWiki facts card (item 7), the update window's buttons wrap at large
  text sizes, and the Game Launch Scripts card hides its defaults until Enable scripts is ticked.
- **Held, evidence too thin:** naming which Windows features (WSL2, Virtual Machine Platform,
  Hyper-V) keep the hypervisor loaded. The measured cost belongs to Memory Integrity, which is
  already reported.
- **Held, no measurements found:** PCIe link-state power management, USB selective suspend, RGB
  software as a stutter cause, memory compression, SysMain, Wi-Fi power saving.
- **Dismissed as snake oil:** HPET and `useplatformclock`, standby-list clearing, RAM cleaners,
  "unlock all cores" in msconfig, disabling the page file, registry cleaners, aggressive debloat
  scripts.
