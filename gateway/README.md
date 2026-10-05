# LoadingAI free-tier gateway

> **You do not need this to use LoadingAI.** It is the service behind the add-in's built-in free
> provider. Read on if you want to audit how that provider works, or run your own gateway for a
> team (point the add-in at it with `LOADINGAI_GATEWAY_URL`). Examples below use
> `gateway.example.com`; substitute your own hostname.

The LoadingAI add-in's built-in provider ("LoadingAI", included free) talks to this gateway instead
of calling the model vendor directly. The gateway:

* holds the upstream API key in an environment file on the VM – the key is never in the add-in DLL,
  never in git, and never in a Cloudflare variable;
* allows **100 chat sends per device per UTC day**, counted in SQLite on the VM, with a whole-service
  and a per-address ceiling on top (see *Abuse limits*);
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
| `/health` | GET | liveness probe (no add-in header) |
| `/v1/quota` | GET | `{limit, used, remaining, resetUtc}` – does not consume |
| `/v1/quota` | POST | consumes one send, returns `{..., ticket, ticketTtlSeconds}` |
| `/v1/models` | GET | answers locally, never calls upstream, no quota |
| `/v1/chat/completions` | POST | requires `x-loadingai-ticket`, proxies to upstream |

`/v1/*` requires `X-LoadingAI-Client`. Without it, casual curl callers get `401 bad_client`.
`/health` stays open for probes.

Headers the add-in sends:

* `X-LoadingAI-Client` – the add-in's identity (`loadingai-addin-v1`). **This is not a secret**: it
  is in this repository and in the add-in, so it only filters casual traffic. Real abuse limits are
  the per-device and global quotas below. Override it with `LOADINGAI_CLIENT_TOKEN` on a gateway
  you run, and in your own build of the add-in.
* `X-LoadingAI-Device` – an anonymous GUID from `%AppData%\LoadingAI\device.json`.
* `X-LoadingAI-Ticket` – from the last successful `POST /v1/quota`. Valid 2 hours by default.

Any `Authorization` header from the client is dropped; only the server-side key reaches the model.
Streaming responses (`stream: true`) are piped straight through, so the in-chat progress bar works.

## Production topology

```
add-in  ──https──▶  Cloudflare  ──tunnel──▶  cloudflared (VM)  ──http──▶  127.0.0.1:8787  ──https──▶  MiniMax
              gateway.example.com        node src/server.mjs              api.minimaxi.com
```

* `cloudflared` dials out to Cloudflare, so the VM needs **no inbound ports** and no public IP – the
  origin is reachable only through the tunnel.
* The Node process binds `127.0.0.1:8787`, so even on the VM itself other users cannot reach it.
* Cloudflare holds no secret beyond its own tunnel credential; the upstream key lives only in
  `/etc/loadingai-gateway.env` (mode `600`, root-owned).
* Cloudflare terminates TLS with an edge certificate for the zone, and the tunnel hop is encrypted.

The tunnel is remote-managed, with `CNAME gateway.example.com -> <tunnel-id>.cfargotunnel.com`
(proxied). The DNS record is the only part that matters to clients; recreating the tunnel means
updating that CNAME.

## Deploying (one time)

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

Then set the upstream key and restart. This is the only credential the gateway needs, and it is typed
at a hidden prompt so it never lands in shell history, a log, or a chat transcript:

```bash
sudo node /opt/loadingai-gateway/deploy/set-secret.mjs
#   MiniMax API key (hidden, not echoed): ****
```

`set-secret.mjs` validates the value looks like a key, writes it to `/etc/loadingai-gateway.env`
(keeping the file `600`), pins `LOADINGAI_CLIENT_TOKEN` if the file predates the add-in gate, restarts
the unit, then proves the whole path with a throwaway device id: `/health`, a `POST /v1/quota` and one
real `chat/completions`. It prints lengths and status codes, never key material, and exits non-zero if
the end-to-end probe fails. Run it again to rotate.

Status without changing anything:

```bash
sudo node /opt/loadingai-gateway/deploy/set-secret.mjs --check
#   env file    /etc/loadingai-gateway.env (mode 600)
#   unit        active
#   key         set (… chars)
#   client gate explicit
#   /health     {"ok":true,"service":"loadingai-gateway"}
#   chat probe  upstream answered: "ok"
```

Doing it by hand instead is the same five steps: `useradd --system loadingai`, copy the directory to
`/opt/loadingai-gateway`, `install -m 600 deploy/env.example /etc/loadingai-gateway.env`,
`install -m 644 deploy/loadingai-gateway.service /etc/systemd/system/`, then
`systemctl daemon-reload && systemctl enable --now loadingai-gateway`.

