# Apps Section Design Review

**Review Date:** 2026-09-14  
**Target Document:** [`docs/apps-section-plan.md`](apps-section-plan.md)  
**Target Version:** TrayTrigger 1.4.x  

---

## Executive Summary

The proposed plan is architecturally sound, adheres to TrayTrigger's core philosophy (*"keep it simple, off by default, fast shortcut launching"*), and correctly identifies the primary pitfall: **do not overload the game library or `GameEntry`**.

This document captures detailed feedback, recommendations for native feel, suggested cuts for v1 scope, risk mitigations, and answers to the open questions.

---

## 1. Architectural Decision: Separate Section vs. Game Library

**Verdict: Approved (Unquestionably the right choice).**

Attempting to reuse `GameEntry` would introduce severe technical debt:

* **Session & Performance Profiles:** Game launches route through `ProcessLauncherService.LaunchGame`, which orchestrates process tracking, performance profiles (MMCSS priority, CPU affinity, GPU preferences, power plan switches, HDR, Defender exclusions), and pre-launch/post-exit scripts. For companion tools (e.g., Discord, RTSS, MSI Afterburner, Vortex), this behavior is unwanted and disruptive.
* **Enrichment & Platform Detection:** Dropping a file onto the library invokes `PlatformLookupService` and online Steam/RAWG/SteamGridDB scrapers looking for box art, App IDs, and genres. Utility tools have none of these.
* **Code Smells & Maintenance:** Using `GameEntry` with an `IsApp` flag would require guard clauses across 15+ services (statistics, filtering, playtime tracking, tray grouping, batch editing, and diagnostics).
* **Isolation:** Storing utilities in an isolated `apps.json` via `AppEntry` ensures that turning the feature off has zero impact or side effects on the user's primary game library.

---

## 2. Ensuring a Native, First-Class Feel

To prevent the feature from feeling "bolted on" when enabled:

1. **Direct Navigation from System Tray:**
   * In `App.xaml.cs`, the tray menu provides navigation shortcuts: *Games Library*, *Scan for Games...*, and *Settings*.
   * When the Apps/Tools section is enabled, add a direct navigation item (e.g., *Tools Library* or *Manage Tools...* using glyph `\uE74C`) that opens the window directly to `NavSection.Apps`. Without this, tray-only users must navigate to Games Library first and then click over in the sidebar.
2. **Standard Keyboard Navigation:**
   * Support `F2` (Rename), `Delete` (Remove), and `Enter` (Launch selected item).
   * Bind `Ctrl+F` to focus the search box.
3. **Drop Overlay Consistency:**
   * Reuse the existing `DropOverlay` visual control from the Library view so dragging executables into the window provides the exact same visual drop target and animations.
4. **Working Directory Fallback:**
   * When adding an app from a bare `.exe` or a `.lnk` with no working folder specified, default `WorkingDirectory` to `Path.GetDirectoryName(targetPath)`. Many portable utilities and mod managers fail to launch or cannot find adjacent configuration/DLL files if the working directory defaults to TrayTrigger's directory.
5. **Hotkey Recorder Reuse:**
   * The `Edit App` dialog should reuse the existing `HotkeyRecorderControl` (leveraging `HotkeyManager.Instance.CheckAvailability`), providing identical styling and collision warning behavior as the game edit dialog.
6. **Settings Page Placement:**
   * `EnableAppsSection` belongs under **Settings > General**.
   * In addition, `ShowAppsInTray` should be exposed under **Settings > Tray Menu** (matching `ShowRecentInTray` and `ShowFavoritesInTray`) so users configuring their tray find it in its natural location.

---

## 3. Scope Simplification for v1

1. **Trim View Modes (From 4 to 2):**
   * The plan proposes four view modes: *Large Icons (96px)*, *Medium Icons (48px)*, *Small Icons (24px)*, and *List (24px)*.
   * Most users will hold 5–15 companion tools. Building and testing four separate `ItemTemplate` and `ItemsPanel` configurations is over-engineered for v1.
   * Many legacy utility executables (RTSS, Afterburner, JoyToKey) only embed 32px or 48px icon assets, causing 96px Large Icons to render blurry or pixelated.
   * **Recommendation:** Ship v1 with **Medium Icons (48px)** and **List (24px)**. This satisfies both grid and dense list preferences while halving XAML complexity.
