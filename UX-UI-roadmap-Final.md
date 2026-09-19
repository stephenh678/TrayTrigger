# TrayTrigger UX/UI Roadmap (Final, Curated)

> **Status:** Approved by the owner on 2026-09-19 for release 1.5.0. Every owner decision is recorded in Section 7 and already applied to the items below. Approved work: UX-01 to UX-07 and UX-09 to UX-16. `UX-08` is withdrawn.
> **Date:** 2026-09-19. **Baseline:** v1.4.5, branch `prerelease`.
> **Source:** Curated from the three reviews in `UX-UI-roadmap.md` (Gemini, Claude, OpenAI/Codex), checked against the code, `CHANGELOG.md`, `git log`, and `review-notes/1.3.7-ui-overview-review.md`.
> **Audience:** The owner, and the model that implements the work (for example Opus 5). Each work item is written to be picked up without reading the source reviews.

---

## 1. How to use this file (implementer instructions)

1. Work items have stable IDs (`UX-01` …). Refer to them by ID in commits and discussion.
2. Do items in the order of Section 3 unless the owner says otherwise. Each item lists its dependencies.
3. **Line numbers are hints, not addresses.** They were correct on 2026-09-19 and will drift. Find the code by the quoted text or symbol, then confirm before editing.
4. One item per commit. Do not mix a refactor (`UX-09`) with a behaviour change.
5. Do not implement anything in Section 5 (Rejected) or Section 6 (Deferred). If an item seems to need one of them, stop and ask.
6. Items marked **Verify first** describe a concern found by reading source, not a reproduced defect. Reproduce it before changing anything. If it does not reproduce, record that and skip the item.

### Project conventions

| Topic | Rule |
| :--- | :--- |
| Build / test | `dotnet build`, `dotnet test` (see `CONTRIBUTING.md`). All tests must pass before a commit. |
| Visual regression | `App.DevDiagnostics.cs` and `App.Screenshot.cs` provide about 60 `--screenshot-*` modes. Capture before and after for every item that changes layout. **Back up `settings.json` and `games.json` first and restore them afterwards**; the harness runs against the real user data folders. |
| Styles | Shared styles, colours and brushes live in `App.xaml`. Do not add new inline hex colours. Add a named brush instead. |
| Iconography | Segoe MDL2 / Fluent glyphs through the `IconFont` resource. No emoji (removed in 1.3.7). |
| Copy | A control's name must match its Help topic title in `Help/**/*.md`. If a label changes, change the Help file in the same commit. |
| Changelog | User-facing changes get a bullet under the next version in `CHANGELOG.md`, written for players, not for the git log. |
| Dependencies | The app has two NuGet packages. Adding a third needs owner approval. |

### Effort scale

`S` = under half a day, one or two files. `M` = one to two days, several files. `L` = multi-day, or touches the structure of `MainWindow.xaml`.

---

## 2. Selection principles

These rules decided what made the list. Apply the same rules to any new idea.

1. **Fix what affects every screen before what affects one.** A 10-line change to `App.xaml` beats a new feature.
2. **Correctness of basics before modernization.** Readable text, keyboard conventions and focus visibility come before backdrops, chrome and new controls.
3. **No change justified by an unmeasured number.** Performance work starts with a measurement and has a go/no-go gate.
4. **Do not redo recent work.** Edit Game was restructured in 1.4.2, Settings was regrouped in 1.4.2, and the dark checkbox landed in 1.4.5. A restyle of any of these needs a stronger reason than taste.
5. **Do not retry what was already tried and dropped.** See Section 5.1.
6. **TrayTrigger is a tray-first desktop tool.** It is not a couch or handheld front end.

---

## 3. The roadmap at a glance

| ID | Item | Priority | Effort | Source | Depends on |
| :--- | :--- | :---: | :---: | :--- | :--- |
| UX-01 | Muted-text contrast and an 11 px size floor | P1 | S | All three | none |
| UX-02 | Dark tooltip style | P1 | S | Claude | none |
| UX-03 | Esc cancels and Enter confirms in every dialog | P1 | S | Claude | none |
| UX-04 | Focus-visibility audit (**Verify first**) | P1 | S | Claude, Codex | none |
| UX-05 | Automation names for icon buttons and status badges | P1 | M | Claude, Codex | none |
| UX-06 | Selection toolbar for batch actions | P2 | M | Codex | none |
| UX-07 | Honest restore-point and reset wording; profile summary in Edit Game | P2 | M | Codex | none |
| ~~UX-08~~ | ~~Virtualize the grid views; debounce search~~ **Withdrawn by owner, do not implement.** See `D-06`. | n/a | n/a | Gemini, Claude | n/a |
| UX-09 | Extract Library, Settings and About views; share one card template | P2 | L | Gemini, Claude | UX-01 to UX-05 landed |
| UX-10 | Shorter Welcome with an honest secondary button | P2 | S | Codex | none |
| UX-11 | Longer, pausable undo; undo for Settings removals | P2 | S | Codex, Claude | none |
| UX-12 | Honour the Windows reduce-motion setting | P2 | S | Claude | none |
| UX-13 | Narrow-window pass, Library toolbar excluded (**Verify first**) | P2 | M | Codex, Claude | UX-06 |
| UX-14 | Small polish batch | P3 | S each | Claude, Codex | none |
| UX-15 | Edit Game: "All" plus section tabs, matching Settings and System | P2 | M | Gemini, Codex, owner | UX-03 |
| UX-16 | Optional search box in the existing tray right-click menu (**Verify first**) | P2 | M | Owner, replacing Gemini's flyout | none |

