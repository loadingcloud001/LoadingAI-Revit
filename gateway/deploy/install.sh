#!/usr/bin/env bash
# Installs the LoadingAI gateway on a Debian/Ubuntu VM as a systemd service.
#
#   sudo bash gateway/deploy/install.sh            # from the checkout root
#   sudo bash /tmp/gateway/deploy/install.sh /tmp/gateway
#
# Idempotent: re-running updates the code and restarts the service, but never
# overwrites /etc/loadingai-gateway.env once it exists. The upstream key and the
# ticket secret are the operator's to paste, and are never printed by this script.
set -euo pipefail

SERVICE_USER=loadingai
APP_DIR=/opt/loadingai-gateway
STATE_DIR=/var/lib/loadingai-gateway
ENV_FILE=/etc/loadingai-gateway.env
UNIT_DIR=/etc/systemd/system
SRC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if [[ $EUID -ne 0 ]]; then
  echo "run me with sudo: it creates a service account and writes /etc and /opt" >&2
  exit 1
fi

if [[ $# -ge 1 ]]; then
  SRC_DIR="$(cd "$1" && pwd)"
fi

if [[ ! -f "$SRC_DIR/src/server.mjs" ]]; then
  echo "no src/server.mjs under $SRC_DIR - point me at the gateway directory" >&2
  exit 1
fi

# node:sqlite landed in 22.5; anything older cannot run the counter store.
node_bin="$(command -v node || true)"
if [[ -z "$node_bin" ]]; then
  echo "node not found: install Node 22.5+ first (https://nodejs.org or nodesource)" >&2
  exit 1
fi
node_major="$("$node_bin" -p 'process.versions.node.split(".")[0]')"
node_minor="$("$node_bin" -p 'process.versions.node.split(".")[1]')"
if (( node_major < 22 || (node_major == 22 && node_minor < 5) )); then
  echo "node $node_major.$node_minor is too old: node:sqlite needs 22.5+" >&2
  exit 1
fi
echo "node $("$node_bin" -v) at $node_bin"

if ! id -u "$SERVICE_USER" >/dev/null 2>&1; then
  useradd --system --home "$APP_DIR" --shell /usr/sbin/nologin "$SERVICE_USER"
  echo "created user $SERVICE_USER"
fi

install -d -m 755 "$APP_DIR"
install -d -m 700 -o "$SERVICE_USER" -g "$SERVICE_USER" "$STATE_DIR"

# Code and deploy files; no secrets live in here.
cp -r "$SRC_DIR"/. "$APP_DIR"/
rm -rf "$APP_DIR/node_modules" "$APP_DIR/.wrangler"
chown -R root:root "$APP_DIR"
chmod -R go-w "$APP_DIR"

if [[ ! -f "$ENV_FILE" ]]; then
  install -m 600 -o root -g root "$APP_DIR/deploy/env.example" "$ENV_FILE"
  echo "wrote $ENV_FILE from deploy/env.example"
  echo "  -> paste MINIMAX_API_KEY and 'TICKET_SECRET=$(openssl rand -hex 32)' into it, then:"
  echo "     sudo systemctl restart loadingai-gateway"
else
  echo "kept the existing $ENV_FILE"
fi

install -m 644 "$APP_DIR/deploy/loadingai-gateway.service" "$UNIT_DIR/loadingai-gateway.service"
systemctl daemon-reload
systemctl enable --now loadingai-gateway
sleep 1
systemctl is-active --quiet loadingai-gateway ||
  { echo "the gateway did not start:" >&2; journalctl -u loadingai-gateway -n 30 --no-pager >&2; exit 1; }

if grep -q '^MINIMAX_API_KEY=.\+' "$ENV_FILE"; then
  echo "health: $(curl -fsS http://127.0.0.1:8787/health || echo 'no response')"
  echo "next: install cloudflared and route gateway.loadingtechnology.app to http://127.0.0.1:8787"
else
  echo "MINIMAX_API_KEY is still empty, so /v1/chat/completions will answer 500 until you set it"
fi
