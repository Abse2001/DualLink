# LinkWeaver

LinkWeaver is a free Windows 11 multipath tunnel that bonds Ethernet, Wi-Fi hotspot, and USB tethering through a self-hosted Linux relay. It also preserves the original route-metric failover and Proton WireGuard preparation modes. Existing DualLink settings and relay installations remain compatible.

## Documentation

| Guide | Purpose |
| --- | --- |
| [Installation](docs/INSTALLATION.md) | Install, upgrade, verify, and uninstall the Windows application. |
| [User guide](docs/USER-GUIDE.md) | Configure adapters and use Bonding, Failover, and Redundant modes. |
| [AWS relay setup](docs/AWS-RELAY.md) | Create and secure the Ubuntu aggregation server from the AWS console. |
| [WireGuard and Proton](docs/WIREGUARD-PROTON.md) | Prepare Proton configurations and understand tunnel failover. |
| [Architecture](docs/ARCHITECTURE.md) | Protocol, scheduling, encryption, routing, NAT, liveness, and limitations. |
| [Troubleshooting](docs/TROUBLESHOOTING.md) | Diagnose no Internet, relay errors, poor speed, high latency, and Defender warnings. |
| [Development and releases](docs/DEVELOPMENT.md) | Build, test, package, sign, and publish LinkWeaver. |
| [Security policy](SECURITY.md) | Trust model, credential handling, release verification, and vulnerability reports. |

## Connectivity forensics and stable paths — 2.2.11

LinkWeaver 2.2.13 makes a manually selected preferred adapter authoritative in tunnel Failover mode for both uplink and relay downlink traffic. It recreates physical probe routes after a link down/up transition and forces WireGuard's endpoint route off the temporary backup when the preferred adapter recovers. The preferred label no longer moves to a temporary backup; the Role column distinguishes configured preference from current activity. Backup adapters still exchange small health/control probes, while Bonding mode intentionally uses every healthy path.

Selectable Aggressive, Fast, Balanced, and Stable response profiles control confirmation for Internet-only fluctuations while keeping physical disconnect response immediate. A WireGuard path is no longer reported as switched unless end-to-end tunnel Internet is verified; failed recovery attempts retain the last verified path and retry.

LinkWeaver 2.2.11 treats end-to-end reachability as authoritative when a cable
remains connected but its upstream Internet service fails. Physical probes run
concurrently using a persistent, unique target per adapter, preventing probe host-route
races, stale routes after reconnection, and false Offline/online flicker. Failed
route verification is retried until the chosen and verified paths agree. Direct-mode route changes are verified
through the selected interface, and stale relay paths expire after 750 ms. Every
actual WireGuard path change validates and moves the endpoint route while keeping
the active tunnel service alive. LinkWeaver no longer restarts WireGuard during
failover, avoiding a deliberate tunnel teardown that can end game sessions.

Bonding path numbers are now stable for each Windows adapter, so removing Ethernet
does not renumber or recreate the surviving Wi-Fi/USB path. History and CSV exports
record the physical link state, IPv4 and gateway availability, upstream reachability,
active path, WireGuard and bonding state, local/public IP changes, probe errors,
throughput, latency, and outage duration.

Adapter health uses interface-bound TCP handshakes instead of ICMP-only ping. This
prevents Ethernet from being marked Offline when a router, ISP, or VPN path blocks
ICMP while ordinary Internet traffic is still passing. Missing adapter identities
are retained for diagnosis and automatic recovery. Two attempts run concurrently
on every 75 ms monitoring cycle with a 225 ms deadline; if both fail, an upstream-only outage is acted
on in that same cycle even while Windows still reports the physical link as Up.

Tunnel paths are health-probed every 150 ms. Four missed replies mark a path unavailable in about 600 ms, and a packet that encounters a socket failure is retried immediately on the healthiest remaining path. Windows adapter-change notifications wake direct failover immediately, with a 250 ms verification cadence for upstream failures. Recovered paths are probed and automatically rejoined.

