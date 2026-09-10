# Disable Sticky, Filter, and Toggle Keys Shortcuts

## What it changes

Turns off the keyboard shortcuts that switch on three accessibility features: Sticky Keys (tap Shift five times), Filter Keys (hold Right Shift for eight seconds), and Toggle Keys (hold Num Lock for five seconds). The features themselves stay available from Settings, Accessibility, Keyboard.

## Why it helps

- Five Shift taps in a shooter opens the Sticky Keys prompt over the game and steals focus.
- Holding Shift to sprint for eight seconds turns on Filter Keys, which then starts ignoring short key presses.
- Both are on by default on every Windows install and are among the most common "my game just alt-tabbed" causes.

## Trade-offs

- If you rely on any of these shortcuts to turn a feature on quickly, keep this off. The features can still be enabled from Settings.
- No restart and no administrator rights needed. Takes effect immediately.

## Details

- Sets Flags to 506 under HKEY_CURRENT_USER\Control Panel\Accessibility\StickyKeys, 122 under Keyboard Response (Filter Keys), and 58 under ToggleKeys. Windows defaults are 510, 126, and 62; the difference is the "hotkey active" bit.
- Same as unticking the "Allow the shortcut key to start ..." options under Settings, Accessibility, Keyboard.
