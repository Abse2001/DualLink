# Troubleshooting

## Bonding says established but there is no Internet

On the VPS run:

```bash
sysctl net.ipv4.ip_forward
sudo systemctl is-active nftables
sudo systemctl is-active duallink-relay
sudo ss -lunp | grep ':443 '
ip -br address show dlbond0
sudo nft list ruleset
ip route get 1.1.1.1
```

Confirm forwarding is `1`, services are active, `dlbond0` is `10.77.0.1/24`, the NAT rule covers `10.77.0.0/24`, and the nftables output interface matches the interface returned by `ip route get`.

Also confirm EC2 source/destination checking is disabled and the security group permits UDP 443 from `0.0.0.0/0`.

## The relay does not answer

Check:

- the Elastic IP in LinkWeaver belongs to the current instance;
- UDP 443 is open in the correct security group and region;
- the instance is running;
- the relay key in the Windows application matches `/etc/duallink/relay.env`;
- no other process owns UDP 443;
- the phone carrier is not blocking UDP 443.

Commands:

```bash
sudo systemctl status duallink-relay --no-pager -l
sudo journalctl -u duallink-relay -n 100 --no-pager
sudo ss -lunp | grep ':443 '
```

## Server setup times out on SSH

- Security group: allow TCP 22 from the PC's current public IP, or temporarily from anywhere while using key-only authentication.
- Verify the selected `.pem` matches the EC2 key pair.
- The expected user for the Ubuntu image is `ubuntu`.
- Confirm Windows optional feature **OpenSSH Client** is installed.
- Proton/WireGuard may change the observed source IP; temporarily stop it during server setup if the SSH allowlist does not include the VPN address.

## Ethernet Internet dies but the cable stays connected

Use 2.3.0 or later. The carrying path is removed on its first failed round, while interface-change notifications interrupt obsolete samples. Each NIC has independently bound HTTPS tests to Cloudflare and Google; a filtered destination or a 150–200 ms hotspot should not repeatedly mark working Wi-Fi offline.

Routes use native Windows calls and are updated in place. Probe bypass routes are audited every second, and DHCP address/gateway/index changes clear cached health. Tunnel verification is separate from the monitor and bound to the tunnel, so it cannot delay another failover or pass through a physical route.

The dashboard must say **verified** for a confirmed WireGuard path or list an authenticated relay path under **Tunnel**. **routed, verifying** means the physical endpoint route moved but end-to-end tunnel traffic has not yet been confirmed. A green physical probe alone does not prove the tunnel works.

For upstream-only failure, Aggressive starts at 250 ms, Fast 300 ms, Balanced 350 ms and Stable 500 ms; the deadline increases with measured latency up to 1500 ms. Physical disconnect response does not wait for these deadlines when a recently verified backup is available. Ethernet returns to preference after one second of successful recovery rather than on a single transient reply.

Keep using an already prepared profile with `PersistentKeepalive = 2`. If the profile was never prepared, deactivate it, use **Prepare Proton config**, import the generated `-DualLink.conf`, and reactivate it before joining a game. The app never restarts WireGuard during handoff.

Do not use `1.1.1.1` or `8.8.8.8` as end-to-end ping tests: these are physical probe destinations that intentionally bypass the VPN. Use `ping 208.67.222.222 -t` or the repository's [continuity capture script](../scripts/Test-Continuity.ps1). Export history after an interruption; it records link state, address/gateway, reachability, errors, route changes and observed public IP.

See [the full code audit](CONNECTIVITY-AUDIT.md) for exact changes, tests and limitations. Direct WireGuard failover has a detection/roaming interval. For continuous packet copies across both independent links, use the relay's **Redundant** mode and update the relay through **Setup server** outside an active game session. Do not run both exit tunnels concurrently.

## Failover shows traffic on both adapters

Small backup-path traffic is expected: encrypted probes run every 150 ms and control frames run every 300 ms. This does not mean payload is being bonded. Compare sustained rates during a large transfer; the backup should remain near probe overhead.

## Bonding is slower than Ethernet alone

Common causes:

- high or unequal RTT to the relay;
- cellular jitter/loss causing reordering delay;
- AWS instance CPU/network throttling or depleted burst credits;
- outer-path MTU problems;
- hotspot throttling while tethering;
- single speed-test server behavior;
- encryption and encapsulation overhead.

Test Ethernet-only, hotspot-only, and Bonding against the same test server. Watch VPS CPU with `top`, network with `ip -s link`, and relay logs. Bonding helps when both links have usable capacity; a severely lossy path can reduce single-flow performance.

## Ping is higher while bonding

All traffic travels to the VPS before reaching the destination. Minimum tunnel latency is therefore limited by the route to the VPS. Choose the lowest-RTT AWS region and prefer Failover for latency-sensitive gaming. Bonding optimizes throughput, not minimum ping.

## Internet remains broken after stopping

Press **Restore defaults**. If the application cannot open, use elevated PowerShell:

```powershell
Get-NetRoute -DestinationPrefix '0.0.0.0/1','128.0.0.0/1' -ErrorAction SilentlyContinue |
  Where-Object InterfaceAlias -eq 'DualLink Bond' |
  Remove-NetRoute -Confirm:$false

Get-NetIPInterface -AddressFamily IPv4 |
  Where-Object InterfaceAlias -notmatch 'Loopback' |
  Set-NetIPInterface -AutomaticMetric Enabled
```

Then deactivate/reactivate WireGuard if it is active.

## Defender or browser reports malware

Do not disable protection globally. Download from the official release, compare SHA-256 with `SHA256SUMS.txt`, inspect **Properties → Digital Signatures**, and submit the file to Microsoft for false-positive analysis. Unsigned network software that installs a driver, opens SSH, and changes routes is more likely to trigger heuristic detection.

## Logs and useful diagnostics

Windows log:

```text
%LOCALAPPDATA%\DualLink\duallink.log
```

VPS diagnostics:

```bash
sudo journalctl -u duallink-relay --since '10 minutes ago' --no-pager
sudo nft list ruleset
ip -s link show ens5
ip -s link show dlbond0
```

Remove private keys and relay keys before posting diagnostics publicly.
