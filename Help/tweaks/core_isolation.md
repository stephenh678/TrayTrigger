# Core Isolation / Memory Integrity (HVCI)

## What it is

Memory Integrity runs kernel-mode code integrity checks inside a hypervisor-protected environment, so a vulnerable or malicious driver cannot inject code into the kernel. It is a real security boundary.

## Why it is shown here

- Microsoft has documented a CPU cost in some games while it is on, roughly 3 to 8 percent in the cases they measured.
- Whether that trade is worth it is your call, so TrayTrigger only shows the state and links to the Windows Security page.

## Reading the badge

- ON means Memory Integrity is running right now, read from the DeviceGuard status Windows itself reports.
- OFF means it is not running. Because Windows applies a change to this setting at the next restart, the badge can differ from the switch in Windows Security until you reboot.
- This row is informational. It is never counted in the optimization score, and turning a security feature off is not presented as an optimization.

## Details

- Read from Win32_DeviceGuard SecurityServicesRunning (value 2), falling back to the Enabled value under HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity.