**Release plan (owner decision):** everything ships in one release, **1.5.0**, through two betas.

| Beta | Theme | Items | Why grouped |
| :--- | :--- | :--- | :--- |
| **1.5.0-beta.1** | Everything the user can see | UX-01 to UX-07, UX-10, UX-11, UX-12, UX-15, UX-16, and all of UX-14 | Visible, low-risk changes. Testers can judge them by looking. They also land before the refactor, so the refactor moves finished XAML. |
| **1.5.0-beta.2** | Under the hood | UX-09, then UX-13 | Structural changes whose success criterion is "nothing looks different". They need a beta of their own so a regression is easy to attribute. |

Within beta.1, do UX-01 to UX-05 first, then UX-15 (it depends on UX-03), then the rest. Do UX-16 last: it starts with a spike, and if the spike fails it drops out without holding up the beta. If beta.2 feedback finds a regression, fix it in a release candidate, not a third beta.

---

## 4. Work items

### UX-01 Muted-text contrast and an 11 px size floor

- **Priority / Effort:** P1 / S. **Source:** all three reviews agree.
- **Current state:** `ColorTextMuted` in `App.xaml` is `#6E6E7A`. It measures about 3.7:1 on `#121214`, about 3.4:1 on `#1C1C23` cards, and about 3.3:1 on `#1D1D25` dialogs. WCAG AA needs 4.5:1. About 130 text elements are under 11 px (`FontSize` 8.5 to 10.5), mostly in `MainWindow.xaml`, `Views/GameEditDialog.xaml` and `Views/SystemView.xaml`. Commit `20f16c7` already moved two Edit Game hints to the secondary colour for this reason; this item finishes the job globally.
- **Change:**
  1. Set `ColorTextMuted` to `#8A8A96` (about 5.0:1 on cards, about 5.5:1 on the background). Owner decision: use this value, not Gemini's `#8E8E9C`.
  2. Raise every `FontSize` below 11 to 11, **except** text inside a badge or pill (PLAYING, MISSING, HIDDEN, platform pills, OPT-IN, ADMIN, RESTART, RECOMMENDED, IN LIBRARY). Those may stay at 9 to 10 px.
  3. Do not raise description text to 13 to 14 px (see Rejected, `R-12`).
- **Acceptance:** No readable (non-badge) text under 11 px. Muted text on every surface is at least 4.5:1. Before/after screenshots show no clipped or newly wrapped labels on poster cards, list rows, and the status bar at the 720 px minimum width.
- **Gotcha:** Muted and secondary (`#9E9EAA`) become close. That is acceptable; do not darken secondary to compensate.

### UX-02 Dark tooltip style

- **Priority / Effort:** P1 / S. **Source:** Claude.
- **Current state:** `App.xaml` has implicit styles for `ScrollBar` and `ContextMenu` but none for `ToolTip`. Every tooltip renders as the stock light Windows box. Several tooltips are full sentences.
- **Change:** Add an implicit `Style TargetType="ToolTip"` in `App.xaml`: background `BrushSurfaceActive`, border `BrushBorderLight`, foreground `BrushTextPrimary`, `AppFont` at 12 px, padding about 10,6, corner radius to match context menus, `MaxWidth` about 320 with wrapping text. String tooltips need a `ContentTemplate` with a wrapping `TextBlock`.
- **Acceptance:** Tooltips on the view-mode buttons, the ADMIN and RESTART badges, and the "End Session" and "Force Close" menu items are dark and wrap. Tooltips that already use custom content still render.

### UX-03 Esc cancels and Enter confirms in every dialog

- **Priority / Effort:** P1 / S. **Source:** Claude.
- **Current state:** `Views/GameEditDialog.xaml` (Cancel and Save Changes buttons near the end of the file) sets neither `IsCancel` nor `IsDefault`, and its code-behind has no Esc handler. `ScanForGamesDialog`, `FolderBatchImportDialog` and `LauncherDetectionDialog` set `IsCancel` but no `IsDefault` on the accent button. The other dialogs set both. Check `ToolEditDialog` too.
- **Change:** In every dialog, the cancel button gets `IsCancel="True"` and the accent button gets `IsDefault="True"`.
- **Gotchas:**
  - `Views/HotkeyRecorderBox` must keep receiving Esc and Enter while it is recording. Confirm a recording box does not close or save the dialog.
  - In Edit Game and Edit Tool, Esc with unsaved changes must ask before discarding (reuse `ModernDialog`). Esc with no changes closes at once.
  - Enter must not confirm while focus is in an editable `ComboBox` with its dropdown open.
