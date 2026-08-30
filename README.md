# DualLink

DualLink is a free, native Windows 11 utility that monitors Ethernet and Wi-Fi independently, scores each connection using latency, jitter, and packet loss, and automatically gives the best healthy connection the preferred Windows route.

## What it does

- Detects active Ethernet and Wi-Fi adapters.
- Lets the user explicitly prefer Ethernet or Wi-Fi, with automatic backup.
- Prepares a Proton WireGuard configuration for dual-path routing without reading, logging, or uploading its private key.
- Pins the WireGuard server endpoint outside the full tunnel through both Ethernet and Wi-Fi.
- Uses a high-contrast interface designed to remain readable on Windows display themes.
- Probes through each adapter's own IPv4 address.
- Uses a dedicated public ICMP health target per physical adapter while WireGuard is connected, detecting upstream ISP and cellular-call interruptions in about one second.
- Scores quality using ping, jitter, and packet loss.
- Fails over immediately when the preferred link goes offline.
- Requires repeated wins before switching for quality, preventing route flapping.
- Restores Windows automatic interface metrics with one click.
- Runs in the notification area and writes local diagnostic logs.
- Uses no paid service, subscription, VPN, packet interception, or remote server.

## Honest limitation

DualLink provides failover and route optimization. It cannot merge one TCP/UDP flow or preserve the same public IP across two ISPs without a remote bonding endpoint. Separate applications and connections can still use Windows networking independently, but a single game session remains on one route.

## Dual-path Proton setup

1. Download a fresh fixed-server WireGuard `.conf` from Proton and keep it private.
2. With WireGuard deactivated, click **Prepare Proton config** in DualLink and select that file.
3. DualLink writes a sibling `-DualLink.conf`, replaces the Windows `/0` kill-switch routes with equivalent `/1` full-tunnel routes, pins the resolved Proton endpoint through both physical gateways, and stores only the public endpoint IP for future launches.
4. Import the generated `-DualLink.conf` into the official WireGuard app and activate it while Ethernet and Wi-Fi are connected.

The private and public WireGuard keys remain only in the local configuration files. DualLink never logs or transmits them. Health monitoring sends two small ICMP probes per physical link outside the VPN each second; application and GTA traffic remain routed through WireGuard.

This mode uses WireGuard endpoint roaming and fast route changes to minimize interruption. It is experimental and cannot mathematically guarantee that every game session survives every outage.

## Install

Download `DualLink-Setup-x64.exe` from the latest private repository release or Actions artifact. Windows asks for administrator permission because changing interface metrics requires it.

To update, download and run the newer installer. Its stable application ID detects the existing installation, closes DualLink if it is running in the tray, and upgrades the same installation in place. Uninstalling the old version first is not required.

## Safety

DualLink changes only IPv4 `InterfaceMetric` and `AutomaticMetric` settings using documented Windows PowerShell networking commands. **Restore Windows defaults** re-enables automatic metrics. It does not disable the firewall or install a network driver.

Logs: `%LOCALAPPDATA%\DualLink\duallink.log`

## Development

```powershell
dotnet restore DualLink.sln
dotnet test DualLink.sln
dotnet run --project src/DualLink.App
```

The GitHub Actions workflow tests the solution, publishes a self-contained Windows x64 executable, and packages it using Inno Setup.
