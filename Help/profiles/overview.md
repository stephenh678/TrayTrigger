# How Performance Profiles work

A Performance Profile is a set of system changes that apply only while a specific game is running. Assign one per game in Edit Game. When the game launches, the profile is applied; when the game exits, everything is put back exactly as it was.

## Optimized versus Aggressive

- Optimized applies the low-risk set: the full-clock power plan and the high-performance GPU preference.
- Aggressive applies everything in Optimized plus MMCSS scheduling changes and Above Normal process priority. The Defender exclusion is available under Aggressive but stays off until you turn it on.
- The toggles on this page control which tweaks each tier includes. Changing one affects every game assigned to that tier.

## Order of events

- Before the game starts: the profile snapshots the current values, then applies every tweak that does not need the running process. The game therefore starts up already on the fast plan, with its GPU preference and any Defender exclusion in place.
- Your pre-launch script runs next, if you set one.
- The game starts. Above Normal priority is applied to the new process.
- The game exits. The profile restores the snapshot, then your post-exit script runs.

## Two games at once

Machine-wide tweaks such as the power plan are applied by the first game to launch and restored only when the last tracked game exits. Per-game tweaks such as GPU preference are applied and restored for each game independently.

## Steam and other launchers

- Steam games are tracked through Steam's own "running" flag, so the profile applies before the Steam launch and restores when Steam reports the game closed.
- TrayTrigger never holds the real process for a Steam game, so Above Normal priority does nothing there. GPU preference and the Defender exclusion also need a real exe path, which a Steam link does not provide.
- Links from other launchers such as Epic or GOG give no exit signal at all, so profiles are not applied for them.

> If TrayTrigger or Windows crashes mid-game, the snapshot is still on disk. The next time TrayTrigger starts it restores your pre-game settings automatically.
