<p align="center">
  <img src="Assets/app_icon.png" width="96" alt="TrayTrigger icon">
</p>

<h1 align="center">TrayTrigger — Windows Game Launcher & Gaming Optimizer</h1>

<p align="center">
  <strong>Your PC games. One tray. Launch, organize, optimize.</strong>
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
  <a href="#screenshots">Screenshots</a>
  &nbsp;·&nbsp;
  <a href="#faq">FAQ</a>
</p>

<p align="center">
  Windows 10/11 &nbsp;·&nbsp; Free &nbsp;·&nbsp; Open Source (MIT) &nbsp;·&nbsp; No account required
</p>

---

TrayTrigger is a lightweight Windows game launcher that lives in your system tray. It automatically imports your Steam library with full artwork and metadata, organizes every game you own in one place, and can apply — then automatically revert — per-game performance optimizations, all without another full-screen launcher window competing for your attention.

The installer and portable download are both **self-contained** — no separate .NET runtime install required.

<p align="center">
  <img src="Assets/screenshots/library-grid.png" width="800" alt="Games library, poster grid view">
</p>

---

## Why TrayTrigger?

TrayTrigger isn't trying to replace Steam, or compete with a full-screen library manager like Playnite. It stays out of the way in your system tray and combines instant game launching with automatic, reversible per-game optimization — the two things a Steam client or a library manager wasn't really built to do together.

**⚡ Instant Access**
Launch any game from the tray's right-click menu in one click — no separate window to open, find, and close.

**🎯 Automatic Optimization**
Assign a game an Optimized or Aggressive performance profile once. TrayTrigger applies it the moment the game launches and reverts your system to exactly how it was the moment the game closes — no manual undo, no config left behind.

**🪶 Lightweight Companion**
TrayTrigger isn't another always-on-top launcher. It's a tray icon until you need it.

## Features at a Glance

- 🎮 **Tray Game Launcher** — 1-click launch from the tray's jump list, no full UI required
- 📚 **Automatic Steam Library** — Steam games detected and imported automatically, metadata included
- 🖼️ **SteamGridDB Artwork** — high-res poster art fetched automatically for your whole library
- 📖 **RAWG Game Info** — optional developer, publisher, synopsis and ratings for games that aren't on Steam (Game Pass, Epic exclusives, Roblox), with a per-game Steam | RAWG switch
- 🎯 **Per-Game Performance Profiles** — Optimized/Aggressive tweaks applied on launch, reverted on exit
- 🤖 **Per-Game Automation** — optional pre-launch and post-exit scripts, scoped to a single game
- ⚡ **Verified & Reversible Windows Gaming Optimizations** — 21 tweaks grounded in documented Windows behavior, not folklore, each reverting to the exact prior state
- 📊 **Live Hardware Monitoring** — CPU, GPU, VRAM, RAM, displays, motherboard, and BIOS at a glance

---

## Screenshots

<p align="center">
  <img src="Assets/screenshots/library-grid.png" width="800" alt="Games library, poster grid view"><br>
  <em>Poster grid library view with automatic art, categories, and Steam integration</em>
</p>

<p align="center">
  <img src="Assets/screenshots/system-hardware.png" width="800" alt="Live hardware telemetry and system specs"><br>
  <em>Live hardware telemetry: GPU, display, CPU load, RAM, and storage</em>
</p>

<p align="center">
  <img src="Assets/screenshots/about-features.png" width="800" alt="About page, key features"><br>
  <em>In-app overview of key features, shortcuts, and diagnostics</em>
</p>

---

## Game Library

- **Automatic Steam discovery and artwork** — no manually building your library one game at a time. TrayTrigger scans your installed Steam library and fetches official metadata (name, description, developer, release date, Metacritic score) plus high-res poster art from SteamGridDB.
- **GOG, EA, Epic, Ubisoft Connect, and Xbox / PC Game Pass too** — Scan for Games reads each launcher's own install records, so every installed game shows up with its real title and launches through its own client (or, for Game Pass titles, through Windows itself). Each integration has its own on/off switch in Settings.
- **Multiple Layout Views** — Grid view (vertical poster cards), Large Icons view, and Detailed List view.
- **Automated Icon Extraction** — crisp 32-bit icons pulled from executables, shortcuts (`.lnk`), and game folders, with procedural fallback badge generation when nothing better is available.
- **Drag & Drop Importing** — drag an executable or shortcut onto the window to add it.
- **Batch Folder Scanner** — scan an entire game drive with intelligent executable scoring that filters out uninstallers and launcher binaries.
- **Favorites & Categories** — pin favorites to the top of the tray menu and library, organize the rest into custom categories (Action, RPG, Strategy, Steam, etc.) or view as a flat list.
- **Batch editing** — Ctrl+click, Shift+click, or Ctrl+A to select several games in any view, then right-click for a batch menu that favorites, hides, re-categorizes, sets the Performance Profile, refreshes poster art and metadata, or removes them in one step, with the same undo as a single removal.
- **Launcher stays out of the way** — per game, close Steam, GOG Galaxy, EA App, Epic, Ubisoft Connect, or the Xbox app automatically when the game exits. For Steam, that option also starts the client minimized to the tray when TrayTrigger has to open it, so only the game shows.

