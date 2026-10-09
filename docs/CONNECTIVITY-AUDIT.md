# Connectivity audit and recovery changes — 2.3.0

This audit follows both data paths: physical adapters → WireGuard/Proton, and physical adapters → encrypted LinkWeaver relay → Internet. The priority is continuity, followed by recovery consistency and a stable exit IP. A healthy spare connection must already be connected; software cannot forward through a disconnected phone or Wi-Fi network.

## Code breakdown

| Component | Responsibility | Finding and change |
| --- | --- | --- |
| `MainWindow.xaml.cs` | Monitoring, preference, route orchestration, dashboard/history | Removed synchronous multi-attempt tunnel verification from failover. Physical-change events cancel obsolete probe rounds. Active-path probes run independently of standby probes. Removed adapters lose their cached health. Route state and verified state remain distinct. |
| `NetworkService.cs` | Adapter enumeration, interface-bound probes, route ownership, verification | Replaced one destination per NIC with two independent HTTPS services per NIC. A successful destination completes the round immediately. Deadlines adapt to latency. DHCP addresses still tentative, duplicated or link-local are excluded. |
| `WindowsRouteTable.cs` | Native Windows IP Helper operations | New route layer uses Create/Set/Get/DeleteIpForwardEntry2 and GetBestRoute2. Existing routes are updated instead of removed/recreated. New gateways are installed before obsolete gateways are removed. Windows' actual winning route is verified. |
| `ConnectivityPolicy.cs` | Probe destinations, result aggregation, deadlines | Physical and tunnel destinations cannot collide. A filtered service does not invent 50% adapter packet loss. First-success probing cancels remaining attempts and disposes their sockets. |
| `TunnelRecoveryPolicy.cs` | Physical/VPN reachability disagreement | After two failed tunnel verification rounds, another recently healthy physical path can be tried. A rejected path has a 15-second retry cooldown instead of being immediately selected again by Ethernet preference. A new adapter epoch clears that cooldown. |
| `PathProbeScheduler.cs` | Independent NIC probe jobs | Keeps one job per NIC. Slow or blocked standby jobs continue separately without holding up the next active-path check. Completed old DHCP epochs are rejected and their sockets are canceled. |
| `FailoverController.cs` | Preferred adapter and automatic quality policy | Synchronizes against the successfully routed adapter, rather than retaining a controller-only choice. Clears the active connection when all paths fail. Manual preference and automatic selection use the same tested policy. |
| `PathHealthTracker` | Failure/recovery confirmation | Carrying-path failures use one failed round. Physical disconnect is immediate. Recovered paths require consecutive successful rounds and one second of uninterrupted recovery before normal preference reclaim. Address/index/gateway changes clear old health. Emergency takeover may use a fresh successful backup before recovery hold completes. |
| `WireGuardConfigService.cs`, `WireGuardConfigRewriter.cs` | Preparing Proton profiles | Preparation is idempotent, handles line endings without regex crossing configuration sections, rejects multiple peers/IPv6 endpoints, preserves one two-second keepalive, and uses /1 coverage. Preparing a different profile while a tunnel is active is blocked to protect the existing session. |
| `BondingTransport.cs` | Per-NIC UDP sockets, encrypted frames, relay telemetry | Old receive failures cannot remove replacement sockets. Authenticated relay replies can restore a path independently of a filtered public probe. Failed loss/reliability state recovers. Redundant copies track acknowledgements independently by sequence and path. Telemetry storage and sweeps are bounded. |
| `BondingEngine.cs` | Wintun capture and heartbeat/control loops | Existing independent data and 150 ms heartbeat loops remain. Mode changes now apply to a running engine. The WireGuard tunnel and relay tunnel cannot compete for ownership of the same default traffic routes. |
| `Bonding.cs` | Scheduling and packet reordering | Added timed draining of gaps. A final buffered packet no longer waits forever for another packet. Maximum reorder hold is 25 ms; Redundant mode delivers the first authenticated copy immediately after deduplication. |
| `DualLink.Relay/Program.cs` | Relay ingress, downlink path selection, UDP/TUN forwarding | Each authenticated duplicate updates its physical path's liveness, with per-path replay protection. Old control messages cannot overwrite newer settings. Added a 25 ms reorder flush loop. Redundant downlink continues copying to live physical paths. |
| `WintunDevice.cs`, `LinuxTunDevice.cs`, `RelayNetwork.cs` | Windows/Linux tunnel devices and addressing | Existing full-duplex device paths retained. Transient Wintun ring backpressure drops/logs the affected packet without terminating the relay receiver forever. Windows Wintun and Linux TUN smoke tests cover initialization/forwarding primitives. |
| `MonitorSettingsStore.cs` | Persisted policy | New atomic settings file preserves preferred NIC, response profile, automatic route management, Proton-safe mode, and bonding mode across restart. An absent preferred NIC retains its identity. First-time setup prefers Ethernet. |
| `AppLog.cs`, `ConnectionHistoryStore.cs` | Diagnostics and history | Decision logging is transition-based and rotates at 5 MB. History/chart sampling is throttled to once per second rather than rebuilding historical plots on every probe. |
| `App.xaml.cs`, `StartupService.cs` | Lifecycle, single instance, startup | Single-instance behavior retained. Added a native-route smoke mode. Closing the window no longer synchronously waits on an asynchronous bonding disposal from the UI thread. |
| `ServerProvisioner.cs`, `BondingSettingsStore.cs`, deployment files | Relay installation and credentials | Existing SSH timeouts, private temporary directory, package integrity checks, DPAPI key storage, forwarding/NAT and systemd setup retained. The new relay is bundled in the installer. |

