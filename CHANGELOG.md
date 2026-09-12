# Changelog

Notable, user-facing changes per stable release. The release workflow copies the matching
section into the GitHub Release as its **Highlights**, above the full commit list, so keep
each entry short and written for players, not for the git log.

Format: one `## x.y.z` heading per stable version (no `v` prefix), then a few bullets.
Pre-release builds (`-beta.N`, `-rc.N`) do not get their own section; they roll up into the
stable version they lead to.

## 1.4.1

- **Library filters** — a filter flyout beside the sort box narrows the library by launcher, performance profile, and state (never played, favorite, missing executable, has a hotkey, has scripts, runs elevated). A count badge shows how many filters are active, and an empty result explains itself instead of looking like an empty library.
- **Quick settings in the right-click menu** — CPU Cores, Run as Administrator, and Close Launcher After Game Exits are now one click away for a single game or a whole selection. The single-game menu is grouped into Change and Steam submenus instead of a 16-item flat list.
- **Unmute Speakers While Playing** — new opt-in Optimized tweak that clears a muted playback device on launch and puts the mute back on exit.
- **HDR fixes** — Enable HDR no longer blanks displays that were already in HDR when the game started, and no longer fires at wide-color-gamut displays that have no HDR mode.
- **Poster cards zoom on hover**, Xbox-dashboard style.
- **Beta builds track betas** — a pre-release build now sees newer pre-releases even with the beta toggle off, so testers are never stranded on an old beta reporting "up to date".
- **Fewer disk writes** — launching or exiting a game no longer rewrites settings.json, and a multi-launcher scan commits once instead of once per platform.
- **Per-user install** — the installer now installs for the current user only and clears any old all-users registration; uninstall removes everything the app leaves behind.
- **Better logs** — every line is timestamped, each session is stamped with its build, and two running instances no longer overwrite each other's log.
- **Scanner accuracy** — Ubisoft Connect titles use the folder name the launcher trusts, short names like R6 and P3R keep their digit, VR editions count as their own game, and the Downloads folder is no longer walked for games.
- RAWG metadata is off by default again (like SteamGridDB); enter a key in Settings to turn it on.

## 1.4.0

- **Per-game scripts** — attach your own `.bat`, `.cmd`, `.ps1`, or `.exe` to any game as a pre-launch or post-exit script, with Test Run buttons, per-game arguments, hidden or elevated execution, and a scripts folder that ships with blank templates, five real-world examples, and a README.
- **Default scripts** — set a script once in Settings and it runs for every game that has no script of its own, with a per-game opt-out. Off until you enable it.
- **Change Match** in the context menu — re-pick which Steam or RAWG entry a game matches when the automatic match was wrong.
- **Batch selection count** shown in the context menu when several games are selected.
- A failed decrypt no longer wipes a stored API key.
- Sidebar and title bar polish: a visible edge on the rail, title bar colors derived from the palette.
