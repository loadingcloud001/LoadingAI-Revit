# Revit AI POC — WebView2 Chat

Single in-process Revit 2027 Add-in (`net10.0-windows`):
`DockablePane → WebView2 (Vite + React) → bridge → OpenAiCompatibleClient → AgentLoop (64 rounds) → ToolRegistry → PluginLoader → RevitBridge (ExternalEvent)` → ~66 native tools.

## Prereqs
- Revit 2027
- .NET 10 SDK
- Node 18+ to rebuild `web/`

## Build & Install

```ps
cd web; npm install; npm run build
dotnet build RevitAiPoc.sln -c Release
dotnet test RevitAiPoc.sln -c Release --no-build
```

Post-build copies DLL + `Web/dist` to `%AppData%\Autodesk\Revit\Addins\2027\RevitAiPoc\` and the `.addin` next to it.

## Use
1. Start Revit 2027 → open a model
2. Add-Ins → **AI POC** → **AI Chat**
3. Settings → provider + API key (stored DPAPI-protected in `%AppData%\RevitAiPoc\settings.json`)
4. Plugins tab toggles L1/L2/L3 tools. L3 defaults off. Destructive tools confirm via TaskDialog.
5. File import/export is sandboxed to `%AppData%\RevitAiPoc\exports\`

## Layout
```
web/                         Vite + React
src/RevitAiPoc/
  App.cs                     IExternalApplication
  Ui/HostPage.xaml(.cs)      WebView2 host + bridge
  Llm/*                      client, agent, settings, history
  Tools/AllTools.cs          helpers + CreateAll
  Tools/Core/*               core query/view/param/geometry
  Tools/Compat/*             pyRevit/Dynamo-shaped tools
  Plugins/Core/*             loader (ALC) + contract
src/RevitAiPoc.Tests/        xunit
Plugins/example.hello/       sample plugin
Plugins/export-tools/        real PDF/DWG/IFC export + purge
Plugins/view-tools/          schedules, floor/3D/section views, path of travel
Plugins/modeling-tools/       load family, place instance, change type, set param, topography, phases
```
