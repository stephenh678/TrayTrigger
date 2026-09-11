# How Performance Profiles work

A Performance Profile is a set of system changes that apply only while a specific game is running. Assign one per game in Edit Game. When the game launches, the profile is applied; when the game exits, everything is put back exactly as it was.

## Optimized versus Aggressive

- Optimized applies the low-risk set: the full-clock power plan and the high-performance GPU preference. Enable HDR and Do Not Disturb are available under Optimized but stay off until you turn them on.
- Aggressive applies everything in Optimized plus MMCSS scheduling changes, Above Normal process priority, and a 0.5 ms timer resolution request. The Defender exclusion is available under Aggressive but stays off until you turn it on.
- The toggles on this page control which tweaks each tier includes. Changing one affects every game assigned to that tier.
- Separately from the tier, each game can be pinned to performance cores on a hybrid CPU (Edit Game, CPU Cores). See the CPU Cores topic.

## What the badges mean

- ENABLED or DISABLED shows whether that tweak is part of its tier right now. Enabled tweaks apply to every game assigned to the tier the next time one launches; a game already running keeps its current session.
- OPT-IN marks a tweak with a real trade-off, such as Enable HDR, Do Not Disturb, or the Defender exclusion. It stays off until you turn it on yourself, even for games on that tier.
- ADMIN marks a tweak that writes a machine-wide value, so Windows shows a User Account Control prompt the first time it is applied in a session.
- In the library, a green PLAYING badge on a game card means a profile session is active for it; End Session and Force Close in the card's right-click menu act on that session.

## Order of events

- Before the game starts: the profile snapshots the current values, then applies every tweak that does not need the running process. The game therefore starts up already on the fast plan, with its GPU preference and any Defender exclusion in place.
- Your pre-launch script runs next, if you set one. If it is set to cancel the launch on failure and it fails, the profile is rolled back and the game is not started.
- The game starts. Above Normal priority is applied to the game's process as soon as TrayTrigger has found it.
- The game exits. The profile restores the snapshot, then your post-exit script runs.

## Two games at once

Machine-wide tweaks such as the power plan are applied by the first game to launch and restored only when the last tracked game exits. Per-game tweaks such as GPU preference are applied and restored for each game independently.

## Steam and other launchers

- Steam games are tracked through Steam's own "running" flag, so the profile applies before the Steam launch and restores when Steam reports the game closed. TrayTrigger also looks for the game's own process under its Steam install folder once Steam reports it running, so Above Normal priority, window focus, and Force Close work for Steam games too. GPU preference and the Defender exclusion need a real exe path, which a Steam link does not provide, so those two do not apply to Steam-by-AppId games.
- GOG, EA, Epic, and Ubisoft games launched through their client (or directly) are tracked by watching the game's install folder for its real process, because the exe the client registers is often only a short-lived launcher stub. Every tweak, priority included, applies to them.
- A bare launcher link, meaning a dropped .url or protocol shortcut with no platform ID behind it, has no exit signal at all, so profiles are not applied for it.
- Launching a game that is already running never re-applies the profile or re-runs scripts; TrayTrigger just brings its window forward.
- "Close the launcher after this game exits" (Edit Game, launcher card) shuts the platform client down once the session ends, so it does not stay resident with its overlay and background processes. For Steam it also changes how the client is started: when Steam is not already running, TrayTrigger starts it minimized to the tray and launches the game in the same step, so only the game appears. If Steam is already open, or the option is off, the game is launched through Steam's normal steam:// link and the client window is left as it was.

## Now Playing, End Session, and Force Close

- While a session is tracked the game shows a PLAYING badge, and the tray menu has a Now Playing section for it.
- End Session restores the profile and runs the post-exit script immediately, without touching the game. Use it if a session looks stuck, for example a launcher stopped on an update dialog so the game never appeared.
- Force Close kills the game's process, then ends the session. Anything unsaved in the game is lost.
- If the game never appears (Steam or the launcher never reports it running) the profile is rolled back automatically after three minutes.

> If TrayTrigger or Windows crashes mid-game, the snapshot is still on disk. The next time TrayTrigger starts it restores your pre-game settings automatically. On a normal Windows shutdown or sign-out, TrayTrigger restores the power plan, HDR, and GPU preference right away and finishes any tweak that would need an administrator prompt on the next start.