## Bonding mode

- Captures IPv4 traffic with the official signed Wintun driver.
- Sends encrypted packets over every healthy physical Internet adapter using separately interface-bound UDP sockets.
- Supports Ethernet, Wi-Fi, Android RNDIS/USB tethering, and iPhone USB adapters.
- Uses AES-256-GCM, per-session keys, sequence numbers, replay protection, and authenticated headers.
- Reassembles packets at the relay so a single TCP/UDP flow can use multiple links.
- Keeps one public VPS address while an individual physical link disappears or returns.
- Measures RTT, loss, acknowledgements, delivery rate, and recent reliability per path.
- Recreates a path automatically when tethering reconnects with a new local address.
- Provides Bonding, Failover, and Redundant modes.

The relay is a self-contained .NET 8 Linux service using `/dev/net/tun`, systemd capability restrictions, nftables forwarding, and NAT. No commercial bonding provider is involved.

## First-time relay setup

1. Prepare an Ubuntu VPS with UDP port 443 open, IPv4 forwarding enabled, source/destination checking disabled, and the provided nftables rules.
2. Install LinkWeaver and press **Setup server**.
3. Enter the VPS public IPv4 address and select its AWS `.pem` private key.
4. LinkWeaver uploads the bundled relay, creates a random 256-bit bonding key, starts the service, and stores the key encrypted with Windows DPAPI for the current user.
5. Connect one or more Internet adapters and press **Start bonding**. With one adapter it operates as an encrypted tunnel; additional adapters are bonded automatically when they become available.

The private SSH key is used only by the local Windows OpenSSH client. DualLink does not copy or store it.

## Existing failover and Proton mode

When bonding is stopped, DualLink can continue monitoring physical connections and managing ordinary Windows route metrics. **Prepare Proton config** remains available for the earlier Proton/WireGuard failover setup. Proton is optional and is not used as the bonding relay.

## Connection history

The **Connection history** tab keeps a rolling quality chart for Ethernet, Wi-Fi, and USB tethering. It marks connection drops in red and records recovery times and outage durations. Up to 24 hours of history is stored locally and remains available after restarting DualLink.

## Latency display

While bonding is active, DualLink shows direct Internet ping, encrypted relay RTT for every physical path, and end-to-end bonded Internet latency. These measurements make it clear whether delay comes from the local ISP, the route to the relay, or the complete tunnel path.

## Install and update

Download `LinkWeaver-Setup-x64.exe` from the [latest release](https://github.com/Abse2001/DualLink/releases/latest). The stable installer application ID upgrades an existing DualLink installation in place; uninstalling the previous version is not required.

Every release also includes `SHA256SUMS.txt`. Compare its installer hash with `Get-FileHash LinkWeaver-Setup-x64.exe -Algorithm SHA256` before running the installer. LinkWeaver does not invoke PowerShell with an execution-policy bypass.

Administrator permission is required to create Wintun and change routes. **Restore Windows defaults** removes DualLink-managed tunnel routes and restores automatic interface metrics.

Enable **Start with Windows** on the dashboard to install an elevated Task Scheduler logon task and launch LinkWeaver minimized after sign-in. Minimizing or closing the main window keeps bonding and monitoring active in the system tray. Double-click the tray icon, choose **Open LinkWeaver**, or click the pinned taskbar shortcut to restore the existing instance; LinkWeaver prevents duplicate running instances. Use **Exit** from the tray menu to stop the application.

Logs: `%LOCALAPPDATA%\DualLink\duallink.log`

## Development

```powershell
dotnet restore DualLink.sln
dotnet test DualLink.sln
dotnet build src/DualLink.App/DualLink.App.csproj -c Release
```

CI builds the Windows installer, verifies the pinned official Wintun archive checksum, and produces the self-contained Linux relay artifact.
