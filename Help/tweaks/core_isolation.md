# Core Isolation / Memory Integrity

## What this shows

Whether Memory Integrity, also called Hypervisor-protected Code Integrity or HVCI, is currently on. TrayTrigger reports it but does not change it.

## Why it matters for gaming

- Memory Integrity runs kernel driver code inside a virtualisation boundary. Microsoft has documented a CPU cost in some games, typically a few percent, sometimes more on older CPUs.
- Turning it off recovers that headroom.

## Why you might leave it on

- It is a real security feature. It blocks a class of attacks that load a vulnerable or malicious driver to take over the kernel, which is how several game cheats and ransomware families operate.
- Microsoft enables it by default on new Windows 11 installs for that reason.

> This is a judgement call about security versus a small performance gain, so TrayTrigger will not flip it for you. The Open Core Isolation Settings button takes you to the switch in Windows Security. A restart is required after changing it.

## Details

- Read from Enabled under HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity.
