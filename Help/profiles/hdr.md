# Enable HDR (profile)

## What it changes

Turns on Windows' native "Use HDR" display mode for the duration of the session, then puts every display back to whatever it was set to before the game launched.

## Why it helps

- Some games only render their full HDR color and brightness range when Windows itself is already in HDR mode - if HDR is off system-wide, the game falls back to SDR even if it supports HDR.
- This flips the same switch as Settings > System > Display > HDR, so you don't have to turn it on by hand before playing and remember to turn it back off after.

## Trade-offs

- Only applies to displays Windows already reports as HDR-capable. Displays without HDR support are left untouched.
- A display already in HDR before the game launched is left alone, and is left in HDR afterward rather than being forced off.
- Not every game renders HDR well - one that doesn't can look washed out, over-bright, or have crushed blacks with this on. This is why the tweak defaults off even though it lives under Optimized.
- Affects every app on that display while the game runs, not just the game itself, since HDR is a system-wide display mode.
- **Risk if your display uses Windows' "Automatically manage color for apps" (Auto Color Management):** a display already sitting in ACM's "WCG" mode (wide color, short of full HDR - this is common as Windows' own default desktop color mode on 24H2+) is also forced into full HDR while the game runs. Windows' HDR API can only turn a display fully off on restore, not back to WCG specifically, so that display may render in plain SDR after the game closes instead of returning to WCG - until Windows re-negotiates it on its own (e.g. switching to another app, or toggling HDR by hand once in Settings).

## Details

- Not a registry value - HDR is a live per-display color pipeline negotiated with the monitor over EDID, so it's controlled through the same Connecting and Configuring Displays (CCD) API the Settings app uses (`DisplayConfigGetDeviceInfo`/`DisplayConfigSetDeviceInfo` with the advanced-color device-info types), not a static setting.
- On exit, each display's captured prior HDR state is restored individually - a monitor that was already HDR-on keeps it on, one that was off (including one that was in WCG mode - see the trade-off above) goes back to off.
