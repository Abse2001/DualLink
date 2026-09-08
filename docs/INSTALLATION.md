# Installation

## Requirements

- Windows 11 x64.
- Administrator access. LinkWeaver creates a Wintun adapter and manages routes.
- At least one working IPv4 Internet adapter.
- For bonding: a Linux VPS with a public IPv4 address and inbound UDP 443.
- For automatic server setup on AWS: the EC2 `.pem` private key and inbound SSH TCP 22.

Ethernet, Wi-Fi, Android USB/RNDIS tethering, and iPhone USB networking are detected as physical paths. Bluetooth PAN normally appears as an Ethernet-class adapter and may work, but it has much lower throughput and has not received the same validation as Ethernet, Wi-Fi, and USB tethering.

## Download and verify

1. Open the [latest release](https://github.com/Abse2001/DualLink/releases/latest).
2. Download `LinkWeaver-Setup-x64.exe` and `SHA256SUMS.txt`.
3. In PowerShell, run:

   ```powershell
   Get-FileHash "$HOME\Downloads\LinkWeaver-Setup-x64.exe" -Algorithm SHA256
   ```

4. Compare the result with the installer entry in `SHA256SUMS.txt`.
5. Run the installer and approve the administrator prompt.

The installer is intentionally multi-file and not executable-packed. A trusted Authenticode signature is present only when the release workflow has been supplied with the repository signing certificate. A matching checksum proves the download matches the GitHub release; it does not substitute for a trusted publisher signature.

Do not disable Microsoft Defender, Smart App Control, or browser protection globally. If Windows reports a detection, verify the checksum and inspect the release signature. Submit a false-positive report to Microsoft rather than creating a broad exclusion.

## Upgrade

Run the newer installer directly. The stable installer ID upgrades DualLink/LinkWeaver in place and removes the old `DualLink.exe` and old shortcuts. Settings remain under:

```text
%LOCALAPPDATA%\DualLink
```

When a release changes the Linux relay, press **Setup server** once after upgrading so the bundled relay on the VPS is updated.

## First launch

1. Start LinkWeaver as administrator.
2. Connect the adapters you want to monitor.
3. Press **Probe now**.
4. Confirm each usable adapter reports connected and has a plausible ping.
5. Complete [AWS relay setup](AWS-RELAY.md) before starting Bonding, tunnel Failover, or Redundant mode.

## Start with Windows and tray behavior

Enable **Start with Windows** to launch LinkWeaver minimized after sign-in. Closing or minimizing the window keeps the process running in the notification area. Double-click the tray icon to restore it; use **Exit** in the tray menu to fully stop LinkWeaver.

## Recovery and uninstall

Before uninstalling, stop bonding and press **Restore defaults**. This removes LinkWeaver-managed tunnel routes and restores automatic Windows interface metrics.

Logs are stored at:

```text
%LOCALAPPDATA%\DualLink\duallink.log
```
