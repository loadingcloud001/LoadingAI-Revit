# Revit AI POC — WebView2 Chat

Single in-process Revit 2027 Add-in (`net10.0-windows`) proving:
`DockablePane → WebView2 (Vite + React) → bridge → OpenAiCompatibleClient → AgentLoop → ToolRegistry → RevitBridge (ExternalEvent)` → 1 read + 1 write on Snowdon Towers.

## Prereqs
- Revit 2027 at `F:\Program Files\Autodesk\Revit 2027\Revit.exe` (.NET 10)
- .NET 10 SDK (`dotnet --list-sdks` shows 10.0.100)
- Node 18+ for `web/` (only needed to rebuild `Web/dist`)

## Build & Install

Web (first time or after `web/src/*` changes):
```ps
cd web; npm install; npm run build   # → src/RevitAiPoc/Web/dist
```

Add-in:
```ps
dotnet build src/RevitAiPoc/RevitAiPoc.csproj -c Release
```
Post-build copies:
- `*.dll` + `Web/dist/**` → `%AppData%\Autodesk\Revit\Addins\2027\RevitAiPoc\`
- `RevitAiPoc.addin` → `%AppData%\Autodesk\Revit\Addins\2027\`

Uninstall: delete that `.addin` + `RevitAiPoc\` folder.

## Use
1. Start Revit 2027 → open `Snowdon Towers Sample Architectural.rvt`
2. Add-Ins tab → **AI POC** → **AI Chat** (dockable pane right of Project Browser, now WebView2)
3. Settings (web) → add provider (Name / Base URL / Model / API Key), Save, Use
   - Presets: DeepSeek `https://api.deepseek.com` (`deepseek-chat`), OpenAI `https://api.openai.com/v1`, Ollama `http://localhost:11434/v1`
   - Stored in `%AppData%\RevitAiPoc\settings.json` (not in repo)
4. Ask:
   - `Hello` → text reply, no tool
   - `How many walls, doors, and windows are in this model?` → trace `Tool get_model_summary` + counts (host doc only)
   - Select a wall, then `Set Comments on the selection to POC-OK` → TaskDialog confirm → transaction `Revit AI POC: set comments` → Ctrl+Z to undo
   - No model → `Please open a model first.`; no selection → `Please select elements first.`; cancel → `Cancelled by user.`

WebView2 Evergreen runtime required (Revit 2027 ships loader); if missing pane shows fallback text.

## Architecture
`HostPage.xaml (WebView2)` ↔ `PostWebMessageAsJson` bridge ↔ `web/src/App.tsx` (React + react-markdown) ↔ `Llm/AgentLoop` (max 8, system prompt locked) ↔ `ToolRegistry` (MCP-shaped cards) ↔ `RevitBridge (ExternalEvent)` → Revit API

Bridge (non-streaming): JS `{send, clear, saveSettings, requestSettings}` → C# → `{status, trace, assistant, settings, error}` → JS.

## Project Layout
```
web/                         # Vite + React 18 + react-markdown → src/RevitAiPoc/Web/dist
src/RevitAiPoc/
  App.cs                      # IExternalApplication, RegisterDockablePane → HostPage
  Ui/HostPage.xaml(.cs)       # WebView2 host + bridge
  Ui/ChatPane.xaml(.cs)       # legacy WPF (kept, unused)
  Llm/*, Tools/*, Revit/*     # unchanged backbone
  Web/dist/**                 # committed SPA (copied to Addins)
```

## Notes
- Keep: `Llm/*`, `Tools/*`, `Revit/*`, settings, bridge. Throw: `ChatPane` will be removed after WebView2 proven.
- Next: read-only `get_wall_metrics` / `get_selection_summary` / `isolate_in_view` reuse same bridge.

## Verification (same 9 steps via web transcript)
A pane docks, B hello, C wall count, D set comments, E undo, F cancel, G switch provider, H no doc, I WebView2 fallback on missing runtime — all via web chat/editor.
