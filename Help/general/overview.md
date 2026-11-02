# The window, the tray icon and hotkeys

TrayTrigger lives in the system tray. The window is for setting things up; day to day, the tray icon and hotkeys are all you need. These options are under Settings > General, apart from the three about launching a game, which are under Settings > Launch & Performance > Launching Games.

## Closing and exiting

- The window's X hides it to the tray. TrayTrigger keeps running, so your hotkeys and the tray menu still work. The first time, a notice by the tray says so.
- Exit in the sidebar asks whether to minimize or really exit.
- Exit TrayTrigger at the bottom of the tray menu quits straight away.
- Exiting while a game is running puts its Performance Profile back and records its playtime first.

## Starting

- **Start with Windows** starts TrayTrigger when you sign in.
- **Start minimized to tray**, under it, starts it as a tray icon with no window when it starts with Windows.

## Launching a game

These three are under Settings > Launch & Performance > Launching Games.

- **Minimize to system tray when launching a game** hides the window as a game starts, so it never covers the game.
- **Keep game launchers minimized when launching a game** asks Steam, GOG Galaxy and Epic to start quietly, so only the game opens. See How a game is launched.
- **Show a launch popup by the tray icon** shows a small popup by the clock when a game is started from the tray or a hotkey: what it is waiting for, and the reason if the launch fails. See How the tray menu works.

## Search and filter

Three ways to find something, named for what they do. A **filter** (Library, Tools and Activity) narrows the list you are looking at to what matches. A **page search** (Settings, About and System) finds a card on any tab of that page and takes you to it. **Search everything** looks in all of them at once. Ctrl+F puts the cursor in the filter or search box of the page you are on, Ctrl+K opens Search everything, and Esc clears or closes either.

### Search everything

The magnifier at the top of the left bar, or **Ctrl+K** from any page, opens a search box over the window that looks in every place at once. Results are grouped: Games, Tools, then the cards of Settings, System and About, and one line for Activity & History when its entries match.

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
- Leaving the page by the sidebar clears the search, puts the page back on its All tab and scrolls it to the top, so it opens fresh next time. The Library, Tools and Activity do the same for their filter box (the category tab, sort and view you chose stay).

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

Games and tools can each have a launch hotkey of their own, set in Edit Game and Edit Tool.

## Setting a hotkey

Click the box and press the keys. The combination is recorded from what you press, not typed.

- Ctrl, Alt or Shift plus a key, or a function key on its own. Win+ anything is reserved by Windows.
- A combination already in use, by a game, a tool, another TrayTrigger hotkey or another program, is refused with the reason.
- Escape keeps the old value; Backspace, Delete or the x clears it. A cleared hotkey is simply off.
- If a game or tool already has the combination, it keeps it and the tray menu or suspend hotkey gives way until you choose another.
