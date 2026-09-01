# DualLink

DualLink 2 is a free Windows 11 multipath tunnel that bonds Ethernet, Wi-Fi hotspot, and USB tethering through a self-hosted Linux relay. It also preserves the original route-metric failover and Proton WireGuard preparation modes.

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
2. Install DualLink 2 and press **Setup server**.
3. Enter the VPS public IPv4 address and select its AWS `.pem` private key.
4. DualLink uploads the bundled relay, creates a random 256-bit bonding key, starts the service, and stores the key encrypted with Windows DPAPI for the current user.
5. Connect one or more Internet adapters and press **Start bonding**. With one adapter it operates as an encrypted tunnel; additional adapters are bonded automatically when they become available.

The private SSH key is used only by the local Windows OpenSSH client. DualLink does not copy or store it.

## Existing failover and Proton mode

When bonding is stopped, DualLink can continue monitoring physical connections and managing ordinary Windows route metrics. **Prepare Proton config** remains available for the earlier Proton/WireGuard failover setup. Proton is optional and is not used as the bonding relay.

## Connection history

The **Connection history** tab keeps a rolling quality chart for Ethernet, Wi-Fi, and USB tethering. It marks connection drops in red and records recovery times and outage durations. Up to 24 hours of history is stored locally and remains available after restarting DualLink.

## Latency display

While bonding is active, DualLink shows direct Internet ping, encrypted relay RTT for every physical path, and end-to-end bonded Internet latency. These measurements make it clear whether delay comes from the local ISP, the route to the relay, or the complete tunnel path.

## Install and update

Download `DualLink-Setup-x64.exe` from the latest private repository release. The stable installer application ID upgrades an existing installation in place; uninstalling the previous version is not required.

Administrator permission is required to create Wintun and change routes. **Restore Windows defaults** removes DualLink-managed tunnel routes and restores automatic interface metrics.

Logs: `%LOCALAPPDATA%\DualLink\duallink.log`

## Development

```powershell
dotnet restore DualLink.sln
dotnet test DualLink.sln
dotnet build src/DualLink.App/DualLink.App.csproj -c Release
```

CI builds the Windows installer, verifies the pinned official Wintun archive checksum, and produces the self-contained Linux relay artifact.