Then the tunnel. One script does the whole Cloudflare side — creates the tunnel, writes the remote
ingress (`deploy/tunnel-config.json`), points `gateway.example.com` at `127.0.0.1:8787`,
stores the connector token in a `600` file, installs the hardened unit and starts it:

```bash
sudo node /opt/loadingai-gateway/deploy/setup-tunnel.mjs --zone example.com --hostname gateway.example.com
#   Cloudflare API token (hidden): ****
```

Create the token at https://dash.cloudflare.com/profile/api-tokens → **Create Custom Token**:

| Resource | Scope | Permission |
| --- | --- | --- |
| Account | the account that owns your zone | **Cloudflare Tunnel:Edit** (also listed as Cloudflare One Connector: cloudflared Write) |
| Zone | your zone (for example `example.com`) | **Zone:Read**, **DNS:Edit** |

Account-level **Cloudflare Tunnel:Edit** is required — Zone:Read + DNS:Edit alone verify as `active` then fail `POST /cfd_tunnel` with `403 10000`.

The token is read from `CF_API_TOKEN`, from `--token-file`, or from a hidden prompt, and is never
echoed, logged or written to disk; only the connector token that the API returns is stored, in
`/etc/cloudflared/loadingai-tunnel.env`. The script is idempotent — it reuses an existing tunnel and
DNS record — and requires `--zone` and `--hostname`, with optional `--service` and `--tunnel`.

Doing it by hand instead: **Networking → Tunnels → Create a tunnel** named `loadingai-gateway`, add a
route for subdomain `gateway` on `example.com` to `http://127.0.0.1:8787`, then give the
connector its token from a `600` file so it never lands in shell history or the process list:

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
DNS with `cloudflared tunnel route dns loadingai-gateway gateway.example.com`, and set
`TUNNEL_ID` in place of `TUNNEL_TOKEN`. `deploy/tunnel-config.json` is the same ingress as an API
body, for `PUT /accounts/{account_id}/cfd_tunnel/{tunnel_id}/configurations`.

The add-in reaches the gateway at the constant in `DefaultLlm.PublicGatewayBaseUrl` (the project's
own hosted gateway). To point a build at yours without editing code, set `LOADINGAI_GATEWAY_URL`
(for example `https://gateway.example.com/v1`, or `http://127.0.0.1:8787/v1` for local work).

## Configuration

Set in `/etc/loadingai-gateway.env` (see `deploy/env.example`):

| Name | Default | Purpose |
| --- | --- | --- |
| `MINIMAX_API_KEY` | – | upstream key. Rotate it if it was ever stored anywhere else |
| `TICKET_SECRET` | _required_ | 32 random bytes; signs tickets. Never derived from `MINIMAX_API_KEY` |
| `MINIMAX_BASE_URL` | `https://api.minimaxi.com/v1` | upstream base, includes `/v1` |
| `MINIMAX_MODEL` | `MiniMax-M3.1-Flash-Preview` | model forced on every proxied call |
| `DAILY_LIMIT` | `100` | sends per device per UTC day |
| `GLOBAL_DAILY_LIMIT` | `3000` | sends per UTC day across all devices; `503 capacity_reached` once reached |
| `IP_DAILY_LIMIT` | `500` | sends per client address per UTC day; `503 ip_limit` once reached |
| `TICKET_TTL_SECONDS` | `7200` | how long a send's ticket stays valid |
| `MAX_OUTPUT_TOKENS` | `8192` | optional; a ceiling applied only when a client sends `max_tokens` (max `1000000`). The shipped add-in never sends it, so this does **not** bound output for the real client |
| `UPSTREAM_TIMEOUT_MS` | `100000` | optional; wait for upstream response headers before answering `504` (max `600000`). Kept below the add-in's own 120 s non-stream timeout so the 504 message reaches the user; it is also the effective ceiling for a silently thinking model on the built-in provider. Non-streamed replies only send headers after the full completion, so for them this bounds the whole reply |
| `HOST` | `127.0.0.1` | bind address – leave on loopback |
| `PORT` | `8787` | must match the tunnel ingress service |
| `QUOTA_DB` | `quota.db` | counter database; `/var/lib/loadingai-gateway/quota.db` in the unit |

## Local development

