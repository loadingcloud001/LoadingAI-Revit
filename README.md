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

The built-in provider never talks to the upstream vendor directly from your machine. It calls a small gateway (`gateway/`) that holds the upstream credential, checks the device's daily allowance, and streams the answer back. Requests carry a random device id (`%AppData%\LoadingAI\device.json`) and a short-lived ticket issued for that one message; no key is stored on the client and none is compiled into the add-in.

The gateway is a small Node service on our VM, reached over a Cloudflare Tunnel at `https://gateway.loadingtechnology.app/v1`. The upstream key lives only in a root-owned `600` environment file on that VM — never in git, never in a Cloudflare variable, never in the DLL. The daily counters are SQLite on the same VM, so one send costs one row rather than one Cloudflare KV write. Deploy steps are in [`gateway/README.md`](gateway/README.md); the add-in finds the default endpoint through `DefaultLlm.PublicGatewayBaseUrl`, and `LOADINGAI_GATEWAY_URL` overrides it at runtime for local testing.

Release builds contain no upstream key: packing one is an explicit, dev-only opt-in (`dotnet build ... -p:PackDefaultLlmKey=true` plus `LOADINGAI_DIRECT_MINIMAX=1`), and CI fails if a generated `DefaultLlmKey.g.cs` shows up in a release.

Releases are published when a `v*` tag is pushed (for example `v0.1.0`). You can also run **Actions → Release add-in → Run workflow** and download the artifacts.

## Build from source

```powershell
cd web; npm install; npm run build
dotnet test src/LoadingAI.Tests/LoadingAI.Tests.csproj -c Release -p:SkipCopyAddin=true -p:SkipHostPluginBuild=true
dotnet build src/LoadingAI/LoadingAI.csproj -c Release -p:RevitVersion=2027
```

The last command (no `SkipCopyAddin`) copies the add-in into `%AppData%\Autodesk\Revit\Addins\2027\` and re-asserts per-user Always Load + Authenticode on that copy so Revit does not ask you to Accept after a rebuild. That is local-dev only (`scripts/trust-addin.ps1`); CI never runs it.

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
| `gateway/` | The free-tier gateway: request handler, SQLite counters, Node transport and systemd/cloudflared templates |
| `CONTRIBUTING.md` | How to add a plugin |

CI (`.github/workflows/ci.yml`) builds 2024–2027 and tests 2027 **xUnit** (`src/LoadingAI.Tests`). In-Revit NUnit (`src/LoadingAI.RevitTests`) is local-only — see `CONTRIBUTING.md` (test pyramid) and that project's README.
