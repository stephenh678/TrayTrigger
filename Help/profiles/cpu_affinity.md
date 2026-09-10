# CPU Cores: Performance Cores Only

## What it does

On a hybrid CPU (Intel 12th generation and later, and similar designs with performance and efficiency cores), pins the game process to the performance cores for the length of the session. Set per game in Edit Game; it is independent of the Optimized or Aggressive tier.

## Why it helps

- Some older engines and some anti-cheat titles run worse when their threads land on efficiency cores: lower frame rates, stutter when a thread migrates, or an anti-cheat that misreads the topology.
- Pinning to P-cores is the standard community fix for those titles, and the Windows Thread Director does the right thing for the rest.

## Trade-offs

- Leave it on Default for games that scale across all cores; taking away the E-cores costs them throughput.
- No effect on a CPU without efficiency cores. The setting is kept per game so a library moved to a hybrid machine picks it up.
- An elevated or anti-cheat protected game process may refuse the affinity change; TrayTrigger logs it and continues.

## Details

- Cores are classified with GetSystemCpuSetInformation (EfficiencyClass), the same data the Windows scheduler uses; the highest class is treated as performance cores.
- Applied with SetProcessAffinityMask as soon as TrayTrigger has the game process, including Steam games once their process is found. Nothing to restore: affinity dies with the process.