- **Acceptance:** A table of every dialog, with Esc and Enter tested from a text box, a list and a button, all behaving the same way.

### UX-04 Focus-visibility audit (Verify first)

- **Priority / Effort:** P1 / S. **Source:** Claude; Codex asks for keyboard checks.
- **Current state:** `FocusVisualStyle="{x:Null}"` is set in three places: the `ModernCheckBox` style in `App.xaml`, the Details List `ItemContainerStyle` in `MainWindow.xaml`, and the list container style in `Views/ToolsView.xaml`. Buttons, combo boxes and checkboxes have their own `IsKeyboardFocused` triggers. Cards gained a focus ring in 1.3.7.
- **Change:** Tab through Library (all four views), Tools, System, Settings and About. For any control with no visible focus indicator, add a 1 px `BrushAccentHover` ring through an `IsKeyboardFocused` trigger. Likely candidates: Details List rows, Tools list rows, the category tab strip, the sub-tab strips, and the sidebar buttons.
- **Also verify:** Arrow keys move spatially (up and down by row) in the poster grids. Gemini reported sequential movement (Review 1, 6.2). WPF's default directional navigation is geometric, so this is probably already correct. Record the result.
- **Acceptance:** Every interactive control shows focus when reached by keyboard. No mouse-only focus rings are introduced.

### UX-05 Automation names for icon buttons and status badges

- **Priority / Effort:** P1 / M. **Source:** Claude; Codex asks for Narrator checks.
- **Current state:** `MainWindow.xaml` has 22 `AutomationProperties` entries and `ToolsView.xaml` has 9. `Views/SystemView.xaml` and almost every dialog have none.
- **Change:** Add `AutomationProperties.Name` to every icon-only button and to every status badge that conveys state (OPTIMAL, STANDARD, ON, OFF, OPT-IN, ADMIN, RESTART, ENABLED, DISABLED). Give each tweak row a composed name, for example "Windows Game Mode, optimal". Start with `SystemView.xaml`, `GameEditDialog.xaml`, `GameDetailsDialog.xaml`, then the list dialogs.
- **Acceptance:** A Narrator pass of one full flow (Scan for Games, Edit Game, launch, System tweaks) reads every control with a meaningful name. No control is announced as just "button".
- **Out of scope:** High-contrast theme support (Deferred, `D-03`).

### UX-06 Selection toolbar for batch actions

- **Priority / Effort:** P2 / M. **Source:** Codex 2.2.
- **Current state:** Multi-select (Ctrl/Shift+click, Ctrl+A) exists in Library and Tools. Batch actions live only in a right-click menu (`GameBatchContextMenu` in `MainWindow.xaml`, `ToolBatchContextMenu` in `ToolsView.xaml`). `MainViewModel` already exposes `HasSelection`, `SelectionSummary` and the `Batch*Command` set. Nothing on screen says a selection exists or what can be done with it.
- **Change:** When `HasSelection` is true, show a slim bar above the grid: `{SelectionSummary}` · Favorite · Change Category · Hide · More ▾ · Clear selection. "More" opens the existing batch context menu. Bind to the existing commands; add no new view-model logic. Do the same in `ToolsView`. The bar must overlay or replace the toolbar row so the grid does not jump.
- **Acceptance:** Selecting two cards shows the bar. Every action affects exactly the selected set, including after a search or filter change. Esc clears the selection (existing behaviour) and hides the bar. The bar is reachable by Tab.
- **Note:** Keep the context menu. The bar is for discovery; the menu is the fast path.

### UX-07 Honest restore-point and reset wording; profile summary in Edit Game

- **Priority / Effort:** P2 / M. **Source:** Codex 1.2 and 1.3, adopted in part.
- **Adopted:**
  1. The `RESTORE POINT: ON/OFF` badge in `Views/SystemView.xaml` shows a preference, not a result. Reword it to "Restore point before changes: On/Off". After Apply Performance Preset or a reset, report in the status line whether the restore point was created, skipped, or failed.
  2. Rename "Reset Defaults" to "Restore Previous Settings". Verified on 2026-09-19: the command restores the values from before TrayTrigger changed them, not Windows defaults (confirmation text in `SystemViewModel.ExecuteResetDefaultsAsync`; `Help/tweaks/overview.md`). Change the button, the confirmation dialog title, the busy toast, the completion message, the per-row "Revert to Default" button (to "Restore Previous"), and the Help heading in the same commit.
  3. In Edit Game's Performance card, under the profile combo, add a compact read-only summary of what the selected tier will do for this game: the enabled tweaks for that tier, with opt-ins that are off left out, and any that prompt for UAC marked. Bind it to the same profile toggles that `SystemViewModel` owns. One line per tweak, secondary colour, 11 px.
- **Not adopted:** Renaming OPTIMAL/STANDARD to "Applied/Not applied" (see `R-10`).
- **Acceptance:** A user can tell from Edit Game alone what "Optimized" will change for this game. After a preset run, the status line states the restore-point outcome.

### UX-08 Withdrawn

