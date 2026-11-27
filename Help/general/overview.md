# The window, the tray icon and hotkeys

TrayTrigger lives in the system tray. The window is for setting things up; day to day, the tray icon and hotkeys are all you need. These options are under Settings > General, apart from the three about launching a game, which are under Settings > Launching > Launching Games.

## Closing and exiting

- The window's X hides it to the tray. TrayTrigger keeps running, so your hotkeys and the tray menu still work. The first time, a notice by the tray says so.
- Exit in the sidebar asks whether to minimize or really exit.
- Exit TrayTrigger at the bottom of the tray menu quits straight away.
- Exiting while a game is running puts its Performance Profile back and records its playtime first.

## Starting

- **Start with Windows** starts TrayTrigger when you sign in.
- **Start minimized to tray**, under it, starts it as a tray icon with no window when it starts with Windows.

## Launching a game

These three are under Settings > Launching > Launching Games.

- **Minimize to system tray when launching a game** hides the window as a game starts, so it never covers the game.
- **Keep game launchers minimized when launching a game** asks Steam, GOG Galaxy and Epic to start quietly, so only the game opens. See How a game is launched.
- **Show a launch popup by the tray icon** shows a small popup by the clock when a game is started from the tray or a hotkey: what it is waiting for, and the reason if the launch fails. See How the tray menu works.

## Moving around with the keyboard

Tab moves from one group of controls to the next, and the arrow keys move inside a group, as in File Explorer and Windows Settings:

- **The sidebar** is one Tab stop. Tab lands on the page you're on; Up and Down move to the others, and Enter or Space opens one.
- **Each row of tabs** (the Library's categories, its collections, the view buttons, the tabs at the top of System, Settings, About and Activity, and in Game Details) is one Tab stop. Tab lands on the tab that's selected; Left and Right move along the row, and Enter or Space picks one. Ctrl+Tab still changes tabs where it did.
- **The games** in the Library are one Tab stop. Tab lands on the game you were last on, or the first.
- **Game Details' links and news** are one Tab stop each; the arrow keys move between them.

Some things you click can't be reached with Tab or the arrow keys, because the keyboard has another way to them:

- **Favorite stars** - the game's menu (the menu key, or Shift+F10) has Add to Favorites and Remove from Favorites.
- **Play buttons on Details List rows** - Ctrl+Enter plays the row.
- **Details List column headers** - the sort box above the library sorts the same way.
- **Game Details' "Edit performance" and "Edit launch"** - the Settings tab beside Overview opens the same settings.
- **Help links** ("Learn more", "How ... works") - About > Help Topics opens every Help page.

They still work with a click, and a screen reader can still find and press them.

## Using the Library from the keyboard

In the Poster Grid, Extra Large and Compact Icons views, Tab to the games, then:

- **Arrow keys** move to the next game, as in File Explorer. Left and Right carry on to the row above or below; Down from a row with nothing under it goes to the last game.
- **Home** and **End** go to the first and last game, and **Page Up** and **Page Down** move a screenful of rows.
- **Enter** opens Game Details, and **Ctrl+Enter** plays the game.
- **The menu key** (or Shift+F10) opens the game's right-click menu, or the batch menu when the game is one of several selected.
- **Tab** moves on past the games, to the next group.

The Details List is a list: Up and Down move a row, and Enter and Ctrl+Enter work the same.

## Using a game controller

Turn on **Use a game controller in the Library** in Settings > Library & Art > Library View. An Xbox controller, a PlayStation controller (DualShock 4, DualSense, DualSense Edge; wired or Bluetooth), or a handheld's built-in controls (ROG Ally, Legion Go and the like), then moves around the Library and Game Details, and the windows they open:

- **D-pad or left stick** - move between games, in any view. Hold it to keep moving. The first press, when no game is picked, goes to the game you were last on, or the first.
- **A** - open the game's Game Details. In Game Details, or on a button, it presses the button that is outlined.
- **B** - back: closes Game Details, a menu or a prompt; in the Library it clears the selection, then the search.
- **X** - play the game that is outlined.
- **Menu** (the button with three lines) - the game's right-click menu. Move through it with the D-pad, A to choose, B to close.
- **LB and RB** - the previous and next category tab.

**Up from the top row of games** goes to the bar above them: Filters, Sort, the view buttons, the search box and the category tabs. There the D-pad moves to the next control that way, and A presses a button or opens Sort; Down from the bar, or B, goes back to the games. Up again goes back to the control you used last. Holding Up to scroll to the top of your games stops on the top row; press Up again to go on into the bar. Add Game, Add Folder and Scan for Games aren't reached from the controller: they open Windows' own file pickers, which a controller can't use.

- **Filters** - A opens the list. The D-pad moves through it, A ticks a filter (press again to exclude, again to clear), and B closes it.
- **Sort** - A opens it. The D-pad moves through the orders, A chooses one, and B closes it as it was.
- **The search box** can be reached, but typing in it needs a keyboard, or Windows' touch keyboard on a handheld.

On a PlayStation controller, Cross is A, Circle is B, Square is X, Options is Menu, and L1 and R1 are LB and RB.

Game Details opens with Play selected, so A on a game and A again plays it. There the D-pad moves to the next button in that direction, and A presses it: Play, the tabs, the ... menu. On a drop-down, such as Performance Profile, A opens it, the D-pad moves through it, A chooses and B closes it as it was. Typing in its Settings tab still needs a keyboard, or Windows' touch keyboard on a handheld, and the controller passes over its Browse..., Choose image and New script buttons (they open Windows' own file pickers, which a controller can't use) and its hotkey box (it would record the controller's moves as the hotkey). When there is more of the page below or above, Up and Down scroll it until the next control is on screen, so text with nothing to press is read on the way.

