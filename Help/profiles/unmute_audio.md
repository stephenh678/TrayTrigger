# Unmute Speakers While Playing

## What it does

When a game with this profile starts, TrayTrigger clears the mute flag on your current playback device. If the device was muted, the mute goes back on when the game exits.

## Why it helps

- Launching into silence because the speakers were muted from earlier costs a trip through the volume flyout, and on a machine that boots muted it costs it every session.
- It follows whichever device Windows currently treats as the default, so switching from speakers to a headset between sessions needs no setup here.

## Trade-offs

- Silence is often deliberate. That is why this is opt-in.
- If you mute manually while the game is running, that mute is what the exit restore overwrites - the session records the state from the moment the game started, not the moment it ended.

## Details

- Uses Core Audio (`IMMDeviceEnumerator` to `IAudioEndpointVolume.SetMute`), the same call the volume flyout's speaker button makes. No elevation, no registry write, and it reaches the running mixer immediately.
- Volume level is untouched. A device sitting at 5% still plays at 5% - this lifts the mute and nothing else.
- Nothing is recorded when the device was already unmuted, which is the usual case, so most sessions leave no state behind and the exit restore does nothing.
- The prior state is captured in the session snapshot, so a crash still restores the mute on the next start.
- Restore applies to whichever device is default when the game exits. If you moved the sound somewhere else mid-session, TrayTrigger follows you rather than re-muting a device you have walked away from.
- A machine with no playback device at all is logged and skipped.
