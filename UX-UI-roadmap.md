# TrayTrigger: Multi-Model UI/UX Reviews & Implementation Roadmap

> **Target Application:** TrayTrigger (C:\Users\steph\Desktop\TrayTrigger)  
> **Platform & Framework:** .NET 10.0 Windows Desktop (WPF)  
> **Purpose:** Multi-agent comparative review repository to evaluate UI, UX, architecture, and performance recommendations from different LLMs before implementation.

---

## Master Comparison & Decision Matrix

Use this table to synthesize and vote on recommendations across models:

| Category | Gemini Recommendation | Claude | OpenAI / Codex | Decision / Selected Action |
| :--- | :--- | :--- | :--- | :--- |
| **UI Virtualization** | Add Virtualizing WrapPanel to Grid & Icons views | Agree: virtualize the three grid views; add a ~150 ms search debounce | | |
| **Tray Experience** | Build Quick-Launch Flyout (Mini-HUD) for Left-Click / Hotkey | Not evaluated | | |
| **XAML Architecture** | Extract Library, Settings, About into separate UserControls | Agree: extract the three views, and share one card template across the three near-duplicate grid templates | | |
| **Windows 11 Shell** | WindowChrome + Snap Layouts + Mica Alt backdrop | Not evaluated. Note: a comment in `App.xaml` records a Mica Alt spike that was dropped in favour of flat #202020 | | |
| **Dialog Ergonomics** | Refactor GameEditDialog into 4 segmented tabs | Esc cancels and Enter runs the accent action in every dialog; make the two picker dialogs resizable; add access keys | General, Launch, Performance, Scripts tabs; artwork under General | |
| **Settings UI** | Convert indented CheckBoxes to modern SettingsCards | Undo toast for destructive removals (scan location, ignored game) | Clarify actual state, restoration behavior, and session versus persistent changes | |
| **Typography & Contrast** | Segoe UI Variable + bump ColorTextMuted to #8E8E9C (WCAG AA) | Muted text to about #8A8A96; 11 px floor for readable text, 9–10 px for badges only | Prioritize brighter secondary text and 13–14 DIP descriptions; validate contrast and scaling | |
| **First-Run Experience** | Interactive 3-step auto-detection wizard | Not evaluated (1.3.7 already added a Scan for Games button to Welcome) | Shorten welcome to three points; Scan for Games primary, Skip for now secondary | |
| **Selection & Navigation** | | Not evaluated | Expose batch actions in a selection toolbar; show navigation labels when space permits | |
| **Recovery & Undo** | | Reuse the Library undo toast in Settings; add End Session / Force Close to Game Details (open since 1.3.7) | Extend six-second undo and retain actionable launch failures | |
| **Responsive Layout** | | One split Add button; drop the Library subtitle and permanent hotkey hint; replace the tab strip's permanent scrollbar | Adapt toolbar and editor for narrow windows and enlarged text | |
| **Tooltips** | | Add an implicit dark `ToolTip` style with MaxWidth and wrapping; none exists today | | |
| **Keyboard Focus** | | Audit the three `FocusVisualStyle={x:Null}` sites so list rows, tabs and sidebar still show focus | Keyboard and Narrator checks | |
| **Screen Reader & High Contrast** | | Automation names on the System page and dialogs; respond to `SystemParameters.HighContrast` | Narrator and high-contrast validation | |
| **Reduced Motion** | | Gate hover/scale storyboards on `SystemParameters.ClientAreaAnimation` | | |

---

# Review 1: Gemini (Senior .NET UI/UX Architect)

### Reviewer Profile
- **Model:** Gemini (30+ Years .NET Windows Desktop & UI/UX Specialist)
- **Review Date:** September 19, 2026
- **Focus:** WPF Rendering Pipeline, Memory Virtualization, Windows 11 Fluent Integration, and Tray Ergonomics.

---

### Executive Summary
TrayTrigger addresses an authentic and underserved gaming niche: automating the Windows desktop environment before, during, and after game sessions while staying lightweight in the system tray.

While the core functionality (process monitoring, hardware tweaks, DWM cloaking, and metadata enrichment) is robust, the user interface currently exhibits architectural friction common to scaling WPF applications:
- **Zero UI Virtualization** in the primary Poster Grid and Icon views, leading to memory spikes and frame stutter for mid-to-large libraries (300–1,500+ games).
- **A Traditional Win32 Context Menu** for tray interactions, which breaks down when displaying large libraries or deep subcategories.
- **A Monolithic 4,494-line MainWindow.xaml** that instantiates all five main views into the visual tree on startup, degrading cold boot times and complicating maintenance.
- **Legacy Window Chrome & Aesthetics**, missing native Windows 11 Fluent features such as Mica/Mica Alt backdrops, extended title bars with Snap Layouts, and Segoe UI Variable typography.

---

### Strategic Priority Matrix (Gemini)