What it doesn't do:

- **Nothing is read while a game, or any other program, is in front.** The controller is read only while the Library, Game Details or a window they opened (Change Category, Collections, a prompt) is the window in front, so a press in a game never reaches TrayTrigger. A button already held when TrayTrigger comes to the front does nothing until it is let go.
- **It only moves around TrayTrigger.** It sends nothing to Windows or to other programs, and it doesn't stop Steam, Game Bar or a handheld's own software from seeing the controller too. Nothing is sent to a PlayStation controller either: its lights, rumble and mode are left as they are.
- **The sidebar, other pages** (Tools, Activity, System, Settings, About) **and the tray menu** still need a mouse, touch or the keyboard. The D-pad never leaves the Library for the sidebar.
- **Handhelds in a mouse mode.** A handheld set to make its controls work as a mouse and keyboard (a "desktop mode") may not appear to Windows as a controller, and then this setting has nothing to read; in that mode the D-pad may already work as the arrow keys above. Switch the handheld to its controller (gamepad) mode to use this setting.

## Full-screen Library

For a TV or a handheld: the window fills the screen and shows only your games, as Extra Large posters with bigger text, and the controller works whether or not its setting is on. Turn it on from:

- **The tray menu** - Full-Screen Library.
- **The Library** - the full-screen button at the right end of the bar above your games (it also takes you back).
- **F11**, on any page.
- **The controller's View button** (the two squares; Create on a PlayStation controller), in the Library, when Use a game controller in the Library is on.
- **A shortcut to TrayTrigger.exe with `--couch`** - starts TrayTrigger in it, or, when TrayTrigger is already running in the tray, switches it to it.

Game Details opens over it as a card, with the Library dimmed behind it and no window title bar; Close, Esc or the controller's B closes it. When a game you start from it exits, TrayTrigger comes back full screen, so the TV never shows the desktop. Leave it with F11, View, the same button, or by going to another page: the window comes back as it was, with your own view and text size (neither is changed while it's on).

What it doesn't do: the sidebar, Add Game and the other pages stay desktop things, and there is no on-screen keyboard (typing in the search box needs a keyboard, or Windows' touch keyboard on a handheld).

## Search and filter

Three ways to find something, named for what they do. A **filter** (Library, Tools and Activity) narrows the list you are looking at to what matches. A **page search** (Settings, About and System) finds a card on any tab of that page and takes you to it. **Search everything** looks in all of them at once. Ctrl+F puts the cursor in the filter or search box of the page you are on, Ctrl+K opens Search everything, and Esc clears or closes either.