## Performance Profiles

Assign each game its own tweak tier from Edit Game:

| Tier | What it does |
|---|---|
| **Off** | No per-game tweaks — TrayTrigger just launches the game |
| **Optimized** | Applies your custom high-performance power plan and GPU preference for that game, plus opt-in Enable HDR and Do Not Disturb |
| **Aggressive** | Everything in Optimized, plus System Responsiveness, MMCSS "Games" scheduling priority, Above Normal process priority, a 0.5 ms timer resolution request, and an off-by-default Microsoft Defender exclusion |

Independently of the tier, any game can be pinned to the performance cores of a hybrid CPU (Edit Game → CPU Cores) for older engines and anti-cheat titles that stutter on E-cores.

- **Session-scoped, zero manual cleanup** — tweaks apply the moment a game launches (Steam or direct `.exe`) and revert to your exact prior settings the moment it closes.
- **Crash-safe** — if TrayTrigger or your PC crashes mid-session, the next launch detects and restores your pre-game state automatically.
- **Custom pre-launch & post-exit scripts** (advanced, off by default) — attach your own `.bat`, `.cmd`, `.ps1`, or `.exe` to any game. The pre-launch script runs just before the game starts (optionally holding launch until it finishes, with a configurable timeout, and optionally cancelling the launch if it fails); the post-exit script runs the moment the game closes, for direct, Steam, GOG/EA/Epic/Ubisoft, and Xbox launches alike. Scripts receive the phase, game name, executable, game ID, and playtime as arguments plus `TRAYTRIGGER_*` environment variables, can run hidden (with their output captured to the log) or elevated, and each has a Test button that runs it on the spot and shows its exit code and output.

## Verified & Reversible Windows Gaming Optimizations

Gaming optimization without mystery registry hacks. 21 documented Windows gaming settings, each toggled individually, each showing Windows' real current state before you touch anything (HAGS is read from the display driver itself), and each reverting to the exact state TrayTrigger found — not a hard-coded "default". TrayTrigger's in-app "Learn more" for every tweak explains the exact tradeoff — most aren't a guaranteed win for every game, and are presented that way rather than oversold. Tweaks that can't apply on your machine (a Windows 11-only graphics setting on Windows 10, HAGS on an unsupported GPU) say so instead of pretending.

| Tweak | Category | What it changes |
|---|---|---|
| Mouse Acceleration | Input & Display | Disables "Enhance pointer precision" so cursor movement is 1:1 with physical mouse movement |
| Sticky / Filter / Toggle Keys shortcuts | Input & Display | Stops five Shift taps or a long Shift hold from popping an accessibility dialog over your game |
| Optimizations for Windowed Games | Input & Display | Enables flip-model presentation for borderless/windowed DX10/11 games — same low input latency as exclusive fullscreen (Windows 11) |
| Variable Refresh Rate for Windowed Games | Input & Display | Lets G-SYNC/FreeSync engage for borderless DX11 games, not just exclusive fullscreen (Windows 11) |
| Auto HDR *(opt-in)* | Input & Display | Windows 11's Auto HDR for SDR-only DX11/12 games on an HDR display |
| Hardware-Accelerated GPU Scheduling (HAGS) | Input & Display | Lets the GPU manage its own command queue instead of the CPU scheduling every batch |
| Disable Fullscreen Optimizations *(opt-in)* | Input & Display | Restores true exclusive fullscreen instead of Windows' managed borderless shim — helps some old DX9/11 engines, hurts most modern ones |
| Disable Multiplane Overlay *(opt-in)* | Input & Display | NVIDIA's documented workaround for stutter/flicker/black screens in borderless games |
| Windows Game Mode | CPU & Scheduling | Holds Windows Update/driver installs during play and prioritizes game threads |
| System Timer Resolution | CPU & Scheduling | Restores system-wide high-precision timer behavior for engines that don't request it themselves |
| Foreground Priority Boost *(opt-in)* | CPU & Scheduling | The documented `Win32PrioritySeparation` "Programs" scheduling preference — foreground game keeps its core over background apps |
| Windows Visual Effects *(opt-in)* | CPU & Scheduling | Turns off minimize animations and drop shadows to reduce Desktop Window Manager compositing load |
| "Ultimate Plan – TrayTrigger" Power Plan *(opt-in)* | CPU & Scheduling | Pins the CPU at 100% min/max state and disables PCIe/USB power-saving, permanently rather than only during a session |
| Disable MMCSS Network Throttling | Network & Background | Lifts the packet-rate cap Windows applies to non-multimedia traffic while audio/video is active |
| Disable Nagle's Algorithm *(opt-in)* | Network & Background | Sends small TCP packets immediately instead of batching — situational, most games use UDP already |
| Disable Delivery Optimization | Network & Background | Stops Windows silently uploading updates to other PCs on your network or the internet |
| Exclude Drivers from Windows Update *(opt-in)* | Network & Background | Stops Windows Update replacing your pinned GPU/chipset/audio drivers |
| Disable Game Bar Captures | Network & Background | Stops the hardware-encoder background recording buffer Xbox Game Bar keeps running |
| Disable Xbox Game Bar Overlay | Network & Background | Stops the Game Bar overlay and its background processes from loading with your game |
| Disable Diagnostic Telemetry Sweeps | Network & Background | Lowers the Windows diagnostic data policy so background scan tasks (e.g. CompatTelRunner) run less often |
| Core Isolation / Memory Integrity *(status only)* | Security & Advanced | Shows whether HVCI is running (documented CPU cost in some games) — links to Windows Security; never counted as an "optimization" |