- **Owner decision (2026-09-19): do not implement.** This covers grid virtualization, the measurement step and the search debounce. Reason: users are not expected to have several hundred installed games, so there is no benefit to see. The ID is retired, not reused. See `D-06` for the revisit trigger and the preserved technical notes.

### UX-09 Extract Library, Settings and About views; share one card template

- **Priority / Effort:** P2 / L. **Source:** Gemini 3.1, Claude. **Do after UX-01 to UX-05** to avoid merge pain.
- **Why it is still worth doing without `UX-08`:** maintainability. Badge, label and font changes currently have to be repeated by hand across near-duplicate card templates inside one 4,490-line file.
- **Current state:** `MainWindow.xaml` is about 4,490 lines. `SystemView` and `ToolsView` are already `UserControl`s. Library (about 2,100 lines), Settings (about 1,100) and About (about 600) are inline. The Extra Large and Poster Grid card templates are near-duplicates that differ in size.
- **Change:**
  1. Move each section into `Views/LibraryView.xaml`, `Views/SettingsView.xaml` and `Views/AboutView.xaml`, with the code-behind handlers they need. This is a pure move with no behaviour or visual change.
  2. Merge the Extra Large and Poster Grid templates into one `DataTemplate` driven by a size resource. Keep Compact Icons separate; it is a different design.
- **Explicitly not part of this item:** switching to `ContentControl` + `DataTemplate` view swapping (see `R-05`). Keep the current `Visibility`-based section switching.
- **Gotchas:** Window-level handlers (`Window_Drop`, `Window_PreviewKeyDown`, Esc and Ctrl+F handling, `LibraryFilterPopup`, `GamesListBox` references) reach named elements. Expose what they need through the new controls rather than reaching into them by name. The `--screenshot-*` harness finds elements by name too; update it in the same commit.
- **Acceptance:** Every `--screenshot-*` capture is pixel-identical before and after, apart from live telemetry values. All tests pass. `MainWindow.xaml` is under about 800 lines.

### UX-10 Shorter Welcome with an honest secondary button

- **Priority / Effort:** P2 / S. **Source:** Codex 3.1.
- **Current state:** `Views/WelcomeDialog.xaml` has seven bullets and two buttons: "Scan for Games" (accent) and "Get Started" (outline). "Get Started" actually means "skip the scan".
- **Change:** Lead with one sentence: "Add your installed games, then launch them from the tray." Cut to three bullets: scan or drag-and-drop to add games; launch from the tray icon or a hotkey; optional per-game performance profiles, off until you choose one. Rename "Get Started" to "Skip for now". Move the SteamGridDB and "Learn more" bullets out; the empty library and Settings already cover them.
- **Acceptance:** The dialog fits without scrolling at 100% and 150% scaling. Both paths land on a Library whose next step is obvious.
- **Not adopted:** Gemini's three-step wizard (see `R-08`).

### UX-11 Longer, pausable undo; undo for Settings removals

- **Priority / Effort:** P2 / S. **Source:** Codex 3.2 (first half), Claude.
- **Current state:** `LibraryViewModel` finalizes a removal after a fixed 6-second `_undoToastTimer`. In Settings, removing a scan location, an ignored game or an ignored folder is immediate and cannot be undone.
- **Change:**
  1. Extend the undo window to 10 seconds. Pause the timer while the pointer is over the toast or it has keyboard focus.
  2. Reuse the same toast for the Settings removals listed above.
- **Acceptance:** Consecutive removals each remain undoable, or the earlier one finalizes cleanly. Exiting the app during a pending undo finalizes the removal, as it does today.
- **Not adopted:** Session-level undo history (see `R-11`).

### UX-12 Honour the Windows reduce-motion setting

- **Priority / Effort:** P2 / S. **Source:** Claude.
- **Current state:** About 35 storyboards in `MainWindow.xaml` and `SystemView.xaml`. Nothing reads `SystemParameters.ClientAreaAnimation`.
- **Change:** Expose a static `MotionEnabled` flag, read once at startup and on `SystemParameters.StaticPropertyChanged`. When it is off, hover-scale, fade and slide animations use a zero duration or are skipped. State changes must still happen; only the transition is removed.
- **Acceptance:** With Windows "Animation effects" off, cards do not scale on hover and toasts appear without a fade. With it on, nothing changes.

### UX-13 Narrow-window pass (Verify first)

- **Priority / Effort:** P2 / M. **Source:** Codex 4.1, Claude. **After `UX-06`**, because the selection bar changes the toolbar.
- **Current state:** The main window's minimum is 720 × 500. Edit Game's minimum width is 760 with a fixed 220 px artwork column.
- **Change:** At 720 px wide and at 150% display scaling, capture Library, Settings, System and Edit Game. Fix only what actually clips or overlaps.
- **Constraint (owner decision):** The Library header and toolbar rows are **off limits for now** (see `R-01`). Do not move, merge or collapse the three add buttons, the tabs, or the sort, filter and view controls. If something in those rows clips at 720 px, report it to the owner instead of fixing it.
- **Acceptance:** No clipped text or overlapping controls at 720 px, or at 150% scaling on a 1920 × 1080 display.

