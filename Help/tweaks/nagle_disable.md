# Disable Nagle's Algorithm

## What it changes

Tells the TCP stack to send small packets immediately instead of batching them, and to acknowledge every packet instead of every other one, on every network interface.

## Why it helps

- A TCP-based real-time app that does not set TCP_NODELAY itself can see up to 200 ms of extra delay from Nagle plus delayed ACK.

## Trade-offs

- Real-time multiplayer games overwhelmingly use UDP for latency-sensitive traffic precisely to avoid this, and TCP in a modern game is usually matchmaking, chat, and patching. Expect no change in most games, which is why this is opt-in.
- Slightly more packets on the wire for bulk TCP transfers.
- Applies to every interface present now; an adapter added later (a VPN, a new NIC) is not covered until you apply again, and the badge shows STANDARD until every adapter has the values.
- The values are read when an interface binds, so a restart (or disabling and re-enabling the adapter) is needed.

> Requires administrator rights and a restart.

## Details

- Sets TcpAckFrequency=1 and TCPNoDelay=1 under every interface key below HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces. Revert deletes both values, which is the Windows default.
