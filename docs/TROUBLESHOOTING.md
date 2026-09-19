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

Install 2.2.11 or later. The UI must say the replacement adapter is **verified** or list it under **Tunnel**. A green link-state icon alone is not proof of upstream Internet.

If a physical adapter was unplugged while WireGuard remained active, use 2.2.15 or later. LinkWeaver immediately moves the WireGuard endpoint route after its interface-bound backup probe succeeds, then performs slower verification. It also recreates adapter-bound probe routes on every link-state transition and explicitly moves the endpoint route back to the configured preferred adapter after recovery. Older builds could delay the endpoint move behind verification or retain the NIC's address/gateway while Windows silently removed its host route.

If Ethernet remains listed as Offline after its upstream Internet returns, use 2.2.17 or later. An upstream-only outage can leave the Windows link, address, and gateway unchanged, so older versions did not rebuild the Ethernet-specific probe route. LinkWeaver now repairs that route at a rate-limited cadence and immediately re-probes Ethernet without stopping WireGuard.

Version 2.2.19 keeps physical adapter probes separate from WireGuard tunnel verification. Earlier builds could pin the verification address to Ethernet, then repeatedly rebuild the Wi-Fi endpoint route while waiting for a verification that was accidentally following dead Ethernet. The routed endpoint now remains stable while WireGuard roams, and the dashboard shows **routed, verifying** until the tunnel is confirmed. In Aggressive mode the active path still uses a 125 ms probe deadline and one failed round. Use **Prepare Proton config** again and import the generated configuration to enable the two-second persistent keepalive.

Two real TCP attempts are bound to each adapter and run concurrently every 75 ms,
with a 225 ms timeout.
If both fail, LinkWeaver immediately classifies that adapter as `Link up — Internet
unreachable`; it does not wait for Windows to report a cable disconnect.

A manually selected preferred adapter remains preferred throughout an outage.
The backup is temporary: the first successful preferred-path probe triggers
end-to-end route verification and immediate reclamation of that adapter.

Export the history CSV after an interruption. `State` distinguishes a physically disconnected link from a link that remains up without IPv4, without a gateway, or without upstream Internet. The export also records active-path transitions, WireGuard/bonding state, public-IP changes, probe errors, throughput, latency, and recovery duration.

LinkWeaver intentionally keeps the WireGuard service running during a path switch so active sessions are not torn down. Version 2.2.16 also preserves the newly selected endpoint route while tunnel verification catches up, preventing the next monitor round from pointing WireGuard back to dead Ethernet. If Proton remains offline after the endpoint route moves, deactivate/reactivate WireGuard manually after the affected session is already lost, then inspect `%LOCALAPPDATA%\DualLink\duallink.log`.

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
