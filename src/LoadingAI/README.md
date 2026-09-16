# LoadingAI for Revit — WebView2 Chat

In-process add-in for Autodesk Revit **2024–2027** (anchor 2027):
`DockablePane → WebView2 (Vite + React) → bridge → OpenAiCompatibleClient → AgentLoop (64 rounds) → ToolRegistry → PluginLoader → RevitBridge (ExternalEvent)`.

| Year | TFM |
|---|---|
| 2024 | net48 |
| 2025 / 2026 | net8.0-windows |
| 2027 (default) | net10.0-windows |

## Prereqs
- Revit 2024, 2025, 2026, or 2027
- .NET SDK matching the year (10 SDK can target all four)
- Node 18+ to rebuild `web/`

## Build & Install

```ps
cd web; npm install; npm run build
dotnet test src/LoadingAI.Tests/LoadingAI.Tests.csproj -c Release -p:SkipCopyAddin=true -p:SkipHostPluginBuild=true
dotnet build src/LoadingAI/LoadingAI.csproj -c Release -p:RevitVersion=2027
```

Pass `-p:RevitVersion=2024` (or 2025 / 2026) to target another year. Post-build copies DLL + `Web/dist` to `%AppData%\Autodesk\Revit\Addins\<year>\LoadingAI\` and the `.addin` next to it. The same CopyAddin run re-asserts per-user Always Load and signs the AppData DLL (`scripts/trust-addin.ps1`) so Revit does not re-prompt after a rebuild. `-p:SkipTrustAddin=true` skips that. Pack without touching AppData:

```ps
pwsh scripts/pack-addin.ps1 -RevitVersion 2024
```

## Use
1. Start Revit → open a model
2. Add-Ins → **LoadingAI** → **Open Chat**
3. Settings → provider + API key (DPAPI, `%AppData%\LoadingAI\settings.json`). Same pane toggles tools; L3 defaults off. Writes: web approve then TaskDialog.
4. File import/export is sandboxed to `%AppData%\LoadingAI\exports\`

## Layout
```
web/                         Vite + React
src/LoadingAI/
  App.cs                     IExternalApplication
  Ui/HostPage.xaml(.cs)      WPF shell (WebView2 + message switch)
  Ui/WebHostBridge.cs        WebView2 post/receive
  Ui/ChatSession.cs          agent run, tools, turn TransactionGroup
  Ui/ApprovalGate.cs         L3 confirm + chat presets
  Tools/AllTools.cs          host parse wrappers (ParseCategory/ParseEid)
  Tools/ToolGuards.cs        host-owned guards applied on load
  Plugins/Core/*             loader (ALC; LoadFrom on net48) + contract
src/LoadingAI.Core/          agent, DTO, no RevitAPI
src/LoadingAI.Tests/         xunit (Fake IRevitBridge + contracts; hosted CI)
src/LoadingAI.RevitTests/    NUnit in-Revit (local Revit 2027 / ricaun; not hosted CI)
Plugins/example.hello/       sample plugin
Plugins/core-tools/          shipped FullAccess: query/view/param/geometry + compat (63)
Plugins/export-tools/        PDF/DWG/IFC export + purge
Plugins/view-tools/          schedules, floor/3D/section views, path of travel
Plugins/modeling-tools/      load_family, change_element_type, create_topography
Plugins/structure-cuts/      split_beams_by_columns, split_floors_by_beams, split_by_levels, split_by_grids
Plugins/qs-formwork/         formwork_setup, create_formwork_panels, cleanup_formwork, copy_formwork_parent_params
```