```powershell
cd gateway
npm test                      # 47 tests, no network, no Cloudflare account
npm start                     # Node transport on http://127.0.0.1:8787, SQLite in ./quota.db
npx wrangler dev              # optional: the Worker transport, counters in a local KV shim
```

`npm start` reads the same environment variables and warns when `MINIMAX_API_KEY` or `TICKET_SECRET`
are missing. Both are **required**: quota and chat refuse with `500 gateway_misconfigured`
rather than silently signing tickets with the upstream key. To drive the add-in against it, set
`LOADINGAI_GATEWAY_URL=http://127.0.0.1:8787/v1` before starting Revit. `DEVICE` below stands for
the value in `%AppData%\LoadingAI\device.json`.

## Verifying a deployment

```powershell
$base = 'https://gateway.example.com'
$device = (Get-Content "$env:AppData\LoadingAI\device.json" | ConvertFrom-Json).id
$client = 'loadingai-addin-v1'   # X-LoadingAI-Client: the add-in's compiled identity

curl.exe -s "$base/health"
# no client header -> proof the gate is live
curl.exe -s -o NUL -w '%{http_code}' "$base/v1/quota"
#   401
curl.exe -s -H "x-loadingai-device: $device" -H "x-loadingai-client: $client" "$base/v1/quota"

$ticket = (curl.exe -s -X POST -H "x-loadingai-device: $device" -H "x-loadingai-client: $client" `
           "$base/v1/quota" | ConvertFrom-Json).ticket
curl.exe -s -X POST -H "x-loadingai-device: $device" -H "x-loadingai-client: $client" `
  -H "x-loadingai-ticket: $ticket" -H "content-type: application/json" `
  "$base/v1/chat/completions" -d '{"messages":[{"role":"user","content":"say hi"}]}'
```

The `POST /v1/quota` above costs one unit, exactly like a chat Send. Repeat it 100 times and the
101st answers `429` with `code: "daily_limit"` and a `resetUtc` timestamp. A missing or wrong client
header answers `401 bad_client`; a missing, wrong-device or expired ticket answers `401`; a device id
that is not 8–64 URL-safe characters answers `400`.

## Pointing a build at your gateway

The add-in sends `X-LoadingAI-Client`, so a build older than the gate answers `401 bad_client`. If you run
your own gateway and a custom client token, rebuild the add-in with the same value in
`BuiltinQuota.ClientToken` (see the root README for build steps), or set `LOADINGAI_GATEWAY_URL` for
the stock build.

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

## Abuse limits

The device id is generated by the client, so a script can invent any number of devices and the
per-device limit alone cannot bound spend. Two ceilings that ignore the device id back it up:

* `GLOBAL_DAILY_LIMIT` — total sends per UTC day for the whole service.
* `IP_DAILY_LIMIT` — sends per client address per UTC day. The address comes from Cloudflare's
  `CF-Connecting-IP` header and is stored only as a short hash for 48 hours. It is trusted because
  the Node process listens on loopback and only the tunnel can reach it; if you expose the gateway
  directly, anyone can forge the header, so leave the per-address limit to your proxy instead.
  Without the header (a local run) only the global ceiling applies.

Both answer `503` (not `429`, which the add-in reads as "your own allowance is used up") and are
checked before the device's unit is spent, so a refusal never costs a legitimate user a send.
Counts are approximate under concurrent requests: they are ceilings, not metering.

These bound sends, not tokens: one ticket can drive many upstream calls, and the shipped add-in
sends no `max_tokens`. Also set a **spend limit on the upstream key** with your provider. That is the
only hard stop on cost.

## Limits of this design

The daily cap is an allowance check, not tamper-proof licensing: the device id is generated on the
client, so a determined user can reset it. The security win is that the upstream key is not
distributable, and that quota is enforced server-side rather than by the add-in.

SQLite replaces the earlier Workers KV store because a send is a handful of KV **writes** and Cloudflare's
free plan allows only 1,000 writes/day. Here a send updates a device row, a whole-service row and (behind the
tunnel) an address row, in WAL mode, so tens of thousands of devices fit in one small file. The trades are
that the counters are ours to back up, and that the VM and the tunnel are now on the request path.

A ticket stays valid for `TICKET_TTL_SECONDS` and can accompany many upstream calls in that window,
which is what lets one Send drive several tool rounds. A user who scripts the endpoint directly could
stretch one unit that way; lower the TTL to tighten that gap.

If the VM ever becomes a bottleneck, the same `src/index.js` handler runs unchanged on Cloudflare
Workers with a Durable Object (or the paid KV plan) in place of `sqlite-kv.js`.