An optional **System Restore point** is created automatically before any bulk Apply Preset or Reset Defaults, if enabled in Settings (on by default) — a rollback path if anything ever misbehaves.

## Hardware Monitoring

Instant breakdown of CPU, GPU, VRAM, RAM, Displays, Motherboard, BIOS, OS version, and DirectX capability — no separate tool needed to sanity-check your specs before deciding which profile to use.

---

## Installation

### Installer (recommended)
Download `TrayTrigger-Setup.exe` from the [latest release](https://github.com/stephenh678/TrayTrigger/releases/latest) and run it. No .NET runtime install required — TrayTrigger ships self-contained.

### Portable
Download the portable `.zip` from the [latest release](https://github.com/stephenh678/TrayTrigger/releases/latest), extract it anywhere, and run `TrayTrigger.exe`. No installation, no registry footprint beyond what you explicitly enable in Settings.

### Updates
TrayTrigger checks GitHub Releases for updates and installs them in one click. Enable *Receive pre-release (beta) updates* in Settings to get beta and release-candidate builds first; leave it off for stable releases only.

Every release ships a `SHA256SUMS.txt`. The in-app updater verifies the installer against it before launching, and refuses to install anything that doesn't match.

### Verifying a download
```powershell
Get-FileHash .\TrayTrigger-v1.3.9-Setup.exe -Algorithm SHA256
```
Compare the hash with the matching line in the release's `SHA256SUMS.txt`. Signed releases (see [Code signing](#code-signing)) also show a valid publisher in the file's Properties → Digital Signatures tab.

## Requirements

- Windows 10 (version 1809+) or Windows 11 (64-bit recommended)
- No separate .NET install needed (self-contained build)

---

## FAQ

**Why TrayTrigger instead of just using Steam?**
Steam covers Steam games. TrayTrigger sits in your tray for one-click access to your whole library — Steam and non-Steam alike — and adds per-game performance tweaks Steam doesn't offer.

**Why TrayTrigger instead of Playnite?**
Playnite is a full-featured library manager with its own window and a plugin ecosystem. TrayTrigger is intentionally narrower: a tray-first launcher with built-in, reversible performance tweaking, for people who want quick access and optimization rather than another big library UI.

**Is it safe?**
TrayTrigger doesn't collect telemetry or personal data. Its only outbound network calls are to Steam's public APIs, SteamGridDB (for artwork), RAWG (for non-Steam game info, only if you enable it with your own key), and GitHub Releases (for update checks). See [SECURITY.md](SECURITY.md) for the full breakdown.

**Does it modify Windows?**
Only tweaks you explicitly enable. Some require an admin (UAC) prompt because they write to `HKEY_LOCAL_MACHINE`; tweaks scoped to your user account don't.

**Can changes be reverted?**
Yes — every tweak records the prior state and restores it automatically, including after a crash mid-session. An optional System Restore point adds another layer of safety before bulk changes.

**Does it need administrator access?**
Only for specific tweaks that touch machine-wide settings or the Microsoft Defender exclusion list. TrayTrigger does not run elevated by default.

---

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

Bug reports, feature requests, and pull requests are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md) to get started.

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
