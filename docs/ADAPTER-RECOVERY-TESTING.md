# Adapter recovery regression checks

The Ethernet recovery fix maintains the current endpoint route's gateway, interface,
metric and prefix, rather than trusting an unchanged adapter ID or interface index.
Address/gateway changes invalidate pending tunnel verification. Failed tunnel checks
allow six seconds for roaming; physical link loss still triggers immediate path
selection. A rejected path remains available when it is the only healthy connection.

If WireGuard traffic remains stalled after settling, LinkWeaver can reapply the
existing running peer's endpoint with `wg set ... peer ... endpoint ...`. This clears
WireGuardNT's cached source/interface without restarting the adapter or replacing
keys, peers, allowed IPs or the listen port. It requires an unambiguous active peer,
uses bounded commands, and retries at most once every 15 seconds. End-to-end tunnel
verification, rather than command success, confirms recovery.

WireGuardNT's route/cache behavior is documented in its official
[routing notes](https://git.zx2c4.com/wireguard-windows/tree/docs/netquirk.md),
[endpoint update implementation](https://git.zx2c4.com/wireguard-nt/tree/driver/ioctl.c)
and [socket implementation](https://git.zx2c4.com/wireguard-nt/tree/driver/socket.c).

## Automated checks

```powershell
dotnet test tests/DualLink.Tests/DualLink.Tests.csproj -c Release
dotnet build src/DualLink.App/DualLink.App.csproj -c Release
```

The suite exercises real production route coordinators, endpoint discovery/update
code and recovery policy through fake route tables, command runners and explicit
timestamps. It covers repeated Ethernet/Wi-Fi failover and failback, DHCP gateway
and interface changes, route drift, failed route mutations, disappearing backups,
three adapters, cooldown escape, delayed tunnel verification, bounded endpoint
updates, ambiguous peer refusal and tunnel names containing spaces. The existing
protocol, relay, probe scheduling and reorder tests remain enabled.

CI runs the suite on Windows and Linux and retains TRX results. The Windows job
also builds the WPF app and runs native route, Wintun and window startup smoke checks.
Route-table fakes do not reproduce the Windows kernel or a real WireGuard driver.

## Dual-adapter acceptance checks

Use a Windows PC with two independent Internet connections. Run the existing
`scripts/Test-Continuity.ps1` in another terminal to record timestamps and exit IP.
Repeat the matrix first with WireGuard inactive, then with the prepared WireGuard
configuration active and Proton-safe mode enabled. Keep automatic route management
enabled. Adapter enable/disable operations can interrupt remote sessions, so perform
these checks locally.

| Scenario | Expected result |
| --- | --- |
| Prefer Ethernet, unplug cable | Wi-Fi becomes active; with WireGuard enabled, its service stays running. |
| Reconnect Ethernet | Confirmed Ethernet health restores the preferred path. |
| Disconnect upstream Internet while Ethernet link stays up | Independent physical probes detect failure and select working Wi-Fi. |
| Restore upstream Internet | Ethernet is eligible for confirmed recovery and failback. |
| Renew/change Ethernet address and gateway | The endpoint route uses the current gateway; old verification cannot confirm the new adapter epoch. |
| Disable Wi-Fi while recovered Ethernet is in cooldown | The only healthy Ethernet connection can be selected immediately. |
| Prefer Wi-Fi and repeat the failure/recovery sequence | Preference and fallback work in the other direction. |
| Turn WireGuard on, then off while monitoring | The selected route is reapplied in the new mode; VPN verification is invalidated. |
| Repeat failure/recovery at least 20 times | No path remains permanently rejected; routes converge after every recovery. |

Inspect `%LOCALAPPDATA%\DualLink\duallink.log` and the connection history alongside
the continuity log. A successful endpoint refresh command is followed by fresh
tunnel verification; the UI must not label an unverified tunnel as verified.
Direct failover still has detection and roaming delay. These tests do not establish
zero packet loss or guarantee third-party game/session retention. The app's managed
underlay selection and these adapter checks concern IPv4 routes.
