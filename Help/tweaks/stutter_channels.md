# Memory channels: one stick or two

## Why it matters

A PC's memory works over channels, and two matching sticks in the right slots give it twice the bandwidth of one. A single stick runs on one channel. How much that costs depends on the game: with a strong graphics card at high settings the difference is often a few percent, and in games limited by the processor, or on built-in graphics, testing has found it to be much larger, up to around a third of the frame rate. The 1% lows suffer too.

## What to do

1. Look up your memory's make and model, and buy a second stick of the same kind. A matching pair, or a kit sold as two sticks, is the safest choice.
2. Fit the sticks in the slots your motherboard manual names for two sticks. On most boards that is slots 2 and 4, counting from the processor, not the two next to each other. The manual says which.
3. If the speed drops after fitting, turn XMP or EXPO on again in the BIOS (see Memory speed: XMP and EXPO).

## Details

TrayTrigger reads the memory sticks and the board's slots from Windows' hardware information. It flags a single stick only when the board has room for more. Memory soldered to the board, which is common in thin laptops, and boards with one slot are left out because there is nothing to add. Two or more sticks are shown as fine; Windows does not say which slots they are in, so TrayTrigger cannot tell whether they are paired correctly.
