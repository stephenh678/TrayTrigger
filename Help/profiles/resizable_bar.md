# Resizable BAR for Games NVIDIA Hasn't Decided On (profile)

## What it changes

Turns on NVIDIA's Resizable BAR while an Aggressive game runs, for every game NVIDIA hasn't made a decision about. Games NVIDIA approved or rejected keep NVIDIA's choice. Turned back off when the last game exits.

## Why it helps

- Resizable BAR lets the CPU reach all of the graphics card's memory at once, instead of through a 256 MB window.
- NVIDIA switches it on only for games it has tested and approved, about 60 of them. It switches it off for the games where it tested worse, such as Final Fantasy XVI, Delta Force, Hogwarts Legacy and Alan Wake 2. Every other game, thousands of them, gets nothing, including many recent ones that would gain.
- Tested gains range from nothing to around 20% (Gears 5) and 46% (Dead Space), and average a few percent. Newer DirectX 12 games, built to stream a lot of data to the graphics card, tend to gain the most.

## Trade-offs

- Some undecided games may run worse, with lower 1% lows or stutter. This is more likely in older games. That's why it's in Aggressive rather than Optimized. If a game runs worse, move it to Optimized in Edit Game.
- Needs Resizable BAR on in the BIOS (Above 4G Decoding and Re-Size BAR Support). System > Hardware Specs shows whether it's on. If it's off, nothing is changed.
- NVIDIA GPUs only. AMD's Smart Access Memory and Intel Arc already use Resizable BAR for every game.

## Details

- Sets NVIDIA's hidden "rBAR - Feature" setting (0x000F00BA) to 1 on the driver's Global profile, the switch NVIDIA itself uses on the games it approves.
- A game's own NVIDIA profile outranks the Global one. NVIDIA records its approvals and rejections there, so this only reaches games it hasn't decided on.
- If you've already set it on the Global profile yourself, on or off, TrayTrigger leaves it alone.
- Applied by the first game to launch and restored when the last one exits, with the value it found kept in the crash-recovery record. If it's changed elsewhere during the session, TrayTrigger leaves that change alone.
