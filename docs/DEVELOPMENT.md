# Development and releases

## Repository layout

| Path | Responsibility |
| --- | --- |
| `src/DualLink.App` | Windows 11 WPF application, Wintun, interface probes, routes, provisioning, UI, history. |
| `src/DualLink.Core` | Shared protocol, encryption framing, scheduler, replay and reorder logic, failover scoring. |
| `src/DualLink.Relay` | Linux UDP/TUN aggregation relay. |
| `deploy/linux` | systemd unit and standalone relay installer. |
| `installer` | Inno Setup Windows installer. |
| `tests/DualLink.Tests` | Protocol, scheduler, reorder, replay, and failover tests. |

## Local build

Install the .NET 8 SDK. On Windows:

```powershell
dotnet restore DualLink.sln
dotnet test DualLink.sln -c Release
dotnet build src/DualLink.App/DualLink.App.csproj -c Release
dotnet publish src/DualLink.App/DualLink.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish
```

The official `wintun.dll` must be placed beside `LinkWeaver.exe`. The workflow downloads Wintun 0.14.1 and verifies SHA-256 `07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51` before packaging.

Build the relay:

```bash
dotnet publish src/DualLink.Relay/DualLink.Relay.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -o relay-package
```

## Smoke tests

The Windows workflow runs:

```powershell
publish\LinkWeaver.exe --wintun-smoke
publish\LinkWeaver.exe --ui-smoke
```

The Linux workflow runs the relay's `--tun-smoke` path when `/dev/net/tun` is available. It proves the separate TUN read/write descriptors work concurrently and verifies network configuration.

## Release process

1. Update versions in `src/DualLink.App/DualLink.App.csproj` and `installer/DualLink.iss`.
2. Update the tag and release name in `.github/workflows/build.yml`.
3. Update release notes/documentation.
4. Open a pull request and require both `build` and `relay` jobs to pass.
5. Merge with the release commit message expected by the workflow, or push the version tag.
6. Confirm the release contains:
   - `LinkWeaver-Setup-x64.exe`
   - `DualLink-Relay-linux-x64.zip`
   - `SHA256SUMS.txt`
7. Verify the installer download and signature on a clean Windows 11 machine.

The workflow packages the Linux relay inside the Windows installation so **Setup server** always deploys the client-compatible relay.

## Signing

Configure repository Actions secrets:

- `DUALLINK_SIGNING_PFX`: Base64-encoded code-signing PFX.
- `DUALLINK_SIGNING_PASSWORD`: PFX password.

The workflow signs and verifies `LinkWeaver.exe` and the installer using SHA-256 and a trusted timestamp. Without these secrets it produces an explicitly unsigned build.

Never commit the PFX, password, AWS key, Proton private key, or relay key.

## Compatibility rules

- Keep the installer `AppId` stable for in-place upgrades.
- Do not change protocol version, header layout, nonce construction, or tunnel addresses without coordinated client/relay migration.
- Preserve compatibility with settings stored under `%LOCALAPPDATA%\DualLink`.
- Test one-path startup, path loss with NIC still up, adapter removal/rejoin, heterogeneous RTT, and restoration of Windows routes.
