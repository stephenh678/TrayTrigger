# Stutter Check

## What it does

Runs the checks every "my game stutters" thread tells you to go through, in one click, and sorts the results into what needs a look, what TrayTrigger can fix, and what's fine. It only reports: nothing changes until you press a button, and nothing it finds in another app is ever edited or closed.

## What it checks

- **Overlay apps running** - Discord, the NVIDIA App overlay, RivaTuner (RTSS), Medal, Overwolf and the Xbox Game Bar. One is normal; two or more at once is the most common cause of stutter in the fix threads.
- **Browser and chat apps holding video memory** - Chrome, Edge, Brave and Discord with hardware acceleration on, and how much GPU memory they hold right now.
- **Display refresh rate** - a monitor running below the fastest rate it offers at its resolution.
- **Memory speed** - RAM running below its rated speed, which means XMP or EXPO is off in the BIOS.
- **Games on a hard drive** - library games installed on an HDD rather than an SSD.
- **Drive space** - a drive that holds games or Windows with under 10 % (or 20 GB) free. Shader caches, the pagefile and patches all need room.
- **Shader cache** - the NVIDIA driver's cache capped, or Windows cleanups set to delete the DirectX shader cache. A game recompiling shaders as you play is the most-cited cause of stutter in the fix threads. Fix it applies the tweak.
- **PCIe lanes** - a graphics card on fewer lanes than it supports.
- **Resizable BAR** - off on a card that can use it.
- **Hardware-Accelerated GPU Scheduling, Game Bar recording, Game Mode** - the System tweaks that matter for stutter. Fix it applies the tweak, which Restore Previous can undo. Game Mode is the contested one: Microsoft recommends it on, and some threads blame it for hitching in particular games, so try it the other way for a game that still stutters.
- **Power plan** - whether the Ultimate plan is on, or switched to while a game runs.
- **Core Isolation** - reported, never changed: it is a security boundary.
- **Windows Search** - whether the indexer is busy right now.
- **Intel Application Optimization** - on a Core Ultra or 14th-gen K CPU, whether Intel's APO is installed.

A check that can't be made on this PC - no dedicated GPU, HAGS not supported, the indexer not running - is left out rather than shown as a problem, so the count is what was actually checked.

## The buttons

- **Stutter Check runs by itself** a few seconds after TrayTrigger starts, and again when you open the System page if the last result is more than ten minutes old, so the card always has a verdict. It never runs while a game is running, and a Fix it or the preset re-runs it.
- **Run Again** (and the Refresh at the right of the Performance Tweaks heading) reads everything again. It takes a second or two and runs nothing with administrator rights.
- **Copy Results** puts the whole list on the clipboard, ready for a forum post or a bug report. The same lines are in Copy Diagnostic Info.
- **Fix it** applies one of the tweaks below, with its usual prompt if it needs one.
- **Here's how** opens the page for that app or setting.
- **Open Display Settings** and **Open Core Isolation** open the Windows setting.

## Details

- Overlay apps are found by their running processes. Steam's overlay only exists while a game runs, so it isn't counted.
- Hardware acceleration is read from each browser's Local State file and Discord's settings.json; the files are only read. GPU memory per process comes from the same Windows counters Task Manager's Details tab shows.
- Windows Search counts as busy when SearchIndexer uses more than about 3 % of the CPU over a short sample.
- Intel APO is looked for as the installed Store app; Intel's download is linked from its Here's how page.
