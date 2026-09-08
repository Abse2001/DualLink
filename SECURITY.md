# LinkWeaver security

## Security model

LinkWeaver changes Windows routes, creates a Wintun adapter, and can deploy its relay over OpenSSH. These operations require administrator privileges and should only be performed from an official release whose checksum and publisher signature have been verified.

The relay key is generated locally and stored with Windows DPAPI for the current user. Relay packets are authenticated and encrypted with AES-256-GCM using per-session keys. AWS private keys are selected by the user, passed directly to Windows OpenSSH, and are never copied into DualLink settings.

Relay deployment uses key-only SSH authentication with agent forwarding and port forwarding disabled. Temporary deployment material is created in a current-user-only directory, removed locally after the operation, and removed from the relay on exit. Bundled relay files are checked against their build-time SHA-256 manifest before upload.

The relay systemd unit runs as a dedicated unprivileged user with only `CAP_NET_ADMIN` and `CAP_NET_BIND_SERVICE`, a closed device policy except for `/dev/net/tun`, a read-only system, private temporary storage, and kernel/control-group protections.

## Release integrity

GitHub Actions builds both Windows and Linux outputs from the tagged source, tests the shared protocol, and smoke-tests Wintun and Linux TUN operation. Releases include `SHA256SUMS.txt`.

Release signing is supported through the repository secrets `DUALLINK_SIGNING_PFX` and `DUALLINK_SIGNING_PASSWORD`. Official public releases should be considered publisher-verified only when Windows Properties > Digital Signatures shows a valid trusted signature.

## Reporting a vulnerability

Do not post relay keys, AWS private keys, IP addresses, or exploit details in a public issue. Use GitHub's private vulnerability reporting feature for this repository. Include the affected DualLink version and concise reproduction steps, without real credentials.

## Unsupported shortcuts

Do not disable Microsoft Defender, disable Smart App Control, add a broad antivirus exclusion, or install a release whose checksum does not match. A clean build or checksum alone is not a substitute for a trusted publisher signature.
