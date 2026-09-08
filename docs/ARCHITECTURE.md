# Architecture

## Data path

```text
Windows applications
        ↓ IPv4
Wintun: DualLink Bond (10.77.0.2/24, MTU 1380)
        ↓ encrypted LinkWeaver frames
per-adapter UDP sockets bound with IP_UNICAST_IF
        ↓ Ethernet / Wi-Fi / USB tethering
VPS UDP 443
        ↓ decrypt, reorder, replay-check
Linux TUN: dlbond0 (10.77.0.1/24)
        ↓ nftables masquerade
Internet through VPS public IPv4
```

Two `/1` routes point Internet traffic into Wintun while a separate `/32` route to the relay exists through every physical gateway. This prevents the tunnel from recursively tunneling its own UDP transport.

## Single-flow bonding

LinkWeaver captures the IP packets belonging to one TCP or UDP flow before the public Internet sees them. Individual packets are encapsulated and scheduled across different physical adapters. The VPS decrypts and reorders them, then emits the original flow through one public address. Return packets follow the inverse process. This is packet-level aggregation, not Windows application-level load balancing.

## Transport framing

Protocol version 1 uses:

- a 32-byte authenticated header;
- up to 1,400 bytes of inner payload;
- a 16-byte AES-GCM authentication tag;
- packet kind, direction, path ID, session ID, sequence, acknowledgement, and payload length;
- a session key derived with HMAC-SHA-256 from the 256-bit relay key and random session ID;
- nonces composed from direction, path, packet kind, and sequence;
- a 4,096-entry replay window.

The TUN MTU is 1,380 bytes to leave room for IP, UDP, protocol, and encryption overhead. Networks with unusually small path MTUs may require further reduction.

## Scheduling

The adaptive scheduler rejects paths that are offline, have no delivery rate, or have very low reliability. It estimates packet arrival using:

- half RTT plus jitter;
- queued-byte serialization time;
- packet serialization time at estimated delivery rate;
- loss penalty;
- recent reliability penalty;
- a virtual finish time that prevents one fast path from being overfilled.

This is weighted packet scheduling, not fixed 50/50 round robin. Heterogeneous paths can contribute in different proportions.

## Ordering

Packets share a session sequence space. The receiver buffers out-of-order packets for up to 150 ms, then advances rather than blocking forever on a missing packet. Excess latency difference or loss can therefore lower throughput through head-of-line waiting or skipped packets.

## Liveness and failover

- Client probes: every 150 ms.
- Client reply timeout: approximately 600 ms.
- Relay peer timeout: 750 ms.
- Control update: every 300 ms.
- Direct monitoring cadence: 250 ms, with Windows adapter-change wakeups.
- Immediate UDP socket failures mark a path failed and retry the packet once on another usable path.
- Reconnected adapters are recreated automatically when their address/interface configuration returns.

These values balance fast detection with cellular jitter. **Redundant** mode offers the best protection against the packets sent during the detection window.

## NAT

The VPS masquerades `10.77.0.0/24` through its public interface. Remote services see the VPS public IPv4 address, so local ISP changes do not alter the visible address while the LinkWeaver tunnel stays established.

Masquerading does not automatically produce an “Open NAT” gaming classification. Inbound connections require explicit VPS firewall/DNAT mappings and corresponding client routing. UPnP on a home router cannot control AWS. LinkWeaver currently provides outbound NAT and session stability, not automatic per-game inbound port forwarding.

## Security boundaries

- LinkWeaver provides encryption/authentication between Windows and the VPS.
- Traffic is decrypted at the VPS before normal Internet egress unless another tunnel is added after the VPS.
- The VPS provider can observe destinations and traffic timing.
- The protocol is purpose-built and has not received an independent cryptographic or security audit.
- The relay key grants tunnel access and must remain secret.

See [SECURITY.md](../SECURITY.md) for operational guidance.

## Current limitations

- IPv4 Internet tunneling is the implemented data path; IPv6 forwarding is not complete end-to-end.
- No forward-error correction is implemented.
- No TCP-aware congestion coordination exists across the heterogeneous outer paths.
- Throughput cannot exceed the slowest bottleneck among the two ISPs, relay CPU/network, path loss/reordering, and destination.
- Bonding adds a relay RTT, encryption, UDP/IP overhead, and reordering delay.
- Stable sessions depend on the relay and its Elastic IP remaining available.
