# Hardware-Accelerated GPU Scheduling

## What it changes

Lets the GPU manage its own command queue instead of the CPU scheduling every batch of work. Windows calls this HAGS.

## Why it helps

- Reduces CPU overhead and interrupt latency in the graphics driver.
- Required for NVIDIA DLSS 3 Frame Generation and some other modern driver features.
- Can slightly improve frame pacing on CPU-limited systems.

## Trade-offs

- Needs a GPU and driver that support it: NVIDIA GTX 10-series or newer, AMD RX 5000 or newer, Intel Arc.
- On some older driver versions it caused stutter. If you see that, turn it back off.
- Gains are usually small. This is a "have it on" setting, not a big win.

> Requires a restart and administrator rights. The Open Graphics Settings button takes you to the same switch in Windows Settings.

## Details

- Sets HwSchMode to 2 under HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers.
- Same setting as Windows Settings, Display, Graphics, Default graphics settings.
