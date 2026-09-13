# LoadingAI free-tier gateway

The LoadingAI add-in's built-in provider ("LoadingAI", included free) talks to this gateway instead
of calling the model vendor directly. The gateway:

* holds the upstream API key in an environment file on the VM – the key is never in the add-in DLL,
  never in git, and never in a Cloudflare variable;
* allows **100 chat sends per device per UTC day**, counted in SQLite on the VM;
* signs a ticket per send, so one send can make many upstream calls (tool rounds) without spending
  more than one unit of quota.

A ticket is a signature, not a stored record: it carries `<device>.<expiry>` signed with HMAC. That
costs nothing to keep and means a long agent run never loses its ticket mid-flight.

The same request handler (`src/index.js`) runs two ways: as a Cloudflare Worker (for `wrangler dev`
and for tests) and as a plain Node process (`src/server.mjs`) on the VM. Only the transport and the
counter store differ – `src/quota.js` and the routes are shared, so behaviour cannot drift.

## Routes

| Route | Method | Notes |
| --- | --- | --- |
| `/health` | GET | liveness probe |
| `/v1/quota` | GET | `{limit, used, remaining, resetUtc}` – does not consume |
| `/v1/quota` | POST | consumes one send, returns `{..., ticket, ticketTtlSeconds}` |
| `/v1/models` | GET | answers locally, never calls upstream, no quota |
| `/v1/chat/completions` | POST | requires `x-loadingai-ticket`, proxies to upstream |

Headers the add-in sends:

* `X-LoadingAI-Device` – an anonymous GUID from `%AppData%\LoadingAI\device.json`.
* `X-LoadingAI-Ticket` – from the last successful `POST /v1/quota`. Valid 30 minutes by default.

Any `Authorization` header from the client is dropped; only the server-side key reaches the model.
Streaming responses (`stream: true`) are piped straight through, so the in-chat progress bar works.

## Production topology

```
add-in  ──https──▶  Cloudflare  ──tunnel──▶  cloudflared (VM)  ──http──▶  127.0.0.1:8787  ──https──▶  MiniMax
              gateway.loadingtechnology.app        node src/server.mjs              api.minimaxi.com
```

* `cloudflared` dials out to Cloudflare, so the VM needs **no inbound ports** and no public IP – the
  origin is reachable only through the tunnel.
* The Node process binds `127.0.0.1:8787`, so even on the VM itself other users cannot reach it.
* Cloudflare holds no secret beyond its own tunnel credential; the upstream key lives only in
  `/etc/loadingai-gateway.env` (mode `600`, root-owned).
* Cloudflare terminates TLS with an edge certificate for the zone, and the tunnel hop is encrypted.

## Deploying (maintainer, one time)

On the VM (Debian/Ubuntu, **Node 22.5 or newer** – that is when `node:sqlite` appeared):

```bash
# copy gateway/ to the VM, e.g. from the checkout on your workstation:
#   rsync -a --exclude node_modules --exclude '.wrangler' gateway/ vm:/tmp/gateway/
sudo bash /tmp/gateway/deploy/install.sh /tmp/gateway
```

`install.sh` creates the `loadingai` service account, installs the code under `/opt/loadingai-gateway`,
seeds `/etc/loadingai-gateway.env` (root-owned, mode `600`), generates `TICKET_SECRET` into it without
printing it, and enables the unit. It is idempotent, keeps an existing env file, and refuses to run on
Node older than 22.5. Invoke it as `sudo bash …` so a missing execute bit (scp from Windows) cannot
break the shebang; it also normalizes the copied tree to `755`/`644`, which scp from Windows leaves at
`0700` and the service account cannot traverse.

Then set the upstream key and restart:

```bash
sudo nano /etc/loadingai-gateway.env   # MINIMAX_API_KEY=...
sudo systemctl restart loadingai-gateway
curl -s http://127.0.0.1:8787/health   # -> {"ok":true,"service":"loadingai-gateway"}
```

Doing it by hand instead is the same five steps: `useradd --system loadingai`, copy the directory to
`/opt/loadingai-gateway`, `install -m 600 deploy/env.example /etc/loadingai-gateway.env`,
`install -m 644 deploy/loadingai-gateway.service /etc/systemd/system/`, then
`systemctl daemon-reload && systemctl enable --now loadingai-gateway`.

Then the tunnel. The quickest route is the Zero Trust dashboard, which hands you a token-managed
connector and creates the DNS record for you:

1. **Networking → Tunnels → Create a tunnel**, name it `loadingai-gateway`, choose Debian/Ubuntu.
2. **Routes → Add route → Published application**: subdomain `gateway`, domain
   `loadingtechnology.app`, service URL `http://127.0.0.1:8787`. That is exactly the ingress rule in
   `deploy/tunnel-config.json`.
3. Run the install command the dashboard shows on the VM, then start it:

```bash
sudo <the dashboard's install command>          # writes /etc/cloudflared and its own unit
sudo systemctl enable --now cloudflared
systemctl status cloudflared                    # expect "Registered tunnel connection"
```

To use our hardened unit instead, create the `cloudflared` service account and hand the connector its
token from a `600` file, so it never lands in shell history or in the process list. Read the token with
`read -rs` rather than `export TUNNEL_TOKEN=…`:

```bash
sudo useradd --system --no-create-home --home-dir /var/lib/cloudflared --shell /usr/sbin/nologin cloudflared
sudo install -d -m 755 /etc/cloudflared
read -rs TUNNEL_TOKEN && echo "TUNNEL_TOKEN=$TUNNEL_TOKEN" | sudo tee /etc/cloudflared/loadingai-tunnel.env >/dev/null
unset TUNNEL_TOKEN
sudo chmod 600 /etc/cloudflared/loadingai-tunnel.env
sudo install -m 644 /opt/loadingai-gateway/deploy/cloudflared.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now cloudflared
systemctl is-active cloudflared
```

