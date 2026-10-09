# Memory speed: XMP and EXPO

## Why it matters

Memory is sold rated for a speed, but a PC boots it at a safe fallback - often 4800 MHz for DDR5 that is rated 6000 - until the profile is turned on in the BIOS. On a Ryzen in particular the memory speed sets the Infinity Fabric clock, so running at the fallback costs frame-time consistency: the 1% lows that feel like stutter. It is the most overlooked fix in the hardware threads.

## How to turn it on

1. Restart and open the BIOS (usually Delete or F2 as the PC starts).
2. Find the memory profile: **XMP** on Intel boards, **EXPO** on AMD boards (some AMD boards show both, or call it DOCP or A-XMP).
3. Choose Profile 1, save and exit.

The memory is designed and sold to run at that speed. If the PC won't boot afterwards, the BIOS will fall back by itself after a failed start or two, and the slower profile is the one to use.

## Details

TrayTrigger compares the speed the memory reports it runs at with the speed its modules are rated for, from Windows' hardware information. Hardware Specs shows both under Memory.
