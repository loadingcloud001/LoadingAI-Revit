# LoadingAI free-tier gateway

The LoadingAI add-in's built-in provider ("LoadingAI", included free) talks to this Worker instead of
calling the model vendor directly. The worker:

* holds the upstream API key as a Cloudflare Worker **secret** – the key is never in the add-in DLL;
* allows **100 chat sends per device per UTC day**, counted in Workers KV;
* signs a ticket per send, so one send can make many upstream calls (tool rounds) without spending
  more than one unit of quota.

A ticket is a signature, not a stored record: it carries `<device>.<expiry>` signed with HMAC. That
costs nothing to keep and means a long agent run never loses its ticket mid-flight.

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

Any `Authorization` header from the client is dropped; only the Worker secret reaches the model.
Streaming responses (`stream: true`) are piped straight through, so the in-chat progress bar works.

## One-time deployment

```powershell
cd gateway
npx wrangler login
npx wrangler kv namespace create QUOTA
#   copy the printed id into wrangler.toml -> [[kv_namespaces]].id
npx wrangler secret put MINIMAX_API_KEY   # paste the MiniMax key, never commit it
npx wrangler deploy
```

`wrangler deploy` prints the public URL. Point the add-in at it by editing
`DefaultLlm.PublicGatewayBaseUrl` in `src/LoadingAI.Core/Llm/DefaultLlm.cs`, or without rebuilding by
setting the environment variable `LOADINGAI_GATEWAY_URL` to e.g.
`https://loadingai-gateway.<your-subdomain>.workers.dev/v1`.

## Configuration

| Name | Where | Default | Purpose |
| --- | --- | --- | --- |
| `MINIMAX_API_KEY` | secret | – | upstream key, also the ticket signing secret when `TICKET_SECRET` is unset |
| `TICKET_SECRET` | secret | `MINIMAX_API_KEY` | set it to decouple ticket signing from the upstream key |
| `MINIMAX_BASE_URL` | `[vars]` | `https://api.minimaxi.com/v1` | upstream base, includes `/v1` |
| `MINIMAX_MODEL` | `[vars]` | `MiniMax-M3` | model forced on every proxied call |
| `DAILY_LIMIT` | `[vars]` | `100` | sends per device per UTC day |
| `TICKET_TTL_SECONDS` | `[vars]` | `1800` | how long a send's ticket stays valid |

## Local development

```powershell
cd gateway
npm test                      # unit tests, no network, no Cloudflare account
npx wrangler dev              # local worker on http://127.0.0.1:8787
```

To drive the add-in against the local worker, set `LOADINGAI_GATEWAY_URL=http://127.0.0.1:8787/v1`
before starting Revit. `DEVICE` below stands for the value in `%AppData%\LoadingAI\device.json`.

## Verifying a deployment

```powershell
$base = 'https://loadingai-gateway.<your-subdomain>.workers.dev'
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
101st answers `429` with `code: "daily_limit"` and a `resetUtc` timestamp.

## Limits of this design

The daily cap is an allowance check, not tamper-proof licensing: the device id is
generated on the client, so a determined user can reset it. The security win is that the upstream
key is not distributable, and that quota is enforced server-side rather than by the add-in.

KV is eventually consistent, so simultaneous sends from one device can read the same counter. The
add-in sends serially, so the practical overshoot is a unit or two. A Durable Object fixes both that
and the free-plan write cap below; switch to one when the user base outgrows KV.

A ticket stays valid for `TICKET_TTL_SECONDS` and can accompany many upstream calls in that window,
which is what lets one Send drive several tool rounds. A user who scripts the endpoint directly could
stretch one unit that way; lower the TTL to tighten that gap.

## Cloudflare plan

One send costs one KV **write** (the day counter) and the ticket costs none. Cloudflare's free plan
allows 1,000 KV writes per day, so the free plan caps the whole user base at roughly 1,000 sends/day
– about ten active users at the full 100/day allowance. The Workers Paid plan (USD 5/month) raises
that to 1,000,000 writes/day. Check the numbers before promoting the add-in widely, and watch the
KV write metric in the Cloudflare dashboard.
