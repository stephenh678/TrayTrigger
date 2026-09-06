# TrayTrigger

<p align="center">
  <strong>A modern, ultra-fast system tray game launcher and performance companion for Windows.</strong>
</p>

---

## Overview

**TrayTrigger** is a lightweight, high-performance Windows desktop application designed to streamline how you launch, organize, and optimize PC games. Inspired by modern Windows 11 Fluent design and Steam jump lists, TrayTrigger lives quietly in your system notification tray while giving you instant 1-click access to your library, system specs, and gaming tweaks.

---

## Key Features

### 🎮 Modern System Tray Jump List
- **1-Click Launch**: Launch your favorite and recently played games directly from the system tray context menu without opening the full UI.
- **Dedicated Recent Section**: Directly accessible at the top of the tray menu with high-resolution 24x24 icons.
- **Category Organization**: Keep games structured in folders (Action, RPG, Strategy, Steam, etc.) or view as a flat list.
- **Steam & Windows 11 Aesthetics**: Dark slate elevated surfaces, smooth rounded selection highlights, and drop shadows.

### 📚 Comprehensive Game Library
- **Multiple Layout Views**: Grid view (vertical poster cards), Large Icons view, and Detailed List view.
- **Automatic Metadata & Art**: Integrates with Steam APIs and SteamGridDB to fetch official high-res poster art, descriptions, developers, release dates, and Metacritic scores.
- **Automated Icon Extraction**: Extracts crisp 32-bit icons directly from executables, shortcuts (`.lnk`), and game folders with procedural fallback badge generation.
- **Drag & Drop Importing**: Simply drag executables or shortcuts onto the window to automatically add games to your library.
- **Batch Folder Scanner**: Scan entire game drives or directories with intelligent executable scoring to filter out uninstaller and launcher binaries.

### ⚡ System Specs & Gaming Optimization Tweaks
- **Live Hardware Telemetry**: Instant breakdown of CPU, GPU, VRAM, RAM, Displays, Motherboard, BIOS, OS version, and DirectX capability.
- **Performance Diagnostics**: Scan and audit 10 critical Windows performance settings with 1-click toggle optimization:
  - Disable Enhanced Pointer Precision (enforces 1:1 raw mouse input)
  - Hardware-Accelerated GPU Scheduling (HAGS)
  - Windows Game Mode & Xbox Game Bar
  - Ultimate Performance Power Plan activation
  - Windows Visual Effects optimization
  - Network Throttling & Multimedia Network optimizations

### ⚙️ Customizable Settings & Reliable Storage
- **Single-Source of Truth**: Unified settings for startup, window behaviors, sort options, and theme preferences.
- **Robust Storage**: Fast atomic JSON persistence with automatic `.bak` backups in `%AppData%\TrayTrigger`.
- **Global Hotkey Support**: Quickly toggle TrayTrigger with a customizable global hotkey from anywhere in Windows.

---

## Tech Stack

- **Framework**: .NET 10.0 (WPF) with C# 13
- **Tray Icon**: `H.NotifyIcon.Wpf`
- **WMI & Hardware**: `System.Management`
- **Serialization**: `System.Text.Json` Source Generation (`AppJsonContext`)
- **Native Interop**: Modern `[LibraryImport]` P/Invoke (User32, DwmApi)

---

## Getting Started

### Prerequisites
- Windows 10 (version 1809+) or Windows 11 (64-bit recommended)
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download)

### Build & Run
```bash
# Clone repository
git clone https://github.com/your-username/TrayTrigger.git
cd TrayTrigger

# Build solution
dotnet build

# Run application
dotnet run
```

### Self-Contained Single-File Release
```bash
dotnet publish -c Release -r win-x64 --self-contained true
```

---

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
