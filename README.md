<p align="center">
  <img src="Assets/app_icon.png" width="96" alt="TrayTrigger icon">
</p>

<h1 align="center">TrayTrigger</h1>

<p align="center">
  <strong>Launch. Automate. Play.</strong>
</p>

<p align="center">
  Per-game Windows tuning and launch automation for PC gamers.<br>
  Performance profiles, your own pre-launch and post-exit scripts, launch arguments, and launchers that close themselves.<br>
  <strong>Everything reverts when the game exits.</strong> Steam, GOG, Epic, EA, Ubisoft, Xbox. Lives in your tray.
</p>

<p align="center">
  <img src="https://img.shields.io/github/v/release/stephenh678/TrayTrigger?style=flat-square&label=release" alt="Latest release">
  <img src="https://img.shields.io/github/downloads/stephenh678/TrayTrigger/total?style=flat-square&label=downloads" alt="Downloads">
  <img src="https://img.shields.io/github/license/stephenh678/TrayTrigger?style=flat-square" alt="License">
  <img src="https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6?style=flat-square" alt="Platform">
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square" alt=".NET 10">
</p>

<p align="center">
  <a href="https://github.com/stephenh678/TrayTrigger/releases/latest"><strong>⬇ Download for Windows</strong></a>
  &nbsp;·&nbsp;
  <a href="https://github.com/stephenh678/TrayTrigger/releases/latest">Portable ZIP</a>
  &nbsp;·&nbsp;
  <a href="https://github.com/stephenh678/TrayTrigger/wiki">Wiki</a>
  &nbsp;·&nbsp;
  <a href="#faq">FAQ</a>
  &nbsp;·&nbsp;
  <a href="https://github.com/stephenh678/TrayTrigger/discussions">Discussions</a>
</p>

<p align="center">
  Windows 10/11 &nbsp;·&nbsp; Free &nbsp;·&nbsp; Open Source (MIT) &nbsp;·&nbsp; No account &nbsp;·&nbsp; No telemetry
</p>

---

<!-- HERO GIF: Assets/screenshots/tweaks-apply-revert.gif goes here once recorded. -->
<p align="center">
  <img src="Assets/screenshots/library-grid.png" width="800" alt="TrayTrigger games library, poster grid view">
</p>

---

## Every launcher opens games. TrayTrigger runs the session.

Steam, Playnite, and the rest stop at "the game is running." TrayTrigger is what happens around that: what Windows looks like while you play, what your other apps do, and what gets put back when you're done. All of it per game, all of it automatic, all of it reversible.

| When | What TrayTrigger does |
|---|---|
| **Before launch** | Snapshots your current settings. Applies the game's **Performance Profile**: power plan, GPU preference, HDR, Do Not Disturb, process priority, timer resolution, CPU cores. Runs your **pre-launch script**: pause Wallpaper Engine, start the OBS replay buffer, close Discord, back up saves. |
| **On launch** | Starts the game with your **launch arguments**, as Administrator if you asked, through its own launcher (Steam, GOG, Epic, EA, Ubisoft, Xbox) or directly. Steam can start minimized so only the game appears. |
| **While playing** | Stays a tray icon. The tooltip and the tray menu's *Now Playing* section show what's running. |
| **On exit** | Restores every setting to **exactly what it found**. Runs your **post-exit script**. Closes the launcher if you told it to. Crash-safe: if TrayTrigger or Windows dies mid-game, the snapshot is restored on next start. |

### Why not just Steam? Why not Playnite?

| | Steam | Playnite | **TrayTrigger** |
|---|:---:|:---:|:---:|
| Launches games from every store | | ✅ | ✅ |
| Per-game Windows performance profile | | | ✅ |
| Reverts every change when the game exits | | | ✅ |
| Your own pre-launch and post-exit scripts | | ✅ | ✅ |
| Bundled example scripts, Test Run, per-game arguments, cancel-on-failure | | | ✅ |
| Closes the launcher when the game exits | | | ✅ |
| 21 documented, reversible Windows gaming tweaks | | | ✅ |
| Lives in the tray, no window to manage | | | ✅ |
| Full-screen library UI with a plugin ecosystem | | ✅ | |

