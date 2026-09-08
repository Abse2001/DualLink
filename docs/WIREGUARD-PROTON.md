# WireGuard and Proton

Proton is optional. It is not the LinkWeaver aggregation server.

## Supported arrangements

### Direct failover through Proton

```text
Windows → Proton WireGuard → Internet
          ↳ endpoint route switches between Ethernet and Wi-Fi/USB
```

This keeps the Proton server as the public egress while LinkWeaver monitors physical paths and moves the WireGuard endpoint route.

### LinkWeaver bonding

```text
Windows → LinkWeaver paths → your VPS → Internet
```

Do not activate a separate full-tunnel Proton configuration while testing LinkWeaver bonding unless that combination has been deliberately configured. Double tunneling adds latency, encryption overhead, MTU risk, and more failure points.

## Prepare a Proton configuration

1. Download a new WireGuard configuration from Proton.
2. Keep WireGuard inactive.
3. In LinkWeaver select **Prepare Proton config**.
4. Select the newly downloaded `.conf` file.
5. LinkWeaver creates a separate `-DualLink.conf` file.
6. Import the generated file into the official WireGuard application and activate that generated tunnel.
7. Enable **Proton-safe gaming mode** in LinkWeaver.

The generated configuration replaces IPv4/IPv6 default prefixes with two half-default routes. This avoids WireGuard for Windows's special `/0` kill-switch behavior while still routing Internet traffic through Proton. LinkWeaver resolves and stores the endpoint IPv4 address, then maintains explicit `/32` endpoint routes through physical gateways.

## Failover behavior in 2.2.6

When the Ethernet cable stays connected but its upstream Internet dies, LinkWeaver:

1. detects failed end-to-end probes;
2. raises the healthy adapter's Windows priority;
3. moves the Proton endpoint `/32` preference to that adapter;
4. verifies physical Internet through an interface-bound TCP socket;
5. checks that ordinary routed traffic works through WireGuard;
6. if WireGuard remains pinned to the old path, restarts the active `WireGuardTunnel$*` service;
7. verifies routed Internet again before reporting recovery.

Refreshing WireGuard can cause a short interruption. Keeping the same Proton endpoint normally keeps the same VPN egress address, but no application can guarantee a third-party VPN retains NAT/session state.

## Important limitations

- Ordinary Windows route failover without a tunnel changes the ISP public IP and may end game sessions.
- Proton does not combine multiple LinkWeaver paths for a single flow.
- A Proton endpoint outage is different from a local Ethernet outage; selecting another Proton server requires a new configuration.
- DNS in a Proton configuration may become unavailable while the tunnel is broken. LinkWeaver's health checks use numeric IP targets to avoid confusing DNS failure with path failure.
