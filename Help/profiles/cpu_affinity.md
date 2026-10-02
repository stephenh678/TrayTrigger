# CPU Cores

## What it does

Keeps a game on the cores that suit it, for the length of the session. Set per game in Edit Game > Performance, or from the game's right-click menu; it is independent of the Optimized or Aggressive tier. TrayTrigger reads how your CPU is built and offers only the choices that mean something on it:

- **Intel hybrid CPUs** (12th gen and later, and similar designs with performance and efficiency cores): **Performance cores only**.
- **Ryzen with 3D V-Cache on one of two CCDs** (7950X3D, 7900X3D, 9950X3D, 9900X3D): **3D V-Cache cores** or **Frequency cores**.
- **Other Ryzen with two or more CCDs** (7950X, 9950X and the like): **First CCD only**.
- **Auto** picks for you: the performance cores on a hybrid Intel, the 3D V-Cache cores on a dual-CCD X3D, and no change on anything else. Because it is worked out at each launch, a library that moves to another PC does the right thing there.

On a CPU with nothing to choose - every core alike, or a single-CCD X3D such as the 7800X3D or 9800X3D, where every core already has the cache - the option isn't shown.

## Why it helps

- Some engines and some anti-cheat titles run worse when their threads land on efficiency cores: lower frame rates, stutter when a thread migrates, or an anti-cheat that misreads the topology.
- On a dual-CCD X3D, most games run fastest on the CCD with the extra cache. Windows, AMD's chipset driver and Game Bar are meant to put them there, but often don't - the game lands on the wrong CCD, or is split across both. Keeping it on the V-Cache CCD is the usual fix, and what people otherwise use Process Lasso for.
- A few games prefer clock speed to cache. **Frequency cores** is for those.

## Trade-offs

- Leave it on Default for games that scale across all cores; taking cores away costs them throughput.
- A game running as administrator, or protected by anti-cheat, can refuse the change. TrayTrigger logs it and the game runs as normal. Some anti-cheat titles refuse it only while they start: set **seconds to wait after the game starts** (try 30) and the change is made then instead.
- TrayTrigger doesn't touch core parking, Game Bar or AMD's driver. A game left on Default is not changed at all.

## Details

- Cores are told apart with GetSystemCpuSetInformation (EfficiencyClass, the same data the Windows scheduler uses; the highest class is the performance cores) and GetLogicalProcessorInformationEx, whose L3 cache sizes find the V-Cache CCD: its L3 is 96 MB against the other CCD's 32 MB. There is no list of CPU models, so a CPU released later is recognized the same way. System > Hardware shows what was found.
- Applied with CPU Sets (SetProcessDefaultCpuSets), Windows' soft form of affinity, rather than a hard affinity mask: the scheduler keeps the game's threads on those cores but stays free to work with power management, and a game that sets its own thread affinity still can. A hard mask is what crashed some games and what anti-cheat objects to most.
- Applied as soon as TrayTrigger has the game's process, including Steam games once their process is found, and to every process the game has started. CPU Sets aren't passed on to a new process the way an affinity mask is, so for the next ten minutes TrayTrigger keeps looking for processes the game starts - the game a launcher starts when you press Play in it, say - and sets each one too.
- Nothing to put back: CPU Sets end with the process.
