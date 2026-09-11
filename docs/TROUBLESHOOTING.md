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

Install 2.2.9 or later and update the relay using **Setup server** once. The UI must say the replacement adapter is **verified** or list it under **Tunnel**. A green link-state icon alone is not proof of upstream Internet.

If Proton WireGuard remains offline after a switch, LinkWeaver attempts an automatic tunnel service refresh. If the status explicitly says recovery failed, deactivate/reactivate the WireGuard tunnel and inspect `%LOCALAPPDATA%\DualLink\duallink.log`.

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