TrayTrigger isn't trying to replace Playnite's library or Steam's store. It's the layer under them.

---

## Performance Profiles

Assign each game a tier in Edit Game. It applies the moment the game launches and reverts the moment it closes.

| Tier | What it does |
|---|---|
| **Off** | No per-game tweaks. TrayTrigger just launches the game. |
| **Optimized** | Your custom high-performance power plan and high-performance GPU preference for that game. Opt-in extras: Enable HDR, Do Not Disturb, Unmute Speakers. |
| **Aggressive** | Everything in Optimized, plus System Responsiveness, MMCSS "Games" scheduling priority, Above Normal process priority, a 0.5 ms timer resolution request, and an off-by-default Microsoft Defender exclusion. |

- **CPU Cores**: independently of the tier, pin any game to the performance cores of a hybrid CPU, for older engines and anti-cheat titles that stutter on E-cores.
- **Session-scoped**: tweaks apply on launch (Steam or direct `.exe`) and revert to your exact prior settings on exit. No manual undo, no config left behind.
- **Crash-safe**: the snapshot lives on disk. If TrayTrigger or your PC crashes mid-session, the next start restores your pre-game state. A normal shutdown restores the power plan, HDR, and GPU preference immediately.
- **Two games at once**: machine-wide tweaks apply with the first game and restore with the last. Per-game tweaks apply and restore independently.
- **End Session / Force Close** in the game's right-click menu if a launcher ever stalls. A game that never appears is rolled back automatically after three minutes.

---

## Pre-Launch and Post-Exit Scripts

Attach a `.bat`, `.cmd`, `.ps1`, or `.exe` to any game. It runs just before the game starts and again after it exits, for direct, Steam, GOG, EA, Epic, Ubisoft, and Xbox launches alike. Use it for anything TrayTrigger doesn't do itself.

**Five real ones ship with the app**, written to be read, copied, and changed:

| Script | What it does |
|---|---|
| `Example-WallpaperEnginePause.ps1` | Pauses and mutes Wallpaper Engine while you play, resumes it after. |
| `Example-QuietMode.ps1` | Closes the background apps you name (OneDrive, Teams, Dropbox) and reopens the ones it closed. |
| `Example-CompanionApps.ps1` | Starts the tools a game needs (SimHub, TrackIR) and closes only the ones it started. |
| `Example-OBSReplayBuffer.ps1` | Runs OBS in the tray with the replay buffer on, so a hotkey saves the last minutes of play. |
| `Example-SaveBackup.ps1` | Zips a save folder before and after you play, keeps the newest ten. |

A script doesn't have to be long. This is a complete pre-launch script:

```bat
@echo off
taskkill /im Discord.exe /f
```

**What you get:**
- **Test Run buttons** next to every script field: runs it now, shows the exit code and output, without launching the game.
- **Script Arguments** per game, so one generic script serves your whole library (a save folder, a profile name, a list of apps).
- **Game info passed in**: phase, game name, exe, game ID, and playtime as arguments, plus `TRAYTRIGGER_*` environment variables.
- **Wait / timeout / cancel-on-failure**: hold the launch until the script finishes, or treat it as a precondition and abort the launch (and roll back the profile) if it fails.
- **Hidden or elevated**: suppress the console window with output captured to the log, or run through UAC.
- **Default scripts** in Settings run for every game that has no script of its own, with a per-game opt-out.
- **New script...** creates a blank template with every argument already read for you and opens it in your editor.

