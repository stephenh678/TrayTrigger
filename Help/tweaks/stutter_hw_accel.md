# Browser and chat apps holding video memory

## Why it matters

A browser or Discord with hardware acceleration on renders through the GPU, and keeps video memory for its tabs and video while it's open. On a card with 8 or 12 GB that is often the difference between a game's textures fitting in memory and not, and when they don't fit the game stutters as it swaps them. Stutter Check shows how much they hold right now.

## Two ways to fix it

**Turn hardware acceleration off** in the app. Pages and video render on the CPU instead, which is fine for most use.

- **Chrome, Edge, Brave**: Settings > System > "Use graphics acceleration when available", then relaunch.
- **Discord**: User Settings > Advanced > Hardware Acceleration.

**Or run the app on the built-in GPU**, if your CPU has one, so it keeps acceleration but stops using the gaming card's memory: Windows Settings > System > Display > Graphics, add the app, and set it to Power saving. Discord's screen share still uses the gaming card's encoder.

## Details

TrayTrigger reads each browser's Local State file and Discord's settings.json to see whether acceleration is on. It only reads them. Closing the app before you play works too, of course.
