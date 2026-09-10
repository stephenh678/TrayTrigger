# Exclude Drivers from Windows Update

## What it changes

Stops Windows Update from installing device drivers as part of quality updates. Windows still installs security and feature updates; only driver packages are left alone.

## Why it helps

- Windows Update can replace a GPU driver you pinned deliberately with an older or newer one, with no notice. A sudden change in stutter or crashes "this week" is often that.
- Chipset, audio, and network drivers get swapped the same way.

## Trade-offs

- You now own driver updates: use the GPU vendor's app or the manufacturer's site. A driver with a security fix will not arrive on its own.
- This is a policy value, so the Optional Updates page in Windows Update stops offering drivers too.
- Off by default and skipped by Apply Performance Preset for that reason.

> Requires administrator rights. No restart needed.

## Details

- Sets ExcludeWUDriversInQualityUpdate=1 under HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate.
- This is the documented group policy "Do not include drivers with Windows Updates". Revert removes the value rather than writing a different policy, so Windows Settings is not left showing "managed by your organization".