Scripts are off by default. Nothing runs until you enable them and choose one. See the [scripts wiki page](https://github.com/stephenh678/TrayTrigger/wiki/Pre-Launch-and-Post-Exit-Scripts) for the full contract, and [`docs/roadmap.md`](docs/roadmap.md) for the planned community script catalog.

---

## Launcher Control

- **Launch arguments** per game, passed exactly as typed.
- **Run as Administrator** per game.
- **Close the launcher after the game exits**: Steam, GOG Galaxy, EA App, Epic, Ubisoft Connect, or the Xbox app shut down once the session ends, so they don't stay resident with their overlays and background processes. For Steam, TrayTrigger also starts the client minimized to the tray when it has to open it, so only the game shows.
- **Global hotkey** to open the library, and per-game hotkeys to launch.
- **One-click launch from the tray menu**: Recent, Favorites, and Categories, no window to open.

---

## Verified and Reversible Windows Gaming Tweaks

System-wide settings, separate from the per-game profiles. 21 documented Windows gaming settings, each toggled individually, each showing Windows' **real current state** before you touch anything (HAGS is read from the display driver itself), and each reverting to the exact state TrayTrigger found, not a hard-coded "default". Every tweak has an in-app **Learn more** (and a [wiki page](https://github.com/stephenh678/TrayTrigger/wiki)) that explains the trade-off honestly. Most aren't a guaranteed win for every game, and they're presented that way. Tweaks that can't apply on your machine say so instead of pretending.

<!-- GIF: Assets/screenshots/tweaks-apply-revert.gif (Apply Performance Preset → badges flip → Reset Defaults) -->

| Tweak | Category | What it changes |
|---|---|---|
| Mouse Acceleration | Input & Display | Disables "Enhance pointer precision" so cursor movement is 1:1 with physical mouse movement |
| Sticky / Filter / Toggle Keys shortcuts | Input & Display | Stops five Shift taps or a long Shift hold from popping an accessibility dialog over your game |
| Optimizations for Windowed Games | Input & Display | Enables flip-model presentation for borderless/windowed DX10/11 games, same low input latency as exclusive fullscreen (Windows 11) |
| Variable Refresh Rate for Windowed Games | Input & Display | Lets G-SYNC/FreeSync engage for borderless DX11 games, not just exclusive fullscreen (Windows 11) |
| Auto HDR *(opt-in)* | Input & Display | Windows 11's Auto HDR for SDR-only DX11/12 games on an HDR display |
| Hardware-Accelerated GPU Scheduling (HAGS) | Input & Display | Lets the GPU manage its own command queue instead of the CPU scheduling every batch |
| Disable Fullscreen Optimizations *(opt-in)* | Input & Display | Restores true exclusive fullscreen instead of Windows' managed borderless shim. Helps some old DX9/11 engines, hurts most modern ones |
| Disable Multiplane Overlay *(opt-in)* | Input & Display | NVIDIA's documented workaround for stutter/flicker/black screens in borderless games |
| Windows Game Mode | CPU & Scheduling | Holds Windows Update/driver installs during play and prioritizes game threads |
| System Timer Resolution | CPU & Scheduling | Restores system-wide high-precision timer behavior for engines that don't request it themselves |
| Foreground Priority Boost *(opt-in)* | CPU & Scheduling | The documented `Win32PrioritySeparation` "Programs" scheduling preference: the foreground game keeps its core over background apps |
| Windows Visual Effects *(opt-in)* | CPU & Scheduling | Turns off minimize animations and drop shadows to reduce Desktop Window Manager compositing load |
| "Ultimate Plan – TrayTrigger" Power Plan *(opt-in)* | CPU & Scheduling | Pins the CPU at 100% min/max state and disables PCIe/USB power-saving, permanently rather than only during a session |
| Disable MMCSS Network Throttling | Network & Background | Lifts the packet-rate cap Windows applies to non-multimedia traffic while audio/video is active |
| Disable Nagle's Algorithm *(opt-in)* | Network & Background | Sends small TCP packets immediately instead of batching. Situational; most games use UDP already |
| Disable Delivery Optimization | Network & Background | Stops Windows silently uploading updates to other PCs on your network or the internet |
| Exclude Drivers from Windows Update *(opt-in)* | Network & Background | Stops Windows Update replacing your pinned GPU/chipset/audio drivers |
| Disable Game Bar Captures | Network & Background | Stops the hardware-encoder background recording buffer Xbox Game Bar keeps running |
| Disable Xbox Game Bar Overlay | Network & Background | Stops the Game Bar overlay and its background processes from loading with your game |
| Disable Diagnostic Telemetry Sweeps | Network & Background | Lowers the Windows diagnostic data policy so background scan tasks (e.g. CompatTelRunner) run less often |
| Core Isolation / Memory Integrity *(status only)* | Security & Advanced | Shows whether HVCI is running (documented CPU cost in some games) and links to Windows Security. Never counted as an "optimization" |

**Apply Performance Preset** turns on every recommended tweak in one pass with at most one UAC prompt. **Reset Defaults** reverts only what TrayTrigger changed. An optional **System Restore point** is created before either, if enabled in Settings (on by default).

---

## Your Library

The launcher part, so the session part has something to run.

- **Steam, GOG, EA, Epic, Ubisoft Connect, and Xbox / PC Game Pass**: Scan for Games reads each launcher's own install records, so every installed game shows up with its real title and launches through its own client (or, for Game Pass titles, through Windows itself). Each integration has its own on/off switch.
- **Artwork and metadata**: Steam's official metadata (description, developer, release date, Metacritic score) plus high-res poster art from SteamGridDB. Optional RAWG info for games that aren't on Steam (Game Pass, Epic exclusives), with a per-game Steam | RAWG switch. Both need your own free API key and are off until you add one.
- **Three views**: poster grid, large icons, detailed list. Poster cards zoom on hover.
- **Filters**: by launcher, performance profile, and state (never played, favorite, missing executable, has a hotkey, has scripts, runs elevated).
- **Favorites and categories**: favorites pin to the top of the tray menu; the rest goes into custom categories or a flat list.
- **Batch editing**: Ctrl+click, Shift+click, or Ctrl+A, then right-click to favorite, hide, re-categorize, set the Performance Profile, CPU Cores, or launch options, refresh art, or remove, with undo.
- **Drag and drop** an executable or shortcut onto the window to add it. **Batch folder scanner** with executable scoring that filters out uninstallers and launcher stubs. **Icon extraction** from executables, shortcuts, and game folders.

<p align="center">
  <img src="Assets/screenshots/library-grid.png" width="800" alt="Games library, poster grid view"><br>
  <em>Poster grid with automatic art, categories, filters, and launcher badges</em>
</p>

<!-- GIF: Assets/screenshots/tray-launch.gif (tray icon → right-click → launch) -->

## Hardware Monitoring

Instant breakdown of CPU, GPU, VRAM, RAM, displays, motherboard, BIOS, OS version, and DirectX capability, so you can sanity-check specs before deciding which profile a game gets.

<p align="center">
  <img src="Assets/screenshots/system-hardware.png" width="800" alt="Live hardware telemetry and system specs">
</p>

---

## Installation

### Installer (recommended)
Download `TrayTrigger-v*-Setup.exe` from the [latest release](https://github.com/stephenh678/TrayTrigger/releases/latest) and run it. Installs per-user, no admin needed. No .NET runtime install required; TrayTrigger ships self-contained.

### Portable
Download the portable `.zip` from the [latest release](https://github.com/stephenh678/TrayTrigger/releases/latest), extract it anywhere, and run `TrayTrigger.exe`. No installation, no registry footprint beyond what you explicitly enable in Settings.

### Updates
TrayTrigger checks GitHub Releases for updates and installs them in one click. Enable *Receive pre-release (beta) updates* in Settings to get beta builds first; leave it off for stable releases only.

Every release ships a `SHA256SUMS.txt`. The in-app updater verifies the installer against it before launching, and refuses to install anything that doesn't match.

### Verifying a download
```powershell
Get-FileHash .\TrayTrigger-v1.4.1-Setup.exe -Algorithm SHA256
```
Compare the hash with the matching line in the release's `SHA256SUMS.txt`. Signed releases (see [Code signing](#code-signing)) also show a valid publisher in the file's Properties → Digital Signatures tab.

### Requirements
- Windows 10 (version 1809+) or Windows 11, 64-bit
- No separate .NET install needed

---

## FAQ

**Why do I need this if I already have Steam?**
Steam launches Steam games. It doesn't change your power plan for one game and put it back after, pause your wallpaper, start your replay buffer, or close itself when the game exits. TrayTrigger does that for every game from every store, and it launches Steam games through Steam.

**Why not Playnite?**
Playnite is a library manager: a big window, themes, and a plugin ecosystem. TrayTrigger is a tray icon that manages the game session. If you want a full-screen library, use Playnite for that and TrayTrigger for the launch. They don't conflict.

**Is it safe? What does it touch?**
Only what you explicitly turn on. Every tweak records the state it found and restores exactly that, including after a crash. Bulk changes can create a System Restore point first. Scripts are off until you enable them and choose one, and they run as you. See [SECURITY.md](SECURITY.md) for the full breakdown.

**Does it need administrator access?**
Not to run. A few tweaks write machine-wide settings (`HKEY_LOCAL_MACHINE`, the Defender exclusion list) and show a UAC prompt when you enable them. TrayTrigger never runs elevated by default.

**Does it phone home?**
No telemetry. Outbound calls are Steam's public API (metadata and artwork for games you add), SteamGridDB and RAWG (only if you enter your own key), and GitHub Releases (update checks, which you can turn off).

**Can changes be reverted?**
Yes, all of them, automatically. Per-game profiles revert on exit. System tweaks revert with Reset Defaults or one at a time. A crash mid-session is restored on next start.

---

## Wiki

Every tweak, profile option, script feature, and library setting has a page at the [TrayTrigger wiki](https://github.com/stephenh678/TrayTrigger/wiki). It's the same text the app shows under **Learn more**, generated from the same source files, so it's always current.

## Tech Stack

- **Framework**: .NET 10.0 (WPF) with C# 13
- **Tray Icon**: `H.NotifyIcon.Wpf`
- **WMI & Hardware**: `System.Management`
- **Serialization**: `System.Text.Json` Source Generation (`AppJsonContext`)
- **Native Interop**: Modern `[LibraryImport]` P/Invoke (User32, DwmApi)

## Building From Source

### Prerequisites
- Windows 10 (version 1809+) or Windows 11 (64-bit recommended)
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download)

### Build & Run
```bash
git clone https://github.com/stephenh678/TrayTrigger.git
cd TrayTrigger
dotnet build
dotnet run
```

### Self-Contained Single-File Release
```bash
dotnet publish -c Release -r win-x64 --self-contained true
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for running tests and PR guidelines.

## Contributing

Bug reports, feature requests, and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) to get started. Written a launch script worth sharing? Post it in [Show and tell](https://github.com/stephenh678/TrayTrigger/discussions/categories/show-and-tell).

## Security

If you find a security issue, please see [SECURITY.md](SECURITY.md) for how to report it privately.

## Code signing

> **Status:** code signing is being set up and current releases are **not yet signed**. Until then, verify downloads against `SHA256SUMS.txt` as described under [Updates](#updates). This section describes the process that will apply once signing is live.

Free code signing is provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).

**Code signing policy.** Release binaries are built exclusively by the public GitHub Actions workflow in [`.github/workflows/release.yml`](.github/workflows/release.yml) on GitHub-hosted runners and signed through SignPath's GitHub integration; nothing is built or signed on a developer machine. Signed artifacts are `TrayTrigger.exe` (also inside the portable `.zip`) and `TrayTrigger-v*-Setup.exe`. The artifact configurations are kept in [`.signpath/artifact-configurations/`](.signpath/artifact-configurations/).

**Team roles.**
- Author (commits without external review): [@stephenh678](https://github.com/stephenh678)
- Reviewer (reviews and merges third-party pull requests): [@stephenh678](https://github.com/stephenh678)
- Approver (approves each signing request): [@stephenh678](https://github.com/stephenh678)

**Privacy policy.** TrayTrigger collects no telemetry and transfers no personal data. Its only network calls are to Steam's public APIs (game metadata and artwork for games you add), SteamGridDB (artwork, only if you enter your own API key), RAWG (game info for non-Steam titles, only if you enter your own API key), and GitHub Releases (update checks, which can be turned off in Settings). See [SECURITY.md](SECURITY.md) for the full statement.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
