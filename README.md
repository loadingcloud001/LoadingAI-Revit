# LoadingAI for Revit

In-process BIM chat add-in for Autodesk Revit 2024–2027 (anchor **2027**).

LLM → DockablePane (WebView2) → ExternalEvent / Transaction. Not a generic coding agent.

MIT. API keys stay in `%AppData%\LoadingAI\settings.json` (DPAPI).

## Install (no Visual Studio)

1. Install [WebView2 Evergreen Runtime](https://developer.microsoft.com/microsoft-edge/webview2/).
2. Download the zip for your Revit year from [Releases](https://github.com/loadingcloud001/LoadingAI-Revit/releases) (`LoadingAI-Revit-2027.zip`, …).
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
7. **Settings** → provider + API key. L3 tools default **off**; turn them on in the same pane. Writes: web approve, then one TaskDialog.

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
pwsh scripts/pack-addin.ps1 -RevitVersion 2027
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
| `CONTRIBUTING.md` | How to add a plugin |

CI (`.github/workflows/ci.yml`) builds 2024–2027 and tests 2027.