### UX-14 Small polish batch (P3, each S, independent)

| Sub-ID | Change | Where |
| :--- | :--- | :--- |
| 14a | Access keys (`_Save`, `_Cancel`, `_Add Selected`) on dialog footer buttons | `Views/*Dialog.xaml` |
| 14b | Make `GameCandidatePickerDialog` and `GameMatchPickerDialog` resizable with a minimum size, like Scan for Games | those two files |
| 14c | One pill style for static values (VRAM, Hz) and one for live values (Load, Used, Ping); same decimal precision for the current and max clock speed | `Views/SystemView.xaml` |
| 14d | Drive bars turn `BrushWarning` above 90% full | `Views/SystemView.xaml` |
| 14e | Retire the permanent "Hotkey: … to show or hide this window" status-bar hint after the first five sessions, and put the hotkey in the tray icon tooltip | `MainWindow.xaml`, `Models/AppSettings.cs`, `App.xaml.cs` |
| 14f | Replace the category tab strip's always-visible scrollbar with edge fades or chevrons | `MainWindow.xaml` |
| 14g | Working Directory and Launch Arguments go in an "Advanced launch options" expander that starts open when either has a value (Codex 2.1, partial). Do this as part of `UX-15`. | `Views/GameEditDialog.xaml` |
| 14h | End Session and Force Close in Game Details while the game is playing (GD-4, open since 1.3.7) | `Views/GameDetailsDialog.xaml`, `GameDetailsViewModel.cs` |
| 14i | New installs start with the sidebar expanded; existing users keep their setting (Codex 2.2, partial) | `Models/AppSettings.cs` |

---

### UX-15 Edit Game: "All" plus section tabs, matching Settings and System

- **Priority / Effort:** P2 / M. **Source:** Gemini 5.1 and Codex 2.1, reshaped by the owner. **Depends on:** `UX-03`.
- **Owner decision:** Adopt tabs, but in the app's own pattern. Settings, System and About each have an **All** tab followed by one tab per section. Edit Game gets the same.
- **Current state:** `Views/GameEditDialog.xaml` has a fixed 220 px artwork column on the left and a scrolling right column with four cards (since 1.4.2): Identity & Library, Launch, Performance, Scripts. The Scripts card only shows when scripts are enabled in Settings or the game already has one.
- **Change:**
  1. Add a tab strip above the right column: **All · Identity · Launch · Performance · Scripts**. Reuse the `SegmentedTabButton` style and the `TabBackgroundConverter` / `TabForegroundConverter` from `Converters/TabStateConverters.cs`, the same way the Settings tab strip in `MainWindow.xaml` does (search for `IsAllTab`).
  2. **All** is the default and shows today's layout unchanged. A section tab shows only that card. Tab names must match the card headings.
  3. Cards stay in the visual tree and switch by `Visibility`, as Settings does. Do not use a `TabControl` that unloads content. This keeps bindings, validation state and unsaved edits alive on every tab.
  4. The Scripts tab follows the Scripts card's existing visibility rule. When the card is replaced by its "scripts are hidden" stub, the tab is hidden too.
  5. Selecting a tab scrolls the column to the top (as 1.4.3 did for Settings).
  6. Keep the artwork column, the heading with the game's name, and the fixed Save/Cancel footer as they are.
  7. Include `UX-14g` (Advanced launch options expander) in the Launch card.
  8. Apply the same pattern to `Views/ToolEditDialog.xaml` only if it has three or more cards; otherwise leave it.
- **Why this shape answers the original objection:** the concern with plain tabs was that validation messages and the "scripts are turned off" notice end up on a tab nobody is looking at. With **All** as the default, the dialog opens exactly as it does today. In addition: when Save fails validation, switch to the tab that holds the first error (or to All) and bring the message into view.
- **Optional, ask the owner first:** callers that know their target could open the dialog on a tab, for example the System page's "assigned per game in Edit Game" hint opening on Performance.
- **Acceptance:** Opening Edit Game looks identical to 1.4.5 apart from the tab strip. Edits made on one tab survive switching tabs and are saved. A validation error on a hidden section becomes visible on Save. Esc and Enter behave per `UX-03` on every tab. Ctrl+Tab and Ctrl+Shift+Tab move between tabs.

---

### UX-16 Optional search box in the existing tray right-click menu (Verify first)

