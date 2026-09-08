# Disable MMCSS Network Throttling

## What it changes

Removes the packet-rate limit that the Multimedia Class Scheduler Service applies to non-multimedia network traffic while audio or video is playing.

## Why it helps

- By default Windows caps non-multimedia traffic at roughly 10,000 packets per second whenever a multimedia task is active, to protect audio from glitches.
- Game audio counts as a multimedia task, so the cap can apply during play. On high tick-rate servers with voice chat running at the same time, it can in theory hold back game traffic.

## Trade-offs

- The real-world benefit is not guaranteed and varies by system. Many tests show no measurable change.
- Some testing has found that disabling it increases network driver DPC activity, which can be counterproductive.
- Treat it as situational. Try it, measure, and revert if you see no gain.

> Requires administrator rights. No restart needed.

## Details

- Sets NetworkThrottlingIndex to 0xFFFFFFFF under HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile.
- The default value is 10.
