# Enable Auto HDR (profile)

## What it changes

For games that do not support HDR: turns on Windows 11's Auto HDR while a game runs, so it gets HDR-style brightness and colour on an HDR display, then puts that game's setting back. It needs HDR on: switch on Enable HDR as well, or leave Windows HDR on.

## Per game

Edit Game > Performance has an **Auto HDR** choice for each game, below HDR, on a Windows 11 PC with an HDR-capable display:

- **Profile setting** follows the Optimized profile's Enable Auto HDR switch, like every other game on the profile. This is how every game starts out. With the switch off, TrayTrigger changes nothing: your own Windows settings for the game stand.
- **On for this game** turns Auto HDR on for that game when it launches, and back to what it was when it exits, whatever its profile - even Off, where HDR and Auto HDR are then the only things the session changes.
- **Off for this game** turns Auto HDR off for that game while it runs, even when the profile would turn it on. For a game whose HUD blows out or whose whites clip.

A change to a game's Auto HDR choice is recorded in Activity & History.

## What it needs

- Windows' HDR must be on. The game's own HDR choice or the profile's Enable HDR switch can turn it on for the session, or you can leave it on yourself.
- The global Auto HDR switch (Settings > System > Display > HDR, or the Auto HDR tweak on the System page) must be on. A game's own Auto HDR setting only applies while it is.
- Auto HDR covers DirectX 11 and 12 games. There is no official list of supported games.

## Trade-offs

- Not every game looks better. A HUD can blow out and whites can clip, which is why this is off by default and why each game can decide for itself.
- Windows restarts nothing, but a game that is already running keeps the setting it started with. The change takes effect the next time the game starts.
- A game that Windows has switched off Auto HDR for in Settings > System > Display > Graphics is still changed while it runs, and put back afterwards.

## Details

- Writes the game's own entry under HKEY_CURRENT_USER\Software\Microsoft\DirectX\UserGpuPreferences. The AutoHDREnable number is odd for on and even for off (2097 and 2096 on current Windows 11). Only that number changes; the rest of the entry, including the GPU preference, is kept.
- The game's entry is saved before it is changed and restored when the game exits, and after a crash the next start restores it.
- See Enable HDR, and the Auto HDR System tweak, which switches Auto HDR for every game at once.
