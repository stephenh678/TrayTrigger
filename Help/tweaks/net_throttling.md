# Disable MMCSS Network Throttling

## What it changes

Removes the cap the Multimedia Class Scheduler puts on non-multimedia network packet processing while a multimedia task (game audio, video playback) is running.

## Why it helps

- The default index of 10 limits how many non-multimedia packets are processed per millisecond while audio is playing. On a 128-tick competitive server with Discord open, that cap can be reached.
- Setting the index to its "off" value removes the limit.

## Trade-offs

- The real-world benefit is not guaranteed and varies by system. Some testing has found more NDIS DPC activity with the cap off.
- MMCSS reads this value when it starts, so it takes effect after a restart.

> Requires administrator rights and a restart.

## Details

- Sets NetworkThrottlingIndex=0xFFFFFFFF under HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile. Revert writes the Windows default of 10.