- **Priority / Effort:** P2 / M. **Source:** owner decision on 2026-09-19. It replaces Gemini's tray flyout, which the owner rejected after seeing a mockup (see `R-19`).
- **Goal:** Search from the tray without a new window. The search box is one more row in the current tray menu, styled like the rest of it, and can be switched off in Settings. The tray menu's configurability is a selling point, so this is another option, not a redesign.
- **Current state:** `App.UpdateTrayContextMenu()` in `App.xaml.cs` builds a WPF `ContextMenu` from scratch: Now Playing, Recent, Favorites, then games (flat, or grouped into category submenus), an optional Tools submenu, and the navigation items. It honours `ShowTrayMenuIcons`, `CompactTrayMenu`, `GroupTrayMenuByCategory`, `ShowToolsInTray` and `TrayLeftClickOpensMenu` from `Models/AppSettings.cs`. There is no way to search; a large grouped library means hunting through submenus.
- **Step 1, spike (gate, about half a day):** Put a `TextBox` in the tray `ContextMenu` and confirm, with the menu opened from the tray icon through H.NotifyIcon: (a) the box can take keyboard focus, (b) typed letters reach the box and do not trigger the menu's own type-ahead or access keys, (c) Space and Enter typed in the box do not activate a menu item, (d) the menu stays open while typing. Host the box as a direct item of the `ContextMenu` (any `UIElement` is allowed), not inside a `MenuItem`. **If focus or typing cannot be made reliable, stop and report to the owner. Do not fall back to a separate window.**
- **Change:**
  1. New setting `ShowTraySearch` (bool) in `Models/AppSettings.cs`. Add a checkbox in Settings > Tray Menu, in the card that controls what the menu lists: "Show a search box at the top of the tray menu", with a one-line hint. Default: `true`, for existing users and new installs (owner decision). Because the property is new, an existing `settings.json` without it must deserialize to `true`; set the default in the property initializer as the other settings do.
  2. When on, the first row of the menu is a search box: search glyph from `IconFont`, placeholder "Search games", same height, padding, font and colours as a tray row in the current layout (normal or compact). It sits above Now Playing.
  3. Empty query: the menu looks and behaves exactly as it does today.
  4. Non-empty query: hide the normal sections and show a flat list of matches in their place, built with the existing `CreateGameMenuItem`, so icons, compact layout and launch behaviour are identical. Match on game name and category, case-insensitive, the same fields the Library search uses, without the path. Skip hidden games, as the menu already does. Cap the list at about 10 rows. No matches: one disabled row, "No games match".
  5. If `ShowToolsInTray` is on, tools match too and are listed after the games.
  6. Keys: Down moves from the box into the results. Enter in the box launches the first result. Esc clears a non-empty query first and closes the menu on the second press. Closing the menu always clears the query.
  7. Keep the search row at the **top**. The tray menu opens upward from the taskbar with its top-left corner fixed, so when the result list changes height the top row stays put under the user's eyes.
  8. Do not call `UpdateTrayContextMenu()` to filter. It replaces the whole menu object and would close the open menu. Filter by toggling `Visibility` on the existing items and adding or removing result rows. Also make sure a library or session update that arrives while the menu is open does not wipe a query in progress.
  9. `AutomationProperties.Name="Search games"` on the box. Update `Help/traymenu/overview.md` and the About > Shortcuts row for the tray menu in the same commit.
- **Pairs well with:** `TrayLeftClickOpensMenu`. Left-click, type three letters, Enter.
- **Out of scope:** posters, a Now Playing panel, quick toggles, a hotkey that opens the tray menu, or any new window (`R-19`, `R-15`).
- **Acceptance:** With the setting off, the menu is identical to 1.4.5. With it on: typing filters at once, Enter launches the top match, and the normal menu returns when the box is cleared. It works in flat and grouped modes, in compact and normal layouts, with icons on and off, and with the taskbar at the bottom and at the top of the screen.

---

## 5. Rejected, with reasons

The owner asked that this file record why recommendations from the other reviews were not taken. "Rejected" means "not on this roadmap"; a new fact can reopen any of them.

### 5.1 Already tried and dropped

| ID | Recommendation | Source | Why not |
| :--- | :--- | :--- | :--- |
| R-01 | Re-lay out the Library header: drop the game count and subtitle, move the add buttons beside the title, put the tabs on the sort row | Claude 3.1 | Tried on 2026-09-18 (commit `7598b33`) and reverted the same day (`687c8b5`). The owner has since declared those rows off limits for now, including collapsing the three add buttons into one (Claude 3.2, Codex 4.1). Only the two small details in 14e and 14f survive. |
| R-02 | Mica Alt backdrop | Gemini 4.2 | Already spiked (`spike/mica-alt-sidebar`). The comment above `ColorSidebarBg` in `App.xaml` records the result: a flat `#202020` sidebar carried the whole readability gain without the transparency plumbing. Transparent WPF windows also cost rendering performance, which conflicts with Gemini's own Phase 1. |
| R-03 | Category named "Recent" or "Favorites" vanishing in the grouped tray menu (TR-5) | Claude 4.5 | Deliberately left as is in the 1.3.7 review as an edge case. No new information. |

### 5.2 Rejected on merit