For a locally-managed tunnel instead (`cloudflared tunnel create`, credentials JSON on the VM), put
the same ingress in `/etc/cloudflared/config.yml` — start from `deploy/config.yml.example` — route
DNS with `cloudflared tunnel route dns loadingai-gateway gateway.loadingtechnology.app`, and set
`TUNNEL_ID` in place of `TUNNEL_TOKEN`. `deploy/tunnel-config.json` is the same ingress as an API
body, for `PUT /accounts/{account_id}/cfd_tunnel/{tunnel_id}/configurations`.

The add-in reaches the gateway at the constant in `DefaultLlm.PublicGatewayBaseUrl`
(`https://gateway.loadingtechnology.app/v1`). To point a build somewhere else without editing code,
set `LOADINGAI_GATEWAY_URL` (e.g. `http://127.0.0.1:8787/v1` for local work).

## Configuration

Set in `/etc/loadingai-gateway.env` (see `deploy/env.example`):

| Name | Default | Purpose |
| --- | --- | --- |
| `MINIMAX_API_KEY` | – | upstream key. Rotate it if it was ever stored anywhere else |
| `TICKET_SECRET` | `MINIMAX_API_KEY` | set it (32 random bytes) to decouple ticket signing from the upstream key |
| `MINIMAX_BASE_URL` | `https://api.minimaxi.com/v1` | upstream base, includes `/v1` |
| `MINIMAX_MODEL` | `MiniMax-M3` | model forced on every proxied call |
| `DAILY_LIMIT` | `100` | sends per device per UTC day |
| `TICKET_TTL_SECONDS` | `1800` | how long a send's ticket stays valid |
| `HOST` | `127.0.0.1` | bind address – leave on loopback |
| `PORT` | `8787` | must match the tunnel ingress service |
| `QUOTA_DB` | `quota.db` | counter database; `/var/lib/loadingai-gateway/quota.db` in the unit |

## Local development

```powershell
cd gateway
npm test                      # 42 tests, no network, no Cloudflare account
npm start                     # Node transport on http://127.0.0.1:8787, SQLite in ./quota.db
npx wrangler dev              # optional: the Worker transport, counters in a local KV shim
```

`npm start` reads the same environment variables and warns when `MINIMAX_API_KEY` or `TICKET_SECRET`
are missing. To drive the add-in against it, set `LOADINGAI_GATEWAY_URL=http://127.0.0.1:8787/v1`
before starting Revit. `DEVICE` below stands for the value in `%AppData%\LoadingAI\device.json`.

## Verifying a deployment

```powershell
$base = 'https://gateway.loadingtechnology.app'
$device = (Get-Content "$env:AppData\LoadingAI\device.json" | ConvertFrom-Json).id

curl.exe -s "$base/health"
curl.exe -s -H "x-loadingai-device: $device" "$base/v1/quota"          # remaining, consumes nothing

$ticket = (curl.exe -s -X POST -H "x-loadingai-device: $device" "$base/v1/quota" |
           ConvertFrom-Json).ticket
curl.exe -s -X POST -H "x-loadingai-device: $device" -H "x-loadingai-ticket: $ticket" `
  -H "content-type: application/json" "$base/v1/chat/completions" `
  -d '{"messages":[{"role":"user","content":"say hi"}]}'
```

The `POST /v1/quota` above costs one unit, exactly like a chat Send. Repeat it 100 times and the
101st answers `429` with `code: "daily_limit"` and a `resetUtc` timestamp. A missing, wrong-device or
expired ticket answers `401`; a device id that is not 8–64 URL-safe characters answers `400`.

## Operations

```bash
sudo systemctl status loadingai-gateway cloudflared
journalctl -u loadingai-gateway -n 50            # one line per request, no keys and no bodies
sudo systemctl restart loadingai-gateway         # counters survive: they are in SQLite
sudo apt-get install -y sqlite3                  # not installed by default on Ubuntu 24.04
sudo sqlite3 /var/lib/loadingai-gateway/quota.db 'select key, value from kv order by key limit 20'
```

Update by re-copying the directory, then `sudo systemctl restart loadingai-gateway` – restarting never
resets anyone's counter. Back up `quota.db` (plus its `-wal` file) if you care about the current day's
counts; the data is disposable, so a lost database just hands everyone a fresh allowance.

## Limits of this design

The daily cap is an allowance check, not tamper-proof licensing: the device id is generated on the
client, so a determined user can reset it. The security win is that the upstream key is not
distributable, and that quota is enforced server-side rather than by the add-in.

SQLite replaces the earlier Workers KV store because one send costs one KV **write** and Cloudflare's
free plan allows only 1,000 writes/day (~10 active users at 100/day). One row per device per UTC day
is written here, in WAL mode, so tens of thousands of devices fit in one small file. The trades are
that the counters are ours to back up, and that the VM and the tunnel are now on the request path.

A ticket stays valid for `TICKET_TTL_SECONDS` and can accompany many upstream calls in that window,
which is what lets one Send drive several tool rounds. A user who scripts the endpoint directly could
stretch one unit that way; lower the TTL to tighten that gap.

If the VM ever becomes a bottleneck, the same `src/index.js` handler runs unchanged on Cloudflare
Workers with a Durable Object (or the paid KV plan) in place of `sqlite-kv.js`.
