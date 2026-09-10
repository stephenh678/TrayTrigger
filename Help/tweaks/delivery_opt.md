# Disable Delivery Optimization

## What it changes

Stops Windows from uploading Windows Update and Store downloads to other PCs, on your network or on the internet.

## Why it helps

- Delivery Optimization can seed updates to strangers using your upload bandwidth, with no visible indication.
- On a connection with limited upstream, that background upload can cause sudden ping spikes and packet loss in the middle of a match.

## Trade-offs

- Updates on other PCs in your home no longer pull from this one. On a fast connection you will not notice.
- Downloads to this PC still work normally. Only the sharing is turned off.
- If you already turned off "Allow downloads from other PCs" in Windows Settings, the badge shows OPTIMAL and there is nothing to apply.

> Requires administrator rights. No restart needed.

## Details

- Sets DODownloadMode=0 under HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization.
- Revert removes that policy value rather than writing a different one, so the Windows Settings control comes back instead of showing "managed by your organization".