2. **Defer URL Shortcuts in v1:**
   * In `ImportCoordinator`, URL validation relies on `UrlProtocolHelper.IsAllowedLaunchUrl`, which only permits game launchers (`steam`, `origin2`, `uplay`, etc.) and web links.
   * Companion tools are virtually always local desktop `.exe` or `.lnk` files. Supporting `.url` files introduces protocol-validation edge cases and complex icon extraction (e.g. web favicons).
   * **Recommendation:** Limit v1 strictly to `.exe` and `.lnk` files pointing to local executables.

---

## 4. Risk Analysis & Implementation Guidance

* **UAC Elevation Cancellation (`RunAsAdmin`):**
  * When launching with `UseShellExecute = true` and `Verb = "runas"`, dismissing or clicking "No" on the Windows UAC dialog raises a `Win32Exception` with error code `1223` (`ERROR_CANCELLED`).
  * `AppLauncherService` must catch this specific error code and ignore it silently, rather than reporting an unhandled error or popup failure.
* **Hotkey Registration & Event Separation:**
  * In `HotkeyManager.cs`, game hotkeys currently use IDs starting from `GAME_HOTKEY_BASE_ID = 1000`.
  * Establish a distinct ID offset for apps (e.g., `APP_HOTKEY_BASE_ID = 5000`) or generalize the registration dictionary.
  * Add a dedicated `AppHotkeyTriggered?.Invoke(appId)` event in `HotkeyManager` so hotkey presses route cleanly to `AppLauncherService` without passing through game session handlers.
* **Drop Target Security:**
  * Instead of maintaining a file blacklist (`.bat`, `.ps1`, `.cmd`, `.vbs`, `.js`), use an allow-list: accept dropped files only if they are `.exe` or `.lnk` resolving to an existing `.exe`. Refuse shortcuts pointing to `cmd.exe` or `powershell.exe`.
* **Data Safety:**
  * Register `List<AppEntry>` in `AppJsonContext` for source-generated AOT/trim-safe serialization.
  * Implement `StorageService.LoadApps()` and `SaveApps()` with atomic file writes (`.tmp` replacement and `.bak` fallbacks), matching the durability guarantees of `games.json`.

---

## 5. Feedback on Open Questions

### Question 1: Name — "Apps", "Tools", or "Utilities"?
* **Recommendation: "Tools"**
* **Rationale:** 
  * In PC gaming, players universally refer to these companion programs as **"Tools"** (Vortex, RTSS, Afterburner, DLSS Swapper). Steam uses the header "Tools" in its library for identical utilities.
  * "Apps" carries Windows Store / mobile connotations.
  * "Tools" is concise, readable on sidebar buttons, and immediately conveys its purpose.

### Question 2: Tray Presentation — Submenu vs. Top-Level Section?
* **Recommendation: Submenu (as proposed in the plan)**
* **Rationale:**
  * Tray menus grow tall quickly. If a user has 15+ games, Recent, Favorites, and navigation links, placing 6–10 tools at the top level causes menu cutoff on smaller screens or high-DPI scaling.
  * A `Tools (N) >` submenu keeps the root tray menu clean and focused on rapid game launching.
  * The per-app `ShowInTray` setting allows users to exclude background tools they rarely trigger manually.

---

## Summary Checklist for Implementation

| Item | Status in Plan | Recommendation |
|---|---|---|
| **Architecture** | Separate Model & Service | **Approved:** Cleanest and safest approach |
| **Storage** | `apps.json` via `StorageService` | **Approved:** Leverage atomic save & `.bak` pattern |
| **View Modes** | 4 layouts (96px, 48px, 24px, List) | **Simplify:** Cut to 2 layouts (Medium 48px & List 24px) |
| **Input Sources** | `.exe`, `.lnk`, `.url` | **Simplify:** Defer `.url`; restrict to `.exe` and `.lnk` |
| **Tray Integration** | Submenu | **Approved:** Add a direct "Tools" navigation item |
| **Hotkey System** | Integrated with `HotkeyManager` | **Approved:** Add distinct ID base & `AppHotkeyTriggered` |
| **Naming** | Open (Apps / Tools / Utilities) | **Adopt "Tools"** |