| Priority | Milestone | Focus Area | Key Deliverable | Impact |
| :---: | :--- | :--- | :--- | :--- |
| **P0** | **Phase 1** | Rendering & Performance | UI Virtualization for Grid/Icons (VirtualizingWrapPanel) | Eliminates UI lag; cuts RAM by 60–80%; smooths 144Hz+ scrolling |
| **P1** | **Phase 2** | Core UX | Tray Quick-Launch Flyout / HUD (Win+Alt+G / Tray Left-Click) | Fulfills the tray-first promise; instant search and quick launch |
| **P1** | **Phase 3** | Architecture | Decompose MainWindow.xaml into modular UserControls | Speeds up build times; reduces startup memory footprint |
| **P2** | **Phase 4** | Shell Integration | Modern WindowChrome, Windows 11 Snap Layouts, Mica Alt | Transforms custom WPF skin into a native Windows 11 app |
| **P2** | **Phase 5** | Dialog Workflows | Tabbed GameEditDialog & Interactive Setup Wizard | Eliminates infinite scrolling; speeds up onboarding |
| **P3** | **Phase 6** | Polish & Accessibility | WCAG AA Contrast, Segoe UI Variable, Controller Nav | Delivers crisp visuals across 4K displays and handheld PCs |

---

### Phase 1: Performance & Rendering Pipeline (P0)

#### 1.1 Implement Virtualization in Poster Grid & Icon Views
* **Current State:** In MainWindow.xaml, both Poster Grid and Compact Icons views use an unvirtualized ItemsControl wrapping an unvirtualized WrapPanel. Only the Details List view currently virtualizes.
* **Problem:** In libraries with hundreds of games, WPF instantiates thousands of visual elements (Border, Image, DropShadowEffect, LinearGradientBrush, ScaleTransform, RectangleGeometry clip masks). This causes high memory consumption (300MB–600MB+ for bitmaps), UI thread pauses during category switching or search, and sluggish resize cycles.
* **Target Solution:**
  - Replace the unvirtualized ItemsControl + WrapPanel combination with a Virtualizing Wrap Panel (such as WpfToolkit.Controls.VirtualizingWrapPanel or an equivalent custom virtualizing panel).
  - Configure the panel with container recycling:
    `xml
    VirtualizingPanel.IsVirtualizing=True
    VirtualizingPanel.VirtualizationMode=Recycling
    VirtualizingPanel.ScrollUnit=Pixel
    VirtualizingPanel.CacheLength=2
    VirtualizingPanel.CacheLengthUnit=Page
    `
  - Maintain the existing asynchronous decodePixelWidth image decoding logic in GameCardViewModel to ensure off-screen cards consume zero memory until scrolled into view.

#### 1.2 Eliminate Runtime Shader Blurs on the Composition Thread
* **Current State:** Ambient poster backgrounds in GameDetailsDialog and GameEditDialog apply a runtime WPF BlurEffect (Radius=24).
* **Problem:** Software/D3D shader effects force WPF to allocate intermediate off-screen render targets (MilCore surfaces), which causes stutter when cards are animated (such as the 1.05 hover zoom) or dialogs are opened.
* **Target Solution:**
  - Pre-render or compute low-resolution blurred thumbnails in background threads when poster art is cached to disk, or apply a pre-blurred static overlay rather than applying real-time pixel shader effects.

---

### Phase 2: Modern Tray-First UX & Quick-Launch HUD (P1)

#### 2.1 Re-architecting the Tray Experience
The application is named **TrayTrigger**, but user interaction through the tray is currently restricted to a standard Win32 / WPF context menu dynamically populated in App.xaml.cs.

**The Limitation:**
- When a user has 50+ games in a category, submenus easily extend off-screen and can accidentally close on mouse exit.
- When grouping is turned off, a flat list of 200 games spans multiple monitor columns with Win32 scroll arrows.
- There is no search capability from the tray.

**The Target Solution: The Quick Launcher Flyout (Mini-HUD)**
Introduce a modern, lightweight, borderless flyout window anchored directly above the system tray clock (or centered via hotkey):

`
┌─────────────────────────────────────────────────────────────┐
│ 🔍 Type to search games, tools, or categories...            │
├─────────────────────────────────────────────────────────────┤
│ 🟢 ACTIVE SESSION                                           │
│ Cyberpunk 2077  ·  42m elapsed  ·  Profile: [Optimized]     │
│ [ ⏹ End Session & Restore Tweaks ]   [ ✕ Force Close ]     │
├─────────────────────────────────────────────────────────────┤
│ ★ FAVORITES & RECENT                                        │
│ [Poster: Elden Ring]  [Poster: Baldur's Gate 3]  [Poster: ...] │
├─────────────────────────────────────────────────────────────┤
│ ⚡ QUICK TOGGLES                                             │
│ Master Profile: [ON]  │ DND: [ON]  │ HAGS: [Active]          │
├─────────────────────────────────────────────────────────────┤
│ ⚙ Settings    ⛶ Full Library    🔄 Scan Games    ⏻ Exit      │
└─────────────────────────────────────────────────────────────┘
`

- **Activation:**
  - Single left-click on the system tray icon (default behavior).
  - Global configurable hotkey (e.g., Win+Alt+G or Ctrl+Shift+Space).
- **Right-Click Tray Menu:** Keep clean, minimal, and reserved for administrative commands (*Open Library*, *Scan for Games*, *Settings*, *Exit TrayTrigger*).

---

### Phase 3: Architectural Refactoring & Modular XAML (P1)

