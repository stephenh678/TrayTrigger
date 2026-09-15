# Tools section: design plan

Status: design agreed in discussion 2026-09-14. Built on the prerelease branch; see Build order.
Target: 1.4.4 prerelease. Reviewed by Gemini in [apps-section-review.md](apps-section-review.md); the
decisions below supersede it.

## Summary

An optional, lightweight **Tools** section for launching non-game programs gamers use alongside
games (DLSS Swapper, Vortex, MSI Afterburner, Discord). Off by default. No scanning. Save a
shortcut, launch it. When enabled it must feel native and follow the Library's look and workflow
where it applies: sidebar page, category tabs, favorites, sort, tray submenu, hotkeys and the same
launch popup games use. It does **not** carry over the Library's game features.

## Decisions

| Topic | Decision |
|---|---|
| Structure | Separate section with its own model (`ToolEntry`) and file (`tools.json`). Not a library category, not a `GameEntry` kind. |
| Name | Tools |
| Default | Off |
| Inputs | `.exe`, or `.lnk` whose target is an existing local `.exe`. No `.url` in v1. |
| Categories | Yes. Tools-only categories, never shared with game categories. Library-style tab strip. |
| Favorites | Yes. Star a tool; Favorites tab on the Tools page. Nothing special in the tray. |
| Page sort | Own dropdown on the Tools page: A to Z, Z to A, Favorites first. |
| Tray | Tools submenu only, flat list (no categories), own sort setting with the same three choices. No "Tools" navigation link. |
| Views | Three: Large Icons 64 px, Small Icons 32 px, List |
| Run as admin | Per-tool checkbox, pre-ticked from the shortcut's own "Run as administrator" flag. Windows UAC handles the prompt; TrayTrigger never intercepts it. A declined prompt is logged only. |
| Launch popup | Tools use the same popup and the same popup settings as games |
| Launch delay | 2 seconds for games and tools, one shared value. **Done** (`LibraryViewModel.LaunchDispatchDelay`). |
| Not included | Scanning, online lookup, platform detection, art, game details, sessions, performance profiles, CPU cores, scripts, playtime, last played, hidden, filters, recently launched |
| Discoverability | Changelog and help topic only. No welcome or What's New prompt. |

## Why a separate section

Every library entry is a `GameEntry`. Game launches go through `ProcessLauncherService.LaunchGame`
(sessions, Performance Profile, scripts, playtime, End Session / Force Close in the tray). Drops run
`PlatformLookupService` and an online Steam title search. Steam/RAWG/SteamGridDB enrichment fetches
art. Cards, filters and Edit Game assume a game. Reusing `GameEntry` for tools would need an
"is tool" guard at every one of those points, and every future game feature would have to remember
it. Tools only need a small part of the Library (categories, favorites, sort, views, search), so a
separate model that borrows the Library's shared styles and dialogs is the smaller, safer build.

## Settings

- **General tab: "Enable Tools"** (`EnableTools`, default `false`).
- **Tray Menu tab: "Show Tools in tray menu"** (`ShowToolsInTray`, default `false`). Hidden while
  Tools is disabled. Keeps its value when Tools is toggled.
- **Tray Menu tab: "Tools sort order"** (`ToolsTraySortOption`, default A to Z). Hidden with the option above;
  greyed out while "Show Tools in tray menu" is off.
