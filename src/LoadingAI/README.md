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
dotnet test LoadingAI.sln -c Release -p:SkipCopyAddin=true -p:SkipHostPluginBuild=true
dotnet build src/LoadingAI/LoadingAI.csproj -c Release -p:RevitVersion=2027
```

Pass `-p:RevitVersion=2024` (or 2025 / 2026) to target another year. Post-build copies DLL + `Web/dist` to `%AppData%\Autodesk\Revit\Addins\<year>\LoadingAI\` and the `.addin` next to it. Pack without touching AppData:

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
  Ui/HostPage.xaml(.cs)      WebView2 host + bridge
  Tools/AllTools.cs          helpers + CreateAll
  Tools/Core/*               core query/view/param
  Tools/Compat/*             pyRevit/Dynamo-shaped tools
  Plugins/Core/*             loader (ALC; LoadFrom on net48) + contract
src/LoadingAI.Core/          agent, DTO, no RevitAPI
src/LoadingAI.Tests/         xunit
Plugins/example.hello/       sample plugin
Plugins/export-tools/        PDF/DWG/IFC export + purge
Plugins/view-tools/          schedules, floor/3D/section views, path of travel
Plugins/modeling-tools/      load_family, change_element_type, create_topography
```