#### 3.1 Decompose MainWindow.xaml (4,494 Lines)
* **Current State:** SystemView and ToolsView are decoupled into standalone UserControls under /Views, but LibraryView (~2,140 lines), SettingsView (~1,100 lines), and AboutView (~600 lines) remain inlined in MainWindow.xaml.
* **Problem:**
  - All five views are instantiated in memory when the application starts, relying on Visibility triggers (Collapsed / Visible).
  - Maintainability, version control diffs, and XAML designer tooling are negatively impacted by the massive file size.
* **Target Solution:**
  - Extract three new modular UserControls:
    - Views/LibraryView.xaml
    - Views/SettingsView.xaml
    - Views/AboutView.xaml
  - Refactor MainWindow.xaml to use dynamic ViewModel switching with a ContentControl:
    `xml
    <ContentControl Content={Binding CurrentViewViewModel}>
        <ContentControl.Resources>
            <DataTemplate DataType={x:Type vm:LibraryViewModel}>
                <views:LibraryView/>
            </DataTemplate>
            <DataTemplate DataType={x:Type vm:SettingsViewModel}>
                <views:SettingsView/>
            </DataTemplate>
            <DataTemplate DataType={x:Type vm:SystemViewModel}>
                <views:SystemView/>
            </DataTemplate>
            <DataTemplate DataType={x:Type vm:ToolsViewModel}>
                <views:ToolsView/>
            </DataTemplate>
            <DataTemplate DataType={x:Type vm:AboutViewModel}>
                <views:AboutView/>
            </DataTemplate>
        </ContentControl.Resources>
    </ContentControl>
    `
  - This enables lazy instantiation of Settings, System, and About views, reducing initial application boot time and memory footprint.

---

### Phase 4: Windows 11 Fluent Shell & Visual Modernization (P2)

#### 4.1 Native Windows 11 Title Bar with WindowChrome
* **Current State:** The window relies on standard Win32 OS title bar rendering with caption coloring via DwmSetWindowAttribute(DWMWA_CAPTION_COLOR).
* **Problem:** There is an abrupt line between the OS title bar and the dark Fluent UI, making the app look like a retrofitted legacy window rather than a modern native Windows 11 app.
* **Target Solution:**
  - Implement WPF WindowChrome with client-area extension:
    `xml
    <WindowChrome.WindowChrome>
        <WindowChrome CaptionHeight=42 CornerRadius=0 GlassFrameThickness=0 UseAeroCaptionButtons=False/>
    </WindowChrome.WindowChrome>
    `
  - Extend the left sidebar background directly to the top edge of the window.
  - Implement custom caption controls (Minimize, Maximize/Restore, Close) that support native Windows 11 Snap Layouts by handling WM_NCHITTEST returning HTMAXBUTTON (0x9).