### Search everything

The magnifier at the bottom of the left bar, above Activity, or **Ctrl+K** from any page, opens a search box over the window that looks in every place at once. Results are grouped: Games, Tools, then the cards of Settings, System and About, and one line for Activity & History when its entries match.

- Up and Down pick a result and Enter or a click opens it. A game opens its Game Details; a tool is selected on the Tools page; a setting, system or About card is shown on its page and tab, outlined, with your words marked; the Activity line opens Activity & History with the filter set.
- Each group shows the best few, so a settings card never pushes a game out of sight. When a game search finds more than five, the last line, "Show all 8 games matching ...", opens the Library with that word in its filter box.
- Every word you type must be in the game's or tool's name or category, or in the card. Hidden games and tools you have turned off are not listed.
- Esc, a click outside the box, or Ctrl+K again closes it. Ctrl+K does nothing while a hotkey is being recorded.

### Searching the pages

Settings, About and System each have a search box beside their tabs. Type a word or two and a list drops down with every matching card from every tab, not only the one you are on. Each result shows the card's name, the tab it is on, and the line that matched.

- Up and Down pick a result, and Enter or a click takes you to it: TrayTrigger switches to its tab, scrolls to the card (or to the row inside it, such as one tweak among several) and outlines it, with the words you typed marked in yellow.
- The page itself does not change while you type, and the outline stays until you search for something else or clear the box.
- Every word you type must appear in the card (its title, description, option names or tooltip) or in the tab's name. Whatever you have typed into a box, such as an API key, is never searched.
- The list searches the page you are on. When it finds nothing it says so and points to Search everything (Ctrl+K), which looks in every page.
- A setting your PC can't use (an NVIDIA option without an NVIDIA card, the monitor options with one monitor) is not listed.
- Press Enter in the box again to bring the list back after choosing. Esc, or the x in the box, clears the search.
- Leaving the page by the sidebar clears the search, puts the page back on its first tab and scrolls it to the top, so it opens fresh next time. The Library, Tools and Activity do the same for their filter box (the category tab, sort and view you chose stay).

## Window sizes

Game Details (which holds Edit Game on its Settings tab), Edit Tool, Scan for Games, the folder import and Edit Theme open at the size you last left them. A window you never resized keeps its normal size, and a saved size that is too big for the screen is ignored.

## The tray icon

- Left-click shows or hides the window, and right-click opens the menu. With **Left-click on the tray icon opens the game menu** on, both clicks open the menu. Double-click always opens the window.
- Pointing at the icon shows what is playing, or the hotkey that shows the window when nothing is.
- Windows may tuck the icon into the hidden overflow. **Always show icon in tray** asks Windows 11 to keep it on the taskbar; Windows decides. Dragging the icon from the overflow onto the taskbar always works.

## TrayTrigger's own hotkeys

Each works from anywhere in Windows, with the window open or hidden.

- **Show / Hide Window**, Ctrl+Alt+G unless you change it.
- **Open Tray Menu**, Ctrl+Alt+T: the tray menu by the tray icon, ready to type a search.
- **Suspend / Resume Game**, Ctrl+Alt+P: freezes the game you're playing, and resumes it. See Suspend and resume a game.
- **Launch Last Played Game**, Ctrl+Alt+L: plays the game you played most recently, the same as its Play button, or brings it forward if it's already running.

Games and tools can each have a launch hotkey of their own, set in Edit Game and Edit Tool.

## Setting a hotkey

Click the box and press the keys. The combination is recorded from what you press, not typed.

- Ctrl, Alt or Shift plus a key, or a function key on its own. Win+ anything is reserved by Windows.
- A combination already in use, by a game, a tool, another TrayTrigger hotkey or another program, is refused with the reason.
- Escape keeps the old value; Backspace, Delete or the x clears it. A cleared hotkey is simply off.
- If a game or tool already has the combination, it keeps it and the tray menu, suspend or launch last played hotkey gives way until you choose another.