- **Tools page state** (saved like the Library's): `ToolsViewMode` (default Large Icons),
  `ToolsSortOption` (default A to Z), `LastToolsCategoryTab` (default All).
- **Launch popup settings** (`ShowLaunchPopup`, `ShowLaunchPopupOnEveryLaunch`): labels and help say
  "games and tools" while Tools is enabled, "games" otherwise.
- **Turning Tools off:** hides the sidebar button and tray submenu, unregisters tool hotkeys, keeps
  `tools.json`. If the Tools page is showing, navigate to Library.
- **Reset settings to defaults:** turns Tools off, never deletes `tools.json`.
- **Uninstall:** the installer's existing data-folder removal already covers `tools.json`.

## Data

- `Models/ToolEntry.cs`: `Id`, `Name`, `TargetPath`, `Arguments`, `WorkingDirectory`, `RunAsAdmin`,
  `IconPath`, `Hotkey`, `Category` (default Uncategorized), `IsFavorite`.
- `%AppData%\TrayTrigger\tools.json` via `StorageService.LoadTools` / `SaveTools`, with the same
  `.tmp` write and `.bak` fallback as `games.json`. `List<ToolEntry>` registered in `AppJsonContext`.
- Categories normalized with the same rules as games (`LibraryConstants.NormalizeCategory`: trimmed;
  blank, All, Favorites or Hidden become Uncategorized), but the set of tool categories is built
  only from tools.
- Icons cached in the shared `Icons\{Id}.png` folder via `IconExtractorService`. Removing a tool
  deletes its icon. Game cleanup only deletes files keyed to removed game IDs, so tool icons are safe.

## Adding tools

- Drag onto the Tools page, or **Add Tool...** with a file picker filtered to `.exe` / `.lnk`.
- A tool added while a category tab is selected goes into that category; from All or Favorites it is
  Uncategorized.
- `ShortcutService.Resolve` supplies target, arguments, working folder and icon. Extend it to read the
  shortcut's run-as-administrator flag (`IShellLinkDataList::GetFlags`, `SLDF_RUNAS_USER`).
- Validation: target must be a local, existing `.exe`. Network paths refused. Anything else refused
  with a plain reason.
- Working folder falls back to the exe's own folder (already `ShortcutService` behaviour).
- Name from the file name minus " - Shortcut". Adding the same program with the same arguments as a
  tool already in Tools asks first; different arguments make a different tool.
- Already running: a tool with no arguments matches any running copy of its exe. A tool with arguments
  only matches the copy TrayTrigger started for it, so tools sharing a program each start their own.
- **Drop routing:** `MainWindow.Window_Drop` sends every drop to game import today, whatever page is
  showing. It must route to Tools import while the Tools page is showing. Other pages keep today's
  behaviour.

## Tools page

- `NavSection.Tools`; sidebar button between Library and System, visible only while enabled.
- `Views/ToolsView.xaml` + `ViewModels/ToolsViewModel.cs`, built like `SystemView`, so
  `MainWindow.xaml` does not grow.
- **Follows the Library's look:** reuse the shared styles already in `App.xaml`
  (`SegmentedTabButton`, `CardHoverCircleButton`, `MenuSectionHeaderStyle`, menu item styles), the
  `SearchBox` control and the existing dialogs (`QuickInputDialog` for rename and category,
  `ModernDialog` for confirmations). Card container and hover styles that currently live in
  `MainWindow.xaml` resources (`PosterCardContainerStyle` and its zoom storyboards) move to shared
  resources so tool cards match. That move changes nothing for games.
- **Toolbar:** search box, sort dropdown (A to Z, Z to A, Favorites first), view switcher, Add Tool.
- **Category tabs:** All, Favorites, then each tool category A to Z, same tab strip as the Library.
  A category tab disappears when its last tool leaves it. Selected tab remembered across restarts.
  Empty Favorites tab shows "No favorites yet", like the Library.
- **Empty state** explaining drag-and-drop when there are no tools.
- **Card:** icon, name, star on hover to favorite (same as game cards). Missing target shows the same
  missing state games use; launching offers Locate.
- **Context menu:** Launch, Add to / Remove from Favorites, Edit..., Rename..., Change Category...,
  Change Icon..., Open File Location, Remove.
- **Keyboard:** Enter launches, F2 renames, Delete removes after confirmation, Ctrl+F searches.
- Multi-select with Ctrl+click, Shift+click and Ctrl+A; right-clicking a selection opens a batch menu (favorite, category, Run as administrator, remove).

### Views

| View | Layout | Icon |
|---|---|---|
| Large Icons (default) | Wrap grid, name under icon | 64 px |
| Small Icons | Dense wrap, name beside icon | 32 px |
| List | Rows: icon, name, category, target path, hotkey | 24 px |

One `ListBox` with `ItemsPanel` + `ItemTemplate` swapped per mode. Decode cached icons at display size.

### Sort

| Option | Order |
|---|---|
| Alphabetical (A - Z) | Name ascending |
| Alphabetical (Z - A) | Name descending |
| Favorites first | Favorites A to Z, then the rest A to Z |

Same three options for the page (`ToolsSortOption`) and the tray (`ToolsTraySortOption`), stored
separately. Names compared case-insensitively, as the Library does.

## Edit Tool dialog

Name, category (editable picker suggesting existing tool categories), target, arguments, working
folder, Run as administrator, hotkey (reuse `HotkeyRecorderBox` and its conflict warnings), icon,
favorite. Same card styling as Edit Game.

## Launching

New `Services/ToolLauncherService.cs`, separate from game sessions:

1. Target missing: failure with "Locate executable...".
2. Already running (same image path): bring its window to front. Shared helper extracted from
   `ProcessLauncherService.TryActivateRunningExecutable`. When the running tool has no window
   (tray-only tools such as MSI Afterburner or RTSS), show a short "already running" notice in the
   popup or status bar so the launch doesn't look like it did nothing.
3. Otherwise `Process.Start` with `UseShellExecute = true`, arguments, working folder, and
   `Verb = "runas"` when Run as administrator is ticked. Tools whose own manifest requires admin get
   the UAC prompt from Windows either way.
4. `Win32Exception` 1223 (UAC declined): log, close the popup quietly, show nothing.
5. Other exceptions: failure, same as games.

TrayTrigger's window is never minimized for a tool launch.

**In-flight guard:** a tool that is already in its launch delay or waiting at a UAC prompt ignores
further launch requests, so a double press can't start two copies or two prompts.

**Launch delay:** tools use `LibraryViewModel.LaunchDispatchDelay` (2 s) and the same toast timing as
games. Move the constant somewhere neutral when the tool launcher needs it.

### Launch popup

- Refactor `LaunchPopupCoordinator` / `ILaunchPopup` from `GameEntry` to a small launch-target
  description (id, name, icon) that games and tools both supply. Game behaviour unchanged.
- Same show rules: tray or hotkey launch with the window hidden; every launch with
  `ShowLaunchPopupOnEveryLaunch`. Otherwise the in-window "Launching..." notice.
  `MinimizeOnGameLaunch` does not apply to tools.
- Closes once the tool is started (no session to wait for), after the existing 1.5 s minimum.
  No "Waiting for <platform>" text for tools.
- Failures: missing exe offers Locate; other errors offer Open TrayTrigger; with the window in front
  a dialog is used. Same as games.

## Tray and hotkeys

- With Tools on and "Show Tools in tray menu" on: a "Tools (N)" submenu after the games list, above
  the separator. Flat list in `ToolsTraySortOption` order: no categories, no favorites section.
  Omitted when there are no tools. Rebuilt when tools change.
- Hotkey registration moves out of `LibraryViewModel` (which only knows games) to one place that
  registers games and tools. Tools get their own ID range and a `ToolHotkeyTriggered` event.
- `CheckAvailability` checks games and tools together, including tools while the section is off.
  If a game took a tool's hotkey while Tools was off, that tool hotkey is skipped on re-enable with
  a notice.

## Supporting work

- Help topic (`Help/`), README and site section, About features list, CHANGELOG.
- Diagnostics report: Tools on/off, tool count, category count, tray option.
- Settings search keywords for the three new settings.
- Tests:
  - settings defaults (Tools off, tray off, sort A to Z, view Large Icons);
  - `ToolEntry` JSON round-trip;
  - drop validation (.exe ok, .lnk to .exe ok, .lnk to .bat refused, .url refused);
  - shortcut run-as-admin flag;
  - tool categories never appear in game categories and vice versa;
  - category normalization and tab rebuild when the last tool leaves a category;
  - favorites tab contents;
  - all three sort options, page and tray independently;
  - launch decisions, UAC decline stays silent, in-flight guard, already-running notice;
  - popup closes after tool start;
  - hotkey conflicts across games and tools;
  - view mode normalization;
  - drop routing by page.

## Out of scope for v1

.url links, Microsoft Store apps, hidden tools, filters, drag-to-reorder,
recently launched sort, categories or favorites in the tray, running-state indicator, launching tools
with a game.

## Build order

1. ~~Launch delay to 2 s for games.~~ Done, committed in 1.4.4-beta.2.
2. ~~Popup refactor to a launch-target description, with tests, no behaviour change.~~ Done (`LaunchTarget`).
3. ~~Hotkey registration moved to a shared place, no behaviour change.~~ Done (`HotkeyBinding`, `MainViewModel.UpdateHotkeys`).
4. ~~Card styles moved to shared resources.~~ Not needed: the icon and list cards Tools copies only use styles already in `App.xaml`; the poster zoom styles aren't used by those views.
5. ~~Tools model, storage, settings.~~ Done.
6. ~~Tools page: views, category tabs, favorites, sort, search, add/edit/remove, drop routing.~~ Done.
7. ~~Tool launcher, popup and hotkeys wired in.~~ Done.
8. ~~Tray submenu and tray sort setting.~~ Done.
9. ~~Help, docs, diagnostics, changelog.~~ Done (`Help/tools/overview.md`, README, site, wiki section table, diagnostics line, CHANGELOG).

Dev capture: a Debug build renders the page with sample tools, saving nothing:
`TrayTrigger.exe --screenshot-tools <file.png> [Large Icons|Small Icons|List]`.
