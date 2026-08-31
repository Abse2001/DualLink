#!/usr/bin/env bash
set -euo pipefail

if [[ ${EUID} -ne 0 ]]; then
  echo "Run this installer with sudo." >&2
  exit 1
fi

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
binary_path=${1:-"${script_dir}/DualLink.Relay"}
if [[ ! -f ${binary_path} ]]; then
  echo "Relay binary not found: ${binary_path}" >&2
  exit 1
fi

if ! getent group duallink >/dev/null; then
  groupadd --system duallink
fi
if ! id duallink >/dev/null 2>&1; then
  useradd --system --gid duallink --home-dir /var/lib/duallink --create-home --shell /usr/sbin/nologin duallink
fi

install -d -o root -g root -m 0755 /opt/duallink-relay
install -o root -g root -m 0755 "${binary_path}" /opt/duallink-relay/DualLink.Relay
install -d -o root -g duallink -m 0750 /etc/duallink

if [[ ! -f /etc/duallink/relay.env ]]; then
  relay_key=$(openssl rand -base64 32)
  printf 'DUALLINK_KEY=%s\nDUALLINK_PORT=443\n' "${relay_key}" > /etc/duallink/relay.env
  chown root:duallink /etc/duallink/relay.env
  chmod 0640 /etc/duallink/relay.env
fi

install -o root -g root -m 0644 "${script_dir}/duallink-relay.service" /etc/systemd/system/duallink-relay.service
systemctl daemon-reload
systemctl enable duallink-relay.service

echo "DualLink relay installed but not started."
echo "The Windows client must be configured before starting it."
