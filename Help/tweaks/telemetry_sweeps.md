# Disable Diagnostic Telemetry Sweeps

## What it changes

Sets the Windows diagnostic data policy to its lowest level, which reduces how often background telemetry tasks run.

## Why it helps

- Tasks such as CompatTelRunner periodically scan installed software and drivers, using CPU and disk in the background. With the policy lowered they run less often and collect less.

## Trade-offs

- On Windows Home and Pro the lowest level Windows actually honours is "Required". Only Enterprise and Education editions can turn diagnostic data fully off. Treat this as a reduction, not an elimination.
- Some Windows features that depend on diagnostic data, such as the Insider Program, need a higher level.

> Requires administrator rights. No restart needed.

## Details

- Sets AllowTelemetry=0 under HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection.
- Revert removes the policy value entirely, so the Diagnostics page in Windows Settings returns to your own choice instead of being locked to a policy.
