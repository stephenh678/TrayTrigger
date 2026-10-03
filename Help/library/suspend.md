# Suspend and resume a game

Suspend freezes a game you launched from TrayTrigger exactly where it is - mid-cutscene, mid-fight, on a loading screen - and resume carries on from the same frame. For a cutscene you can't pause, or stepping away from a game that has no pause.

## How to use it

- **The hotkey**, Ctrl+Alt+P unless you change it in Settings > General: press it in the game to suspend it, and again to resume it. With several games running it suspends the one in front.
- **The tray menu**: the game's row under Now Playing has Suspend Game, or Resume Game while it's suspended. A suspended game shows "suspended" there instead of its playtime.
- **The library**: right-click the game and choose Suspend Game or Resume Game. Its PLAYING tag reads PAUSED while it's suspended.
- **Play** on a suspended game resumes it and brings it to the front.

## What happens

- The game is minimized to the taskbar first, so you're back at the desktop. A full-screen game gets a few seconds to hand the screen back; one that doesn't minimize by then is minimized by Windows once it's frozen.
- Every process of the game is frozen, along with every process it started.
- Its sound is muted, so older games don't repeat the last fraction of a second as a buzz. Resume unmutes it. A game you had muted yourself stays muted.
- Its playtime clock stops, so the time it spends suspended isn't added to its playtime.
- Resume restores the game's window and brings it back to the front. Close Game, Exit TrayTrigger and the other ways a suspended game is resumed leave it minimized; click it in the taskbar.

## Limits

- Online games lose their connection while suspended: the server stops hearing from them.
- A game protected by anti-cheat - Easy Anti-Cheat, BattlEye, EA Javelin, nProtect GameGuard, XIGNCODE3 and the like - is never suspended. A game that stops answering its anti-cheat is disconnected at best, and can be flagged. TrayTrigger looks for the anti-cheat in the game's folder, and for an anti-cheat service started with the game.
- While a game is suspended, clicking it in the taskbar won't bring it back - it can't draw itself, and Windows may call it Not Responding. Resume it with the hotkey or the tray menu instead. Close Game resumes it first, then asks it to quit.
- A game running as administrator can only be suspended when TrayTrigger runs as administrator too.
- Some games can crash on resume after being suspended a long time. Save first if it matters.
- A full-screen exclusive game that is slow to give the screen back can leave it black or at its resolution for a moment. Borderless or windowed works best.

## Nothing is left frozen

Closing a game's session, Close Game, Force Close and exiting TrayTrigger all resume a suspended game first. If TrayTrigger stops without exiting - a crash, or ended from Task Manager - it resumes the game the next time it starts.
