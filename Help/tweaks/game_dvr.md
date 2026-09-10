# Disable Game Bar Captures and Background Recording

## What it changes

Turns off the Game Bar capture feature as a whole: background recording ("Record what happened"), manual Win+Alt+R recording, and Game Bar screenshots.

## Why it helps

- Background recording keeps a hardware encoder (NVENC, AMD VCE, Intel QuickSync) running and writes a rolling clip to disk the whole time a game runs. That is encoder contention for anything else using it and constant SSD writes.
- The Game Bar capture pipeline also hooks into the game swap chain, which some games and overlays dislike.

## Trade-offs

- You lose Game Bar clips and screenshots. OBS, ShadowPlay, and ReLive still work.
- No restart and no administrator rights needed.

## Details

- Sets GameDVR_Enabled=0 under HKEY_CURRENT_USER\System\GameConfigStore, and AppCaptureEnabled=0 plus HistoricalCaptureEnabled=0 under HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\GameDVR.
- HistoricalCaptureEnabled is the specific "Record what happened" switch on Windows 11; AppCaptureEnabled is the overall captures switch.
