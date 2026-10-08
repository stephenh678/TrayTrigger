# NVIDIA Shader Cache: Unlimited

## What it changes

Sets the NVIDIA driver's shader cache size to Unlimited on the Global profile, the same choice as Shader Cache Size in the NVIDIA Control Panel's Manage 3D settings.

## Why it helps

- A game compiles each shader the first time it needs it, and the driver keeps the result on disk so the next session doesn't compile it again.
- When the cache reaches its size limit, the driver deletes the oldest shaders. A game you come back to then compiles them again while you play, and the hitches and frame-time spikes of a first run come back.
- A large library fills the default limit quickly. Unlimited means nothing is thrown out to make room.

## Trade-offs

- The cache folder, %LOCALAPPDATA%\NVIDIA\DXCache, keeps growing. With a big library it can reach tens of GB.
- A driver update clears the cache whatever the size setting, so the first session after one still compiles.
- Only for NVIDIA GPUs. AMD and Intel drivers manage their own shader caches, so the row shows N/A on those PCs.

> No administrator rights or restart needed. Games started afterwards use the new size.

## Details

- Writes setting 0x00AC8497 ("Shader disk cache maximum size") on the driver's Global profile with NVIDIA's settings API, the same database the Control Panel and NVIDIA App use. A game whose own profile sets a different size keeps it.
- TrayTrigger records what the Global profile had first: a size you chose, NVIDIA's own value, or nothing. Restore Previous puts that back.
- Restore Previous changes nothing if the size has been changed since, for example in the NVIDIA App. That size is yours now, and TrayTrigger leaves it alone.
