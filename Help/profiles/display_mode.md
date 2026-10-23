# Display and sound (per game)

## What it does

Edit Game > Performance has a **Display and sound** card for each game. It holds everything about the screens and speakers a game runs on, and each part shows only when it can do something on your PC:

- **Resolution and refresh rate** for a monitor (always, when your display lists modes).
- **Monitor**, **Identify**, **Make this monitor the main display** and **Dim the other screens** (with more than one monitor).
- **Keep the mouse on the game's screen** (with more than one monitor).
- **Sound output** (with more than one playback device).

Everything is off until you choose it, set just before the game starts, and put back when it ends.

## Resolution and refresh rate

The resolution and refresh rate the monitor runs at while the game does. It's for a game that has no refresh rate or resolution setting of its own, or a monitor that starts a game at the wrong rate.

There are two lists. **Resolution** shows the resolutions the monitor offers, the largest first; **Don't change the display** is how every game starts out. Once you pick a resolution, **Refresh rate** shows only the rates the monitor offers at that resolution, the fastest first. To change only the refresh rate, pick the current resolution and the rate you want.

## Monitor

With more than one monitor attached, the **Monitor** list picks which one the resolution, refresh rate and main display choice apply to. **Main display** (the default) means whichever monitor is the main one when the game starts. **Identify** puts a large number on every monitor for a moment, the same number Windows' own Identify shows, with the monitor's name under it.

A game remembers its monitor by the monitor's own identity, not by the number Windows gives it, which can change when cables are swapped. If that monitor isn't connected when the game starts, the display is left alone and Activity & History says so.

## Make this monitor the main display

For a game that opens on the main display: with a monitor other than the main one chosen, tick **Make this monitor the main display while the game runs**. The taskbar, open windows and desktop icons move to it, and back when the game ends.

This one changes Windows' own display layout, which has no temporary form, so it is treated with more care than the rest:

- It is off unless you tick it, and hidden when the chosen monitor is already the main one.
- A note of where every monitor sat is written before anything changes. The layout is put back when the game ends, when TrayTrigger closes, and at the next start if TrayTrigger stopped during the game.
- It is put back only if that monitor is still the main display and every monitor that was there still is. If you changed the layout yourself meanwhile, or a monitor was unplugged, it is left alone.
- If Windows refuses, or can't put the layout back, Activity & History says so, and TrayTrigger tries again at the next start. Windows Settings > System > Display sets it by hand.
- Windows moves open windows when the main display changes, and doesn't always put them back where they were.

## Keep the mouse on the game's screen

It's for a game in a window or borderless full screen, where the mouse can slide onto another monitor and a click there pulls the game out of focus.

- While the game's window is in front, the mouse can't leave the monitor that window is on.
- Anything else that takes the focus frees it at once: Alt+Tab, the Windows key, a prompt, another program. When the game is in front again, it holds again. So you can always get the mouse out.
- It is freed when the game ends, when TrayTrigger closes, and by Windows itself if TrayTrigger stops.
- A game that confines the mouse itself, for a camera, is left alone: TrayTrigger only holds the mouse to the whole screen, never tighter, and never lifts a hold it didn't set.
- A game running as administrator may need TrayTrigger run as administrator too.

## Dim the other screens

While the game's window is in front, every other monitor is dimmed to about a quarter of its brightness, so a second screen doesn't compete with the game. It's a see-through black layer over those monitors that can't take the focus or the mouse, so clicks go through to what's under it.

- Alt+Tab, the Windows key or anything else that takes the focus lifts it at once, and it is lifted when the game ends or TrayTrigger closes. Since the layer belongs to TrayTrigger, it goes if TrayTrigger stops.
- On a monitor running HDR the dimming can look greyer than on a standard one, because the layer is drawn in standard colours.

## Sound output

The playback device the game's sound plays through while it runs, put back when it ends: speakers, headphones, a monitor's speakers.

- It moves the device used for games and for music and video. Voice chat keeps the device it had, so a headset stays on the headset.
- A device's job is put back only while it still has the job TrayTrigger gave it: a device you picked meanwhile is left.
- If the old device isn't connected when the game ends, nothing is changed.
- Windows has no public way to do this, so TrayTrigger uses the call Windows' own sound settings use. A Windows update could change it; if so, Activity & History says the sound output couldn't be set, and the game starts as usual.
- A sound output chosen for this game in Windows' volume mixer (Settings > System > Sound > Volume mixer) overrides it.

## What the settings do not do

- The resolution and refresh rate set a mode, not HDR (see HDR), colour depth or scaling.
- They never ask for a mode the monitor doesn't list. A mode Windows would refuse is skipped, the game starts as usual, and Activity & History says why.
- One game at a time holds the display and the sound. If a second game with its own settings starts while the first is running, the second leaves them alone and the first puts them back.
- Nothing here works in a remote desktop session.

## Putting it back

- The resolution and refresh rate come back when the game ends, when TrayTrigger closes, and at the next start if TrayTrigger stopped during the game. TrayTrigger asks Windows for the desktop's own mode, the one set in Windows Settings; it doesn't re-apply a remembered one, so a change you made in Windows in between isn't undone. The mode is temporary, never saved as the desktop's own setting, so a restart brings your normal mode back, and Windows also undoes it when TrayTrigger stops.
- The main display, the sound output and the mode each have a short note kept until they are put back, and each is put back only if it is still as TrayTrigger left it.
- If something can't be put back, Activity & History says so and TrayTrigger tries again at the next start.

## Things to know

- TrayTrigger asks Windows to treat a resolution change as temporary, which is meant to keep it from rearranging open windows and desktop icons. A different resolution can still disturb them on some PCs; a different refresh rate at the same resolution doesn't change the layout.
- A game that sets its own resolution, such as one in exclusive full screen, overrides this while it runs. TrayTrigger still puts the display back when the game ends.
- Changes to these settings, and each time they are set and put back, are recorded in Activity & History.
- The monitor, main display, dimming and mouse parts have been tried on a PC with one monitor only. They are built to do nothing there, and to be careful with two or more.
