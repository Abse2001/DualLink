# AWS EC2 relay setup

This guide uses Ubuntu 24.04 LTS on an EC2 instance. AWS promotional credits and free-plan terms can expire; use AWS Billing and Cost Management to monitor actual charges. An Elastic IP can incur charges under current AWS pricing even when associated, so configure a budget alert.

## 1. Choose a region

Pick the region with the lowest measured latency from every connection you plan to bond. For Libya, Milan is often a better candidate than Frankfurt, but routing varies by ISP. Test rather than relying only on geographic distance.

All AWS resources below must be created in the same region.

## 2. Launch the instance

1. Open **EC2 → Instances → Launch instances**.
2. Name it `LinkWeaver-Relay`.
3. Select **Ubuntu Server 24.04 LTS x86_64**.
4. Select a small general-purpose x86_64 instance such as `t3.micro` if covered by the account's current plan or credits.
5. Create and download an RSA or ED25519 `.pem` key pair. Store it securely; LinkWeaver does not retain it.
6. Use the default VPC and a public subnet with automatic public IPv4 assignment enabled.
7. Launch the instance and wait for all status checks.

## 3. Security group

Add only these inbound rules:

| Type | Protocol | Port | Source |
| --- | --- | --- | --- |
| SSH | TCP | 22 | **My IP** when practical; temporarily `0.0.0.0/0` only if the client IP changes and key-only SSH is enforced |
| Custom UDP | UDP | 443 | `0.0.0.0/0` so both ISP addresses can reach the relay |

Outbound can remain the default allow-all rule. UDP 443 is LinkWeaver traffic, not HTTPS. Never open every inbound TCP/UDP port.

## 4. Elastic IP

1. Open **EC2 → Network & Security → Elastic IPs**.
2. Allocate an Elastic IP in the current region.
3. Select it, choose **Actions → Associate Elastic IP address**.
4. Resource type: **Instance**.
5. Select the instance from the dropdown; do not type its display name as an ID.
6. Select its primary private address and associate it.

Use this Elastic IP in LinkWeaver. If the Elastic IP changes, rerun **Setup server** with the new address.

## 5. Disable source/destination checking

The relay forwards traffic, so EC2 source/destination checking must be disabled:

1. Select the instance.
2. **Actions → Networking → Change source/destination check**.
3. Choose **Stop** or **Disable**, then save.

## 6. Verify Ubuntu

Connect with the EC2 browser terminal or Windows OpenSSH and run:

```bash
uname -m
test -c /dev/net/tun && echo 'TUN available' || echo 'TUN missing'
sudo ss -lunp | grep ':443 ' || echo 'UDP 443 available'
curl -4 https://checkip.amazonaws.com
```

Expected architecture is `x86_64`, `/dev/net/tun` must exist, and UDP 443 should be free before installation.

## 7. Forwarding and NAT

LinkWeaver's current server setup installs the relay binary and systemd unit. Configure forwarding/NAT once on the VPS:

```bash
echo 'net.ipv4.ip_forward=1' | sudo tee /etc/sysctl.d/99-duallink-forwarding.conf
sudo sysctl --system

sudo tee /etc/nftables.conf >/dev/null <<'EOF'
#!/usr/sbin/nft -f

flush ruleset

table inet duallink_filter {
    chain forward {
        type filter hook forward priority filter; policy drop;
        ct state established,related accept
        iifname "dlbond0" oifname "ens5" accept
    }
}

table ip duallink_nat {
    chain postrouting {
        type nat hook postrouting priority srcnat; policy accept;
        ip saddr 10.77.0.0/24 oifname "ens5" masquerade
    }
}
EOF

sudo nft -c -f /etc/nftables.conf
sudo systemctl enable --now nftables
```

If the public adapter is not `ens5`, obtain its name from `ip route get 1.1.1.1` and replace both `ens5` occurrences.

## 8. Install from LinkWeaver

1. Enter the Elastic IP.
2. Press **Setup server**.
3. Select the `.pem` file created for this instance.
4. Wait for **Server: Ready**.

LinkWeaver verifies its bundled relay files, uploads them using Windows OpenSSH, installs them under `/opt/duallink-relay`, writes the key to `/etc/duallink/relay.env`, and starts `duallink-relay.service`. The service runs as the unprivileged `duallink` user with only the capabilities needed for TUN and UDP 443.

## 9. Validate

```bash
sysctl net.ipv4.ip_forward
sudo systemctl is-active nftables
sudo systemctl is-active duallink-relay
sudo systemctl status duallink-relay --no-pager
sudo ss -lunp | grep ':443 '
ip -br address show dlbond0
sudo nft list ruleset
```

Expected results: forwarding `= 1`, both services `active`, UDP 443 listening, and `dlbond0` assigned `10.77.0.1/24`.

## Cost and lifecycle checklist

- Create an AWS monthly budget and billing alert.
- Stop/delete unused instances and release unused Elastic IPs.
- Keep Ubuntu security updates current.
- Restrict SSH to current trusted IP ranges where possible.
- Back up the `.pem` securely; losing it prevents automated upgrades.
