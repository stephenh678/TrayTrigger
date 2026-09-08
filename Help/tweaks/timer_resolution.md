# System Timer Resolution

## What it changes

Restores the older behaviour where any process asking for a high-resolution system timer raises it for the whole system, instead of only for itself.

## Why it helps

- Windows 10 version 2004 and Windows 11 made timer resolution per-process. A game that does not request a fine timer itself can be left on the coarse default tick of about 15.6 milliseconds.
- That coarse tick shows up as stutter, uneven frame pacing, or a frame rate that will not go above a certain ceiling.
- With this on, as long as anything on the system asks for a fine timer, every process benefits.

## Trade-offs

- A finer timer means slightly more CPU wake-ups at idle, so marginally higher idle power use.
- Most modern engines request the timer themselves and see no difference.

> Requires a restart and administrator rights.

## Details

- Sets GlobalTimerResolutionRequests=1 under HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\kernel.
- This is the value Microsoft documented alongside the per-process timer change.
