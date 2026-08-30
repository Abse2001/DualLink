# DualLink

DualLink is a free, native Windows 11 utility that monitors Ethernet and Wi-Fi independently, scores each connection using latency, jitter, and packet loss, and automatically gives the best healthy connection the preferred Windows route.

## What it does

- Detects active Ethernet and Wi-Fi adapters.
- Lets the user explicitly prefer Ethernet or Wi-Fi, with automatic backup.
- Detects an active Proton/WireGuard adapter and warns before GTA can leak onto a different public IP.
- Uses a high-contrast interface designed to remain readable on Windows display themes.
- Probes through each adapter's own IPv4 address.
- Uses protected local-gateway monitoring while Proton is connected, because a VPN kill switch correctly blocks direct internet probes from physical adapters.
- Scores quality using ping, jitter, and packet loss.
- Fails over immediately when the preferred link goes offline.
- Requires repeated wins before switching for quality, preventing route flapping.
- Restores Windows automatic interface metrics with one click.
- Runs in the notification area and writes local diagnostic logs.
- Uses no paid service, subscription, VPN, packet interception, or remote server.

## Honest limitation

DualLink provides failover and route optimization. It cannot merge one TCP/UDP flow or preserve the same public IP across two ISPs without a remote bonding endpoint. Separate applications and connections can still use Windows networking independently, but a single game session remains on one route.

While Proton is active, the displayed latency, jitter, and loss measure reachability to each adapter's local gateway rather than end-to-end internet quality. This safely detects cable, router, and hotspot loss without bypassing Proton. An upstream ISP failure where the local gateway remains reachable may only be detected when Proton reports a tunnel failure.

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