#### 4.2 Mica Alt Backdrop Integration
* **Current State:** Static flat colors (#121214, #202020, #1C1C23).
* **Target Solution:** On Windows 11 Build 22621+, call DwmSetWindowAttribute with DWMWA_SYSTEMBACKDROP_TYPE = 4 (Mica Alt) and set the window background to transparent/translucent. This provides native wallpaper tinting and consistent dark mode depth.

#### 4.3 Typography Modernization
* **Current State:** Static FontFamily=Segoe UI.
* **Target Solution:** Update the global AppFont in App.xaml:
  `xml
  <FontFamily x:Key=AppFont>Segoe UI Variable Display, Segoe UI Variable Text, Segoe UI</FontFamily>
  `
  Segoe UI Variable is the standard font for Windows 11 and provides optical sizing for improved legibility of small 10pt–12pt metadata labels.

---

### Phase 5: Dialog Ergonomics & Workflows (P2)

#### 5.1 Tabbed Structure for GameEditDialog.xaml
* **Current State:** GameEditDialog (617 lines) puts four complex domains into a single vertical scroll view:
  1. Identity & Library Metadata
  2. Launch Configuration & Route
  3. Performance Profiles & Hardware Tweaks
  4. Pre-launch & Post-exit Automation Scripts
* **Problem:** Significant scrolling is required to configure common options, and validation warnings at the bottom of the scroll container are easily missed.
* **Target Solution:** Organize the right column of GameEditDialog into segmented tabs:
  `
  [ General & Art ]   [ Launch & Arguments ]   [ Performance Profile ]   [ Automation Scripts ]
  `
  This keeps all critical controls above the fold and eliminates vertical scroll fatigue.

#### 5.2 Settings Page: Modernize CheckBoxes to SettingsCards
* **Current State:** Settings use traditional desktop CheckBox controls with nested manual indentation margins (Margin=24,3,0,12).
* **Target Solution:** Adopt the standard Windows 11 **SettingsCard** layout:
  - Left: Clear icon, bold title, and descriptive subtitle.
  - Right: Touch-friendly ToggleSwitch, ComboBox, or action button.
  - Improves visual rhythm, hierarchy, and scan-ability.

#### 5.3 First-Run Onboarding (WelcomeDialog.xaml)
* **Current State:** A dialog containing a five-paragraph bulleted wall of text.
* **Target Solution:** Replace with an interactive **3-Step Setup Wizard**:
  - **Step 1: Detected Launchers**: Automatically detect installed launchers with checkboxes:
    > *We detected Steam (42 games), Epic (12 games), and Xbox Game Pass (5 games). Include them?*
  - **Step 2: Tray Behavior & Hotkey**: Choose default click action and set a global launch hotkey.
  - **Step 3: Initial Scan & Launch**: Single-click scan that transitions directly to the populated poster library.

---

### Phase 6: Accessibility, Gamepad & Couch Ergonomics (P3)

#### 6.1 Contrast Ratio Corrections (WCAG 2.1 AA)
* **Current State:** ColorTextMuted is defined as #6E6E7A.
* **Problem:** Against #121214 and #1C1C23, the contrast ratio is ~3.8:1, failing the WCAG 2.1 AA requirement (minimum 4.5:1 for standard body text).
* **Target Solution:** Adjust ColorTextMuted to #8E8E9C (5.2:1 contrast ratio) to improve legibility on HDR displays and TVs.

#### 6.2 2D Directional Arrow Navigation
* **Current State:** Navigating an ItemsControl with arrow keys moves sequentially item-by-item rather than spatially jumping rows.
* **Target Solution:** Implement spatial 2D keyboard navigation (KeyboardNavigation.DirectionalNavigation=Cycle), allowing Up/Down to traverse columns within the grid cleanly.

#### 6.3 Gamepad / Couch Gaming Navigation (XInput)
* **Target Solution:** Add optional XInput controller support for gaming handhelds (Steam Deck, ROG Ally) and couch setups:
  - D-Pad / Left Stick: Navigate poster grid.
  - 'A' Button: Launch selected game.
  - 'X' Button: Open Game Details.
  - 'Y' Button: Open Filter / Search.

---

### Suggested Implementation Order (Gemini)

`
[Sprint 1: Performance & Architecture]
├── 1. Add VirtualizingWrapPanel to Poster Grid & Icons
├── 2. Extract LibraryView, SettingsView, AboutView from MainWindow.xaml
└── 3. Adjust ColorTextMuted to #8E8E9C for WCAG AA compliance

[Sprint 2: Tray Experience]
├── 1. Build QuickLauncherFlyout window (mini-HUD)
├── 2. Wire single left-click tray activation & global hotkey (Win+Alt+G)
└── 3. Simplify right-click tray context menu to administrative actions

[Sprint 3: Windows 11 Shell & Polish]
├── 1. Add WindowChrome with extended title bar & Snap Layouts
├── 2. Enable DWM Mica Alt system backdrop
└── 3. Migrate typography to Segoe UI Variable

[Sprint 4: Dialog Refactoring]
├── 1. Refactor GameEditDialog into 4 segmented tabs
├── 2. Convert Settings CheckBoxes into SettingsCards with ToggleSwitches
└── 3. Replace WelcomeDialog with 3-step interactive onboarding
`

---

# Review 2: Claude (.NET UI/UX Review)

### Reviewer Profile

- **Model:** Claude (Fable 5.1)
- **Review Date:** September 19, 2026
- **Focus:** Accessibility (contrast, keyboard, screen reader, high contrast, motion), dialog keyboard conventions, theme consistency, Library layout density, and grid performance.
- **Method:** Read-only review of `App.xaml`, `MainWindow.xaml`, `Views/*.xaml` and code-behind, `GameCardViewModel.cs`, `CHANGELOG.md`, `review-notes/1.3.7-ui-overview-review.md`, and the three screenshots in `Assets/screenshots`. The application was not run; performance and assistive-technology behaviour were not measured.
- **Status:** Recommendations for consideration. No application changes implemented.

---

### Executive Summary

TrayTrigger 1.4.5 is in good shape. Almost every finding in the 1.3.7 UI review has been implemented: keyboard-focusable cards, Play-first context menus, a recorded hotkey box, accurate copy, and a separate "no matches" state. This review therefore covers what that review did not.

The best return is four small changes that touch every screen: brighter and larger muted text, a dark tooltip style, consistent Enter/Esc behaviour in dialogs, and a focus-ring audit. Each is a few lines in `App.xaml` or a few attributes. Grid virtualization matters next, but only once a library reaches a few hundred games. The rest is layout density and polish.

This review agrees with Review 1 on virtualization and on splitting `MainWindow.xaml`, and with Reviews 1 and 3 on muted-text contrast. It did not evaluate the tray flyout, WindowChrome, Mica, the Edit Game tabs, or onboarding.

---

### Strategic Priority Matrix (Claude)

| Priority | Milestone | Focus Area | Key Deliverable | Expected Impact |
| :---: | :--- | :--- | :--- | :--- |
| **P1** | **Phase 1** | Readability | Muted text to about #8A8A96 and an 11 px floor | Small grey text passes WCAG AA on every screen |
| **P1** | **Phase 1** | Theme Consistency | Implicit dark `ToolTip` style | Removes the one bright element in a dark app |
| **P1** | **Phase 1** | Dialog Keyboard | Esc cancels, Enter runs the accent action, everywhere | Dialogs behave the same from the keyboard |
| **P1** | **Phase 1** | Keyboard Focus | Audit three `FocusVisualStyle={x:Null}` sites | Tabbing never loses the focus indicator |
| **P1** | **Phase 2** | Assistive Technology | Automation names on dialogs and System page; high-contrast support | Usable with Narrator and high contrast mode |
| **P1** | **Phase 2** | Grid Performance | Virtualizing wrap panel and search debounce | Keeps view switches and filtering fast at 300+ games |
| **P2** | **Phase 3** | Library Density | Shorter header, split Add button, no permanent hotkey hint | More posters above the fold; less crowding at 720 px |
| **P2** | **Phase 3** | Settings Recovery | Undo toast for destructive removals | A wrong click no longer needs a full reset |
| **P2** | **Phase 3** | Motion & Dialog Sizing | Honour reduce-motion; resizable pickers | Respects a Windows setting; long paths stay readable |
| **P3** | **Phase 4** | Polish | Access keys, consistent pills, drive-bar colour, shared card template | Finish quality and maintainability |

---

### Phase 1: Global Quick Wins (P1)

#### 1.1 Raise Muted-Text Contrast and Set a Size Floor

* **Current State:** `BrushTextMuted` is #6E6E7A. It is used for "Never played", the driver date, the status bar, and hints. There are about 130 uses of text under 11 px; `MainWindow.xaml` alone has 14 at 9 px and 14 at 9.5 px.
* **Problem:** Contrast is about 3.7:1 on the #121214 background and about 3.4:1 on #1C1C23 cards. WCAG AA needs 4.5:1 for normal text. The failing colour is used at the smallest sizes, which compounds it.
* **Target Solution:**
  - Raise muted text to roughly #8A8A96 (about 5:1 on cards, about 5.5:1 on the background).
  - Set a floor of 11 px for anything the user has to read. Keep 9–10 px for badges only.
* **Validation:** Check each foreground/background pair, including text over poster art. Review wrapping after the size change.

#### 1.2 Add a Dark Tooltip Style

* **Current State:** `App.xaml` styles ScrollBar, ContextMenu, ComboBox and CheckBox. It has no `ToolTip` style.
* **Problem:** Every tooltip renders as the stock light Windows box. Several tooltips are full sentences and run wide.
* **Target Solution:** Add an implicit `ToolTip` style using `BrushSurfaceActive`, `BrushBorderLight` and `BrushTextPrimary`, with a `MaxWidth` of about 320 and wrapping text.
* **Validation:** Hover the view-mode buttons, the ADMIN/RESTART badges, and the long context-menu tooltips (End Session, Force Close).

#### 1.3 Make Enter and Esc Behave the Same in Every Dialog

* **Current State:** `Views/GameEditDialog.xaml:609` has neither `IsCancel` nor `IsDefault`, and its code-behind has no Esc handler. Scan for Games, Batch Add and Launcher Detection set `IsCancel` but have no `IsDefault` on the accent button. The remaining dialogs set both.
* **Problem:** Esc closes some dialogs and not others. Enter confirms in some and does nothing in others.
* **Target Solution:** One rule: Esc cancels, and Enter runs the accent action. For Edit Game, confirm before discarding unsaved edits on Esc. Keep Enter from firing while the hotkey recorder or a multi-line box has focus.
* **Validation:** Open each dialog and press Esc and Enter with focus in a text box, a list, and a button.

#### 1.4 Audit the Disabled Focus Rings

* **Current State:** `FocusVisualStyle={x:Null}` is set at `App.xaml:724` (checkbox), `MainWindow.xaml:2388` (list rows) and `Views/ToolsView.xaml:396`. Buttons, combo boxes and checkboxes have their own `IsKeyboardFocused` triggers.
* **Problem:** Removing the default ring without a replacement everywhere leaves some controls with no visible focus. This is a source-based concern, not a reproduced defect.
* **Target Solution:** Tab through every page. Confirm list rows, the category tab strip, the settings tab strips and the sidebar buttons show focus. Add an accent ring where one is missing.
* **Validation:** Keyboard-only pass of Library, Tools, System, Settings and About.

---

### Phase 2: Assistive Technology & Grid Performance (P1)

#### 2.1 Add Automation Names and High-Contrast Support

* **Current State:** `MainWindow.xaml` has 22 `AutomationProperties` entries and `ToolsView.xaml` has 9. `SystemView.xaml` and nearly every dialog have none. Nothing reads `SystemParameters.HighContrast`. All brushes are `StaticResource` hex values; there are 352 inline hex colours across the XAML.
* **Problem:** Icon-only buttons and the ON/OFF, ADMIN and RESTART badges are unnamed for Narrator. High contrast mode changes nothing in the app.
* **Target Solution:**
  - Name every icon-only button and status badge, starting with the System page and Edit Game.
  - In high contrast mode, swap the brush dictionary for one built on `SystemColors`.
  - Moving inline hex values into named brushes is the prerequisite, and it also keeps a light theme possible later.
* **Validation:** Narrator pass of one full flow (scan, edit, launch). Switch Windows to a high-contrast theme and check every page.

#### 2.2 Virtualize the Three Grid Views and Debounce Search

* **Current State:** Extra Large, Poster Grid and Compact Icons are each an `ItemsControl` with a `WrapPanel` inside a `ScrollViewer` (`MainWindow.xaml:1056`, 1535, 2008). Only the Details List virtualizes. Posters are decoded at 368 px, which helps. The search box filters on every keystroke; no debounce was found.
* **Problem:** Every card is built whether or not it is on screen. At around 300 or more games, expect slow view switches and slow filter typing. This was not measured.
* **Target Solution:** Use a virtualizing wrap panel with container recycling, and debounce search input by about 150 ms. Agrees with Review 1, section 1.1.
* **Validation:** Test with a synthetic library of 500 and 1,500 games. Confirm Ctrl/Shift multi-select, keyboard focus and the hover Play button survive container recycling.

---

### Phase 3: Layout Density, Recovery & Motion (P2)

#### 3.1 Give the Library Header Back to the Posters

* **Current State:** In `Assets/screenshots/library-grid.png`, the title, subtitle, tab strip, toolbar and status bar take about 270 px of a 700 px window before the first poster. The category strip shows a permanent horizontal scrollbar.
* **Target Solution:**
  - Drop the subtitle once the library has games.
  - Merge the game count into the tab strip (for example "All Games 15").
  - Replace the permanent scrollbar with fade edges or chevrons.

#### 3.2 Collapse the Three Add Buttons

* **Current State:** Add Game, Add Folder and Scan for Games sit side by side at equal weight.
* **Target Solution:** One split button, "Add" with a dropdown, and Scan for Games as the secondary button. This also stops the toolbar crowding at the 720 px minimum width. Agrees with Review 3, section 4.1.

#### 3.3 Retire the Permanent Hotkey Hint

* **Current State:** The status bar always shows "Hotkey: Ctrl+Alt+G to show or hide this window".
* **Target Solution:** Show it for the first few sessions only, or move it to the tray icon tooltip.

#### 3.4 Add Undo to Destructive Settings Actions

* **Current State:** Settings are "Saved in real time". Removing a scan location or an ignored game takes effect at once, and the only recovery is resetting all settings.
* **Target Solution:** Reuse the Library's undo toast for those removals. Checkboxes need no undo.

#### 3.5 Honour the Windows Reduce-Motion Setting

* **Current State:** About 35 storyboards in `MainWindow.xaml` and `SystemView.xaml`; nothing checks `SystemParameters.ClientAreaAnimation`.
* **Target Solution:** Gate the hover, scale and fade animations on that setting.

#### 3.6 Make the Picker Dialogs Resizable

* **Current State:** `GameCandidatePickerDialog` (520 × 460) and `GameMatchPickerDialog` (540 × 480) are `NoResize` but list long paths.
* **Target Solution:** Allow resizing with a minimum size, as Scan for Games already does.

---

### Phase 4: Polish (P3)

#### 4.1 Access Keys

* No dialog has an `_` mnemonic. Add Alt shortcuts to the footer buttons (Save, Cancel, Add Selected) at least.

#### 4.2 Consistent Value Pills on Hardware Specs

* In `Assets/screenshots/system-hardware.png`, "15.9 GB VRAM" and "240 Hz" are bordered pills in green and blue, while "Load: 8%", "Used: 57%" and "Ping: 8 ms" are styled differently. Pick one treatment for live values and one for static values.
* "3.20 GHz Current, 3.2 GHz Max" mixes decimal precision.

#### 4.3 Drive-Usage Colour

* Drive bars are all one blue. Turn a bar amber or red above roughly 90% full.

#### 4.4 Share One Card Template

* The three grid views are near-duplicate card templates of about 500 lines each inside a 4,493-line `MainWindow.xaml`. Extract `LibraryView`, `SettingsView` and `AboutView` as Review 1 proposes, and share one card template with a size parameter. When a badge or label changes, the three copies currently have to be kept in step by hand.

#### 4.5 Still Open from the 1.3.7 Review

* Game Details has no End Session or Force Close while a game is playing (GD-4).
* In the grouped tray menu, a category literally named "Recent" or "Favorites" disappears (TR-5).

---

### Suggested Implementation Order (Claude)

1. **Global quick wins:** Muted-text contrast and size floor, dark tooltip style, Enter/Esc in dialogs, focus-ring audit.
2. **Assistive technology and performance:** Automation names, high-contrast support, grid virtualization with search debounce.
3. **Density, recovery and motion:** Library header, split Add button, hotkey hint, Settings undo, reduce-motion, resizable pickers.
4. **Polish:** Access keys, pill consistency, drive-bar colour, shared card template, the two open 1.3.7 items.

### Review Boundaries & Existing Strengths

- Preserve the dark theme, artwork-led library, searchable settings, recorded hotkey box, launch popup and removal undo.
- Dark-only is acceptable for a gaming tool; a light theme is not recommended now.
- Review 1's memory, frame-rate and startup figures were not measured or validated here.
- Not evaluated: tray flyout, WindowChrome and Snap Layouts, Mica, Segoe UI Variable, Edit Game tabs, SettingsCards, onboarding wizard, gamepad navigation.
- A comment in `App.xaml` records that a Mica Alt spike was already tried, and that a flat #202020 sidebar was chosen over the transparency plumbing. Weigh that before adopting Review 1, section 4.2.
- No benchmark, live interaction audit or application-code change was performed for this review.

---

# Review 3: OpenAI / Codex (.NET UI/UX Review)

### Reviewer Profile

- **Model:** OpenAI / Codex
- **Review Date:** September 19, 2026
- **Focus:** Readability, discoverability, game configuration, performance-state clarity, onboarding, recovery, and accessible layouts.
- **Method:** Read-only review of WPF XAML, supporting view models, onboarding, tray-menu code, and bundled screenshots. The application was not run; runtime performance and assistive technology support were not tested.
- **Status:** Recommendations for consideration. No application changes implemented.

---

### Executive Summary

TrayTrigger has a strong visual foundation: a consistent dark theme, artwork-led library, searchable settings, keyboard shortcuts, launch feedback, and removal undo. Preserve this identity while making everyday tasks easier to understand and complete.

The highest-value improvements are brighter, larger secondary text and more precise performance-control wording. Next, reorganize Edit Game, expose selection actions, simplify onboarding, and strengthen recovery. Responsive layouts and accessibility validation should accompany these changes.

These findings are independent of Review 1. Its memory, frame-rate, and startup claims were not measured or validated in this review. Priority labels below indicate proposed sequencing, not approved implementation decisions. Blank comparison cells indicate topics not evaluated in this review.

---

### Strategic Priority Matrix (OpenAI / Codex)

| Priority | Milestone | Focus Area | Key Deliverable | Expected Impact |
| :---: | :--- | :--- | :--- | :--- |
| **P1** | **Phase 1** | Readability | Brighter secondary text and a clearer type scale | Makes instructions and metadata easier to read |
| **P1** | **Phase 1** | Performance Clarity | Accurate state, restoration, and restore-point labels | Reduces uncertainty about Windows changes |
| **P1** | **Phase 2** | Dialog Workflows | Tabbed Edit Game with advanced launch options | Makes hotkeys, profiles, and scripts easier to find |
| **P1** | **Phase 2** | Discoverability | Selection toolbar and labeled navigation | Exposes existing capabilities without memorized gestures |
| **P2** | **Phase 3** | First Run | Shorter welcome and explicit next actions | Helps users reach their first launch sooner |
| **P2** | **Phase 3** | Recovery | Longer undo opportunity and revisitable launch failures | Gives users time and context to recover |
| **P1** | **Across Phases** | Accessible Layouts | Narrow-window, text-scaling, keyboard, and Narrator checks | Keeps improvements usable across display settings |

---

### Phase 1: Readability & Performance Clarity (P1)

#### 1.1 Increase Secondary-Text Contrast and Size

* **Current State:** `App.xaml` defines muted text as `#6E6E7A`. Descriptions, timestamps, and instructions frequently use WPF FontSize values of 9–11 device-independent pixels (DIPs).
* **Problem:** Calculated contrast is approximately **3.37:1** against the `#1C1C23` card surface, **3.33:1** against the `#1D1D25` dialog background, and **3.72:1** against `#121214`. These fall below Microsoft's 4.5:1 guidance for normal text. Several affected labels convey useful information rather than decoration.
* **Target Solution:**
  - Brighten secondary and muted text, validating each actual foreground/background pairing.
  - Aim for 13–14 DIPs for routine descriptions; reserve smaller type for nonessential metadata.
  - Apply shared typography resources consistently across settings and game-edit screens.
* **Validation:** Check normal text against the 4.5:1 target, including text over artwork. Review wrapping and clipping after increasing type sizes.
* **Reference:** [Microsoft: Accessible text requirements](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessible-text-requirements).

#### 1.2 Use Precise Performance-State and Restoration Labels

* **Current State:** `Views/SystemView.xaml` and `ViewModels/SystemViewModel.cs` expose OPTIMAL, STANDARD, Optimize, Reset Defaults, and RESTORE POINT: ON. The reset confirmation describes restoring values from before TrayTrigger changed them.
* **Problem:** These labels mix configuration state, expected performance benefit, and restoration behavior. The restore-point badge reflects a preference, not proof that creation succeeded.
* **Target Solution:**

  | Current Wording | Suggested Wording |
  | :--- | :--- |
  | OPTIMAL | Applied, or the actual setting value |
  | STANDARD | Not applied, or the actual setting value |
  | Optimize | Apply |
  | Reset Defaults | Restore previous settings |
  | RESTORE POINT: ON | Create restore point before changes: On |

  - Show restore-point creation success or failure separately from the enabled preference.
  - Keep buttons, confirmations, and completion messages consistent with actual restoration behavior.
* **Validation:** Review already-configured settings, cancelled elevation, failed restore-point creation, and restoration states. Avoid claiming TrayTrigger applied a setting solely because its current value matches a recommendation.

#### 1.3 Distinguish Session Profiles from Persistent Windows Changes

* **Current State:** The System view already says system-wide tweaks stay on until reset. Game profiles restore settings when a session ends, while the editor primarily presents profile tiers and help links.
* **Problem:** The scope distinction is present but easy to overlook, and a tier name does not explain the changes that will actually run for a specific game.
* **Target Solution:**
  - Use prominent, consistent descriptions: **Game profiles — restored when the game ends** and **Windows settings — stay applied until restored**.
  - Add a compact summary of the selected profile's effective changes in Edit Game, accounting for enabled options and applicable capabilities.
  - Keep custom script cleanup distinct from automatic profile restoration.
* **Validation:** A user should be able to explain which changes persist and what the selected game profile will do without opening a help page.

---

### Phase 2: Game Editing & Library Discoverability (P1)

#### 2.1 Reorganize Edit Game into Four Sections

* **Current State:** `Views/GameEditDialog.xaml` reserves a fixed 220-DIP column for artwork, with configuration cards in a scrolling column and Save/Cancel in a fixed footer.
* **Problem:** Artwork occupies space even when editing a hotkey or script; launch, performance, and automation settings compete in a long form.
* **Target Solution:**
  - Introduce **General**, **Launch**, **Performance**, and **Scripts** tabs.
  - Move artwork editing into General; retain a small thumbnail and game name above the tabs.
  - Retain the fixed Save/Cancel footer.
  - Put working directory and command-line parameters in an **Advanced launch options** expander.
  - Show a concise routing summary such as **Launches through Steam** above detailed controls.
* **Validation:** Verify that changing tabs preserves unsaved edits and validation messages, and that hotkeys and script settings can be reached directly.

#### 2.2 Expose Selection Actions and Navigation Labels

* **Current State:** Batch operations are available through modifier-click selection and a context menu. `Models/AppSettings.cs` defaults the sidebar to collapsed icons.
* **Problem:** Useful existing actions require users to know gestures or recognize destination icons.
* **Target Solution:**
  - Show a contextual toolbar when games are selected: **3 selected · Favorite · Category · Hide · More · Clear selection**.
  - Start with labeled navigation when width permits, then remember the user's preference.
  - Preserve context menus and existing shortcuts as faster alternatives.
* **Validation:** Check selection counts and action scope after search or filter changes. Ensure the toolbar is reachable by keyboard and does not unnecessarily displace focus.

---

### Phase 3: Onboarding & Recovery (P2)

#### 3.1 Shorten the Welcome Experience

* **Current State:** `Views/WelcomeDialog.xaml` contains seven explanatory bullets. The empty library already offers scanning and drag-and-drop guidance.
* **Problem:** The welcome asks users to absorb advanced features before their first launch, and Get Started does not clearly describe skipping the scan.
* **Target Solution:**
  - Lead with **Add your installed games, then launch them from the tray.**
  - Reduce introductory points to scan, launch, and optionally configure profiles.
  - Keep **Scan for Games** primary and rename **Get Started** to **Skip for now**.
  - Explicitly state whether profiles are enabled by default; avoid wording that implies every game automatically receives tweaks.
* **Validation:** Walk through both scan and skip paths with a new library; confirm the next step remains obvious.

#### 3.2 Give Users More Time and Context to Recover

* **Current State:** `ViewModels/LibraryViewModel.cs` finalizes pending removal after a six-second undo window. Launch feedback is available through existing toast and popup flows.
* **Problem:** Recovery depends on noticing transient messages quickly.
* **Target Solution:**
  - Extend the removal undo opportunity and pause expiration while the message is being interacted with.
  - Consider a session-level undo history, defining which removed metadata and cached assets must remain recoverable.
  - Keep removal wording explicit that installed game files are left intact.
  - Preserve launch failures in a revisitable location with relevant actions such as **Locate executable**, **Open launcher**, or **View details**.
* **Validation:** Exercise consecutive removals, keyboard-only undo, dismissal, and application exit. Check that recovery actions fit each failure rather than presenting irrelevant generic options.

---

### Cross-Cutting: Responsive Layout & Accessibility (P1)

#### 4.1 Adapt Layouts to Narrow Windows and Enlarged Text

* **Current State:** The main window permits a 720-DIP minimum width; Edit Game requires 760 DIPs and retains its fixed artwork column. The library toolbar includes categories, three import actions, search, sorting, filters, and four view buttons.
* **Problem:** Fixed allocations and dense toolbars leave limited room when the window is narrow or text is enlarged. This is a source-based layout concern, not a reproduced runtime defect.
* **Target Solution:**
  - Collapse import actions into an **Add games** menu at smaller widths.
  - Move view selection into a labeled dropdown when needed.
  - Stack or adapt editor content instead of retaining a wide artwork column.
  - Preserve existing keyboard-focus handling and accessible names while checking complete flows with Narrator.
* **Validation:** Review 125%, 150%, and 200% display scaling, increased Windows text size, minimum window sizes, keyboard navigation, and high-contrast settings. Confirm that primary actions and validation messages remain reachable.

---

### Suggested Implementation Order (OpenAI / Codex)

1. **Readability and wording:** Improve text contrast and sizing; align performance, restoration, and restore-point labels with behavior.
2. **Daily workflows:** Reorganize Edit Game and expose batch selection actions and navigation labels.
3. **First run and recovery:** Simplify the welcome, extend undo, and retain actionable launch failures.
4. **Validate throughout:** Check narrow layouts, display and text scaling, keyboard operation, and Narrator during each phase.

### Review Boundaries & Existing Strengths

- Preserve the dark theme and artwork-led library.
- Retain searchable settings, keyboard shortcuts, launch feedback, removal undo, and existing empty-library scan guidance.
- The current tray code already excludes Favorites from Recent; duplicate entries visible in a bundled screenshot are not treated as a current defect.
- No performance benchmark, live interaction audit, or application-code change was performed for this review.
- All priorities and proposed wording remain recommendations pending the owner's implementation decisions.

---

# Consolidated Consensus & Final Implementation Plan

Once all model reviews are inserted:
1. Compare recommendations in the [Master Comparison & Decision Matrix](#master-comparison--decision-matrix).
2. Mark agreed-upon items with high ROI as **Selected for Implementation**.
3. Group selected items into development tasks.
