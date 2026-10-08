# Keep the DirectX Shader Cache

## What it changes

Takes the DirectX Shader Cache out of Windows' automatic cleanups, such as Storage Sense and the Disk Cleanup Windows runs on its own when a drive gets low on space.

## Why it helps

- Windows keeps the shaders DirectX games compile in a cache (the D3DSCache folder), so they aren't compiled again next time.
- The automatic cleanups count that cache as junk and delete it. After that, every game rebuilds its shaders while you play, with the stutter of a first run.
- Keeping the cache keeps games as smooth on their next start as they were at the end of the last one.

## Trade-offs

- The cache is no longer trimmed automatically, so it uses some disk space. It's usually a few GB at most.
- Disk Cleanup still lists DirectX Shader Cache when you run it yourself, so you can clear it by hand if a game's cache ever goes bad, for example after a game update starts crashing on load.

> Requires administrator rights. No restart needed.

## Details

- Sets Autorun=0 under HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches\D3D Shader Cache. Windows ships it as 1, which lets automatic cleanups empty the cache.
- TrayTrigger records the value it found first and puts exactly that back on Restore Previous.
- Shown as N/A on a copy of Windows that has no DirectX Shader Cache cleanup handler, since then nothing deletes the cache automatically.
