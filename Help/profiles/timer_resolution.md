# 0.5 ms Timer Resolution Request

## What it does

While a game with an Aggressive profile runs, TrayTrigger asks Windows for a 0.5 millisecond system timer (NtSetTimerResolution) and releases the request when the last tracked session ends. This is what tools such as TimerTool and ISLC do.

## Why it helps

- Sleep() and timer waits inside a game and its driver stack wake on time instead of up to 15.6 ms late, which shows up as smoother frame pacing in engines that do not request a fine timer themselves.

## What it needs

- On Windows 10 version 2004 and later and on Windows 11, timer resolution is per process. The TrayTrigger request only reaches the game when the permanent "System Timer Resolution" tweak (GlobalTimerResolutionRequests) is on. Turn that on under Performance Tweaks first, then restart once.
- Most modern engines already request a fine timer; those see no difference.

## Trade-offs

- A finer timer means slightly more CPU wake-ups, so a little more power draw, only while a game is running.
- Nothing to recover after a crash: Windows drops the request when TrayTrigger exits.
