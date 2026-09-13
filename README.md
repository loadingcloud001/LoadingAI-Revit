# LoadingAI for Revit

In-process BIM chat add-in for Autodesk Revit 2024–2027 (anchor **2027**).

LLM → DockablePane (WebView2) → ExternalEvent / Transaction. Not a generic coding agent.

MIT. Built-in **LoadingAI** is free — no API key, up to **100 requests per day per device** — and runs through our gateway, so no upstream key is ever shipped in the add-in. Keys for other providers stay in `%AppData%\LoadingAI\settings.json` (DPAPI).

## Install (no Visual Studio)

1. Install [WebView2 Evergreen Runtime](https://developer.microsoft.com/microsoft-edge/webview2/).
2. Download the zip for your Revit year from [Releases](https://github.com/loadingcloud001/LoadingAI-Revit/releases) (`LoadingAI-Revit-2024.zip` … `LoadingAI-Revit-2027.zip`).
3. Right-click the zip → Properties → unblock if Windows marked it.
4. Open PowerShell **in the unzipped folder** and run:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\Install.ps1
```

Or copy by hand:

- `LoadingAI.addin` → `%AppData%\Autodesk\Revit\Addins\<year>\`
- `LoadingAI\` folder → `%AppData%\Autodesk\Revit\Addins\<year>\LoadingAI\`
- `Plugins\` → `%AppData%\LoadingAI\Plugins\`

5. If Revit reports **Duplicated AddInId**, delete leftover `%AppData%\Autodesk\Revit\Addins\<year>\RevitAiPoc.addin` and the `RevitAiPoc\` folder.
6. Start Revit → open a model → **Add-Ins → LoadingAI → Open Chat**.
7. Chat uses built-in **LoadingAI** (no API key, 100 requests/day per device). Other providers: Settings → paste a key. L3 tools default **off**; turn them on in the same pane. Writes: web approve, then one TaskDialog.

A "request" is one message you send; the tool calls the model makes while answering it are not counted. The remaining count shows in the chat header and in Settings, and resets at 00:00 UTC. To go unlimited, add your own key for any other provider in Settings.

## The LoadingAI gateway

The built-in provider never talks to the upstream vendor directly from your machine. It calls a small Cloudflare Worker (`gateway/`) that holds the upstream credential as a secret, checks the device's daily allowance, and streams the answer back. Requests carry a random device id (`%AppData%\LoadingAI\device.json`) and a short-lived ticket issued for that one message; no key is stored on the client and none is compiled into the add-in.

Setup (one time, maintainer only, needs a Cloudflare account):

```powershell
cd gateway
npm install
npx wrangler kv namespace create QUOTA          # paste the id into wrangler.toml
npx wrangler secret put MINIMAX_API_KEY         # type the key when prompted — never commit it
npx wrangler deploy                             # prints the public URL
```

Then set that URL in `DefaultLlm.PublicGatewayBaseUrl` (or point `LOADINGAI_GATEWAY_URL` at it for local testing) and rebuild. See [`gateway/README.md`](gateway/README.md) for routes, headers, quota rules and local dev.

Release builds contain no upstream key: packing one is an explicit, dev-only opt-in (`dotnet build ... -p:PackDefaultLlmKey=true` plus `LOADINGAI_DIRECT_MINIMAX=1`), and CI fails if a generated `DefaultLlmKey.g.cs` shows up in a release.

Releases are published when a `v*` tag is pushed (for example `v0.1.0`). You can also run **Actions → Release add-in → Run workflow** and download the artifacts.

## Build from source

```powershell
cd web; npm install; npm run build
dotnet test LoadingAI.sln -c Release -p:SkipCopyAddin=true -p:SkipHostPluginBuild=true
dotnet build src/LoadingAI/LoadingAI.csproj -c Release -p:RevitVersion=2027
```

The last command (no `SkipCopyAddin`) copies the add-in into `%AppData%\Autodesk\Revit\Addins\2027\`.

Pack a zip locally:

```powershell
pwsh scripts/pack-addin.ps1 -RevitVersion 2027   # also 2024, 2025, 2026
```

## Use

- Slash: `/ask` `/plan` `/build`. Default mode is Build.
- Tool lengths are millimetres, areas m², volumes m³.
- Do not commit `models/`, `vendor/`, `local/`, API keys, `*.rvt`.

## Repo

| Path | What |
|---|---|
| `src/LoadingAI` | Host: ribbon, WebView2, Adapter, ExternalEvent |
| `src/LoadingAI.Core` | netstandard2.0, no RevitAPI |
| `src/LoadingAI.RevitUtil` | Parse helpers for FullAccess plugins |
| `Plugins/` | Shipped plugins (Core + RevitUtil, never the host) |
| `web/` | Vite + React pane |
| `gateway/` | Cloudflare Worker that holds the upstream key and enforces the 100/day free allowance |
| `CONTRIBUTING.md` | How to add a plugin |

CI (`.github/workflows/ci.yml`) builds 2024–2027 and tests 2027.
