# Disable Background Game DVR

## What it changes

Turns off Xbox Game Bar's background recording, the feature that lets you save "the last 30 seconds" after something happens.

## Why it helps

- To make that possible, Windows keeps a hardware encoder running the whole time a game is open and writes a rolling buffer to disk.
- That ties up the NVENC or AMD encoder and adds disk writes, which can cause frame dips, especially if you also stream or record with other software.

## Trade-offs

- You lose the Win+Alt+G "record that" shortcut. Manual recording with Win+Alt+R still works if the Game Bar overlay itself is left on.
- No restart and no administrator rights needed.

## Details

- Sets GameDVR_Enabled=0 under HKEY_CURRENT_USER\System\GameConfigStore, and AppCaptureEnabled=0 under HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\GameDVR.
- Same as Windows Settings, Gaming, Captures, "Record in the background while I'm playing a game".