| ID | Recommendation | Source | Why not |
| :--- | :--- | :--- | :--- |
| R-04 | Custom `WindowChrome` title bar with hand-built caption buttons and Snap Layouts hit-testing | Gemini 4.1 | The native title bar is already dark through `WindowThemeService` (DWM caption colour). The native bar provides Snap Layouts, correct maximize bounds, touch targets and accessibility at no cost. A custom bar has to re-implement each of those in WPF, and each is a known source of bugs. The gain is cosmetic and the risk is high. |
| R-05 | Swap views through `ContentControl` + `DataTemplate` for lazy creation | Gemini 3.1 (second half) | It destroys and rebuilds a view on every switch. That loses scroll position, the persistent per-tab search from 1.4.3, and expander state, unless all of it is re-plumbed through view models. The startup cost it aims to fix was never measured. The extraction half is adopted as `UX-09`. |
| R-06 | Edit Game as four tabs **replacing** the scrolling layout | Gemini 5.1, Codex 2.1 | Superseded, not rejected. The owner chose an "All" tab plus section tabs, which keeps today's layout as the default view and matches Settings and System. See `UX-15`. Plain tabs without "All" stay rejected: they hide validation messages and the "scripts are turned off" notice on tabs the user is not looking at. |
| R-07 | Settings as SettingsCards with ToggleSwitches | Gemini 5.2 | A full restyle of about 1,100 lines for appearance only. Settings was regrouped in 1.4.2, gained card search in 1.4.3, and the dark checkbox template in 1.4.5. WPF has no built-in ToggleSwitch, so this also means a new control and its focus, automation and high-contrast states. No usability problem was identified, only that it looks traditional. |
| R-08 | Three-step onboarding wizard | Gemini 5.3 | Step 1 already exists as `LauncherDetectionDialog`. Step 3 already exists as the Welcome "Scan for Games" button. Step 2 (choose tray click behaviour and a hotkey) asks a new user to decide things they have no basis for yet. The cheaper fix is `UX-10`. |
| R-09 | Segoe UI Variable as the app font | Gemini 4.3 | WPF does not support OpenType variable-font axes. It cannot use the optical-size axis, which is the stated benefit, and weight mapping for SemiBold and Bold is unreliable. Windows 10 falls back to Segoe UI anyway, so the app would look different per OS. `UX-01` is the real legibility fix. |
| R-10 | Rename OPTIMAL/STANDARD to "Applied/Not applied", and Optimize to "Apply" | Codex 1.2 | Codex's own validation note explains why: the app must not claim it applied a setting just because the current value matches. "Applied" makes exactly that claim. "OPTIMAL" describes the state regardless of who set it. The badge meanings are already documented in Help. The restore-point and reset wording parts are adopted in `UX-07`. |
| R-11 | Session-level undo history | Codex 3.2 | It needs a defined retention policy for removed metadata, cached art and playtime, plus UI to browse it. That is out of proportion for removing a shortcut that Scan for Games can bring back in seconds. `UX-11` covers the realistic slip. |
| R-12 | 13 to 14 DIP description text | Codex 1.1 | The app's base size is 12 to 12.5 px (buttons 12, checkboxes 12.5). Descriptions at 13 to 14 would be larger than the labels they describe and would re-wrap every Settings card. `UX-01` sets the floor at 11 and fixes contrast, which was the actual failure. |
| R-13 | XInput gamepad navigation | Gemini 6.3 | Out of product scope. TrayTrigger is a tray-first launch-control tool that gets out of the way. Steam Big Picture and Playnite's fullscreen mode own the couch case. It would add a polling input stack and a second navigation model to test on every screen. |
| R-14 | Pre-rendered blurred posters instead of `BlurEffect` | Gemini 1.2 | The two `BlurEffect`s (`GameDetailsDialog.xaml`, `GameEditDialog.xaml`) sit on a static backdrop with `RenderingBias="Performance"`. WPF caches a static effect; it is not re-rendered while unrelated elements animate. No stutter was measured. Revisit only if a profile shows these dialogs opening slowly. |
| R-15 | HAGS, DND and master-profile quick toggles in a tray flyout | Gemini 2.1 (part) | HAGS needs a restart and a UAC prompt, so it cannot be a quick toggle. DND and profiles are per game and applied at launch by design. A global override would contradict the "applied at launch, reverted at exit" model. |
| R-16 | Move view-mode selection into a labelled dropdown at narrow widths | Codex 4.1 (part) | The four view buttons take about 170 px and have had tooltips and automation names since 1.3.7. That row is also off limits for now by owner decision (see `R-01`). |
| R-17 | Light theme | raised and dismissed in Claude's review | Dark-only suits the product. Not worth the brush refactor on its own. |
| R-19 | Tray quick-launch flyout (mini-HUD) with search, Now Playing panel and poster rows (was `D-01`) | Gemini 2.1 | Rejected by the owner on 2026-09-19 after reviewing a mockup: it does not fit the product. The existing right-click menu stays the tray surface, and its configurability is a feature. The one part worth keeping, search from the tray, is adopted inside the existing menu as `UX-16`. |
| R-18 | `KeyboardNavigation.DirectionalNavigation="Cycle"` for 2D grid navigation | Gemini 6.2 | "Cycle" controls wrap-around at the edges, not spatial movement. WPF's arrow-key navigation is already geometric. Folded into `UX-04` as a check. |

---

## 6. Deferred (good ideas, not now)

