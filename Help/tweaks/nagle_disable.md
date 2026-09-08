# Disable Nagle's Algorithm

## What it changes

Turns off TCP's Nagle algorithm and delayed acknowledgements on every network adapter, so small packets are sent immediately instead of being held briefly to combine with others.

## Why it helps

- For applications that send many tiny TCP messages and care about every millisecond, the batching delay can add noticeable latency.
- Older MMOs and some chat-heavy games used TCP for gameplay traffic and benefited from this.

## Trade-offs

- Modern real-time games use UDP for anything latency-sensitive precisely to avoid this problem. TCP is left for matchmaking, chat, and patching, where the delay is irrelevant.
- An application that needs it can already disable Nagle for its own connections. A system-wide change affects everything, including downloads, which may get slightly less efficient.
- Largely a leftover from older advice, so it is OPT-IN and excluded from Apply Performance Preset.

> Requires administrator rights. No restart needed, but existing connections keep the old behaviour until reopened.

## Details

- Sets TcpAckFrequency=1 and TCPNoDelay=1 on each adapter under HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces.
