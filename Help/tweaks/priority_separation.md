# Foreground Priority Boost

## What it changes

Sets the Windows scheduler to give the foreground program short, variable time slices with a 3:1 priority boost over background processes. This is the setting behind System Properties, Performance, "Adjust for best performance of: Programs".

## Why it helps

- Windows client already favours the foreground a little. This sharpens it: a CPU-bound game keeps its core when Discord, a browser, or an updater wants a turn.
- It is a documented Microsoft setting, not a folklore value, and it takes effect without a restart.

## Trade-offs

- Heavy background work such as video encoding or compiling runs slower while a game has focus.
- Modest effect on a modern CPU with cores to spare; most useful on 4 to 6 core machines.
- Opt-in, off by default.

> Requires administrator rights. No restart needed.

## Details

- Sets Win32PrioritySeparation=0x26 (38) under HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\PriorityControl.
- TrayTrigger records the value it found first and puts exactly that back on revert; the Windows client default is 0x02.