## Failure and recovery behavior

1. Both NICs keep receiving interface-bound Internet tests. The two physical targets are `1.1.1.1:443` and `8.8.8.8:443`. They are exceptions to WireGuard's /1 routes.
2. A physical event wakes the monitor and cancels socket results from the old topology. If the carrying adapter disappeared, a same-identity backup success younger than one second can move traffic before waiting for probes.
3. For an upstream outage with the cable still connected, the carrying path's failed round is processed before slow standby work. At least one of the independent targets must answer for a new round to be considered reachable.
4. Native route changes promote the new endpoint/default path before demoting backups. The WireGuard service is never restarted or reconfigured during a handoff.
5. Tunnel verification uses `1.0.0.1:443` and `8.8.4.4:443`, bound to the tunnel's own IPv4 address and interface index. It runs separately from monitoring and cannot be satisfied by a direct physical connection.
6. The dashboard shows a routed path as pending until tunnel verification succeeds. A disconnected NIC cannot remain labeled verified. Authenticated relay reachability is shown separately when a public TCP probe is filtered.
7. Ethernet returns to preference after recovery confirmation, avoiding immediate return to a path that flickers online for one sample. A fresh backup can still take over immediately if the remaining active path fails.

Aggressive starts at a 250 ms deadline, Fast 300 ms, Balanced 350 ms, Stable 500 ms. Each adapts to recent RTT (`4 × RTT + 100 ms`, capped at 1500 ms). This replaces the 125 ms deadline that could reject a healthy 150–200 ms hotspot. These are probe deadlines, not promises of measured handoff time; OS notifications, NIC drivers, ISP latency and WireGuard roaming also contribute.

When installed, official WireGuard's `wg.exe show all endpoints` is queried asynchronously to avoid keeping an obsolete prepared endpoint after the active profile changes. It requests endpoints only, never a configuration or private-key dump. Proton clients without that CLI still require the prepared profile's registered endpoint.

## Test coverage

The 33-test suite exercises independent probe filtering, first-success cancellation, adaptive deadlines, 100 upstream outage/recovery cycles with persistent Ethernet preference, all-path loss, retry after a rejected route change, new adapter identity, disconnected-link authority, independent standby scheduling, tunnel failure cooldown, timed reorder expiry, idempotent profile preparation and native ABI layouts. An encrypted local UDP relay test removes and recreates the Wi-Fi transport ten times while public probe state is offline, requiring every reconnect to become usable from authenticated relay replies.

Windows CI runs the normal xUnit suite, builds/publishes the WPF application, and performs a native-route create/update/lookup/delete and interface-metric round trip against a benchmark-only destination. It restores the original interface policy afterward. Existing Wintun initialization, window startup, relay build and Linux TUN smoke coverage remains.

These tests cannot reproduce the user's Wi-Fi driver, ISP failure, Proton server, or GTA Online peer session in CI. Hardware validation is required before claiming seamless GTA behavior. Use `scripts/Test-Continuity.ps1` to capture timestamped end-to-end loss and public IP changes, alongside the app's history and `%LOCALAPPDATA%\DualLink\duallink.log`.

## Limits that optimization cannot eliminate

Direct WireGuard failover reacts to a failure; it cannot send through a second physical connection before detecting a silent upstream outage. Keeping the VPN exit IP stable avoids one source of session resets, but does not guarantee no packet loss or no GTA session timeout.

The existing relay **Redundant** mode continuously copies traffic over independent working connections, deduplicates at the far end and retains the relay's exit IP. It is the appropriate architecture when the goal is to survive one-path loss without waiting for failure detection. It uses roughly twice the data with two paths. It still requires a healthy backup and a reachable relay; simultaneous failures, a Wi-Fi reconnection without any other working path, or a server outage cannot be hidden.

Use either the prepared Proton/WireGuard tunnel or the relay tunnel for a session. Starting/stopping a different exit tunnel during a game changes the public IP. Upgrade the relay using **Setup server** before using the new relay-side fixes, outside an active game session.
