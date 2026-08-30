# DualLink

DualLink is a free, native Windows 11 utility that monitors Ethernet and Wi-Fi independently, scores each connection using latency, jitter, and packet loss, and automatically gives the best healthy connection the preferred Windows route.

## What it does

- Detects active Ethernet and Wi-Fi adapters.
- Probes through each adapter's own IPv4 address.
- Scores quality using ping, jitter, and packet loss.
- Fails over immediately when the preferred link goes offline.
- Requires repeated wins before switching for quality, preventing route flapping.
- Restores Windows automatic interface metrics with one click.
- Runs in the notification area and writes local diagnostic logs.
- Uses no paid service, subscription, VPN, packet interception, or remote server.

## Honest limitation

DualLink provides failover and route optimization. It cannot merge one TCP/UDP flow or preserve the same public IP across two ISPs without a remote bonding endpoint. Separate applications and connections can still use Windows networking independently, but a single game session remains on one route.

## Install

Download `DualLink-Setup-x64.exe` from the latest private repository release or Actions artifact. Windows asks for administrator permission because changing interface metrics requires it.

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