| ID | Idea | Source | Why deferred | Revisit when |
| :--- | :--- | :--- | :--- | :--- |
| D-02 | Revisitable launch-failure history | Codex 3.2 | The launch popup already shows the failure reason with a link to open TrayTrigger, and the status line and log record it. A history surface is new UI for a rare event. | If support requests show that users miss the popup. |
| D-03 | High-contrast theme support | Claude 2.1 | It needs the 352 inline hex colours moved into named brushes first, then a `SystemColors`-based dictionary and a switch to `DynamicResource`. Large, with a small audience. `UX-05` does the screen-reader half now. | After `UX-09`, when the XAML is in smaller files and the hex consolidation can be done per view. |
| D-04 | Honour the Windows "Text size" setting (`TextScaleFactor`) | Codex 4.1 | WPF does not honour it automatically. It needs a global scale transform or scaled font resources, and every fixed-height row would need review. | After `UX-13` shows how the layouts cope with 150% DPI. |
| D-05 | Virtualize the Tools grid views | follows from `D-06` | Tool lists are tens of items, not hundreds. | Only if `D-06` is ever built and produces a reusable panel. |
| D-06 | Virtualize the Library grid views and debounce search (was `UX-08`) | Gemini 1.1, Claude 2.2 | Withdrawn by the owner on 2026-09-19. Typical libraries are installed games only, tens to low hundreds, where building every card is not noticeable. The work is large and puts hover zoom, draw order, multi-select and keyboard focus at risk for no visible gain. | **If TrayTrigger adds emulation or ROM library support**, or any other source that can add thousands of entries. Start with the measurement step in the notes below. |

---

### D-06 reference notes (not approved work)

Kept so the analysis is not lost. Do not implement unless the owner reopens `D-06`.

- **Effort:** L. Easier after `UX-09`, when there is one card template to convert.
- **Current state:** Extra Large, Poster Grid and Compact Icons are each an `ItemsControl` + `WrapPanel`, and they share one outer `ScrollViewer` (`MainWindow.xaml`, comment "ScrollViewer for WrapPanel Views"). Only the Details List (`GamesListBox`) virtualizes. Posters already decode at 368 px (`GameCardViewModel.ComputeHeavyState`). Search filters on every keystroke.
- **Step 1, measure (gate):** Build a synthetic `games.json` with 500 and 1,500 entries that reuse existing cached posters. Record the time for view-mode switch, category switch, first keystroke in search, and the working set. **Proceed only if view or category switch exceeds about 200 ms at 500 games.** Otherwise close the item with the numbers. Gemini's "300 to 600 MB" and "60 to 80%" figures were not measured by anyone; do not quote them.
- **Step 2, debounce:** Add a delay of about 150 ms to the search text binding or the filter refresh. This is cheap and worth doing even if Step 1 says stop.
- **Step 3, virtualize:** Each grid view needs its own scrolling `ItemsControl` or `ListBox` whose template owns the `ScrollViewer` with `CanContentScroll="True"`. A panel nested in an outer `ScrollViewer` cannot virtualize. Use a virtualizing wrap panel with recycling.
- **Decision needed if reopened:** a NuGet package (for example `VirtualizingWrapPanel`, MIT) or a small custom panel. Card sizes are fixed per view, which makes a custom panel feasible.
- **Gotchas (all must survive):**
  - Hover scale without reflow, and the hovered card painting above its neighbours (see the two comments near the top of `MainWindow.xaml` that mention "WrapPanel").
  - Ctrl/Shift multi-select and Ctrl+A across unrealized items. Selection state must live in the view model, not in containers.
  - Keyboard focus, Enter and Ctrl+Enter, the focus ring, and arrow navigation into rows that are not realized yet.
  - The context-menu highlight that stays on the right-clicked card.
  - Drag-and-drop onto the window, and the empty and no-match states.
  - Recycled containers must not flash the previous game's poster.
- **Acceptance:** The numbers from Step 1 re-measured and improved; every gotcha above demonstrated working.

---

## 7. Owner decisions and open questions

### Decided (2026-09-19)

| # | Question | Decision | Applied to |
| :--- | :--- | :--- | :--- |
| 1 | Grid virtualization | Do not implement. Revisit only if emulation support is added. | `UX-08` withdrawn, `D-06` |
| 2 | Muted-text colour | `#8A8A96` | `UX-01` |
| 3 | What "Reset Defaults" restores | Answered from the code: the values from before TrayTrigger's change. Rename to "Restore Previous Settings". | `UX-07` |
| 4 | Library toolbar row | Off limits for now. No collapsing, merging or moving. | `UX-13`, `R-01`, `R-16` |
| 5 | Tray quick-launch flyout | Do not implement. Instead, add an optional search box to the existing tray right-click menu, switchable in Settings. | `R-19`, `UX-16` |
| 5a | Tray search default | On, for existing users and new installs. | `UX-16` |
| 6 | Edit Game tabs | Adopt, as "All" plus section tabs, matching Settings and System. | `UX-15`, `R-06` |
| 6a | `UX-09` after the withdrawal of `UX-08` | Keep both parts in 1.5.0: extract the three views, and share one card template. | `UX-09`, beta.2 |
| 7 | Release grouping | One release, 1.5.0, in two betas: visible changes, then structural changes. | Section 3 |

### Still open

None. All owner questions are resolved as of 2026-09-19.
