# Contributing — MIT Opensource Plugin Shelf

All code is MIT. No billing, no closed-source plugins.

## How to add a plugin

1. Fork the repo.
2. Create a folder `src/RevitAiPoc.Plugins.<YourPlugin>/` or add tools directly to `src/RevitAiPoc/Tools/AllTools.cs` for small tools (the loader treats `core` as the built-in plugin).
3. Implement `IRevitToolPlugin` (see `src/RevitAiPoc/Plugins/Core/IRevitToolPlugin.cs`):
   ```csharp
   public class HelloPlugin : IRevitToolPlugin {
     public string Id => "hello";
     public string Name => "Hello";
     public string Version => "0.1.0";
     public IEnumerable<ToolEntry> CreateTools(RevitBridge bridge, QueryCache cache)
       => new[] { new ToolEntry{ Definition = ..., Handler = async json => "hi", Tier = ToolTier.L1 } };
   }
   ```
4. Add `plugin.json` next to your DLL when shipping externally:
   ```json
   {"id":"hello","name":"Hello","version":"0.1.0","license":"MIT","author":"You","entryAssembly":"RevitAiPoc.Plugins.Hello.dll","tools":[{"name":"hello","tier":"L1","description":"...","enabledByDefault":true}],"verified":false}
   ```
5. PR to `main` with `Plugins/README.md` entry. Maintainer reviews for Revit API correctness (no wrong-thread API, Transaction required, no hard crash). Merged plugins become `verified:true`.

## Local dev without PR

Drop `<PluginId>/plugin.json + *.dll` into `%AppData%\RevitAiPoc\Plugins\` (or `src/RevitAiPoc/Plugins/dev/` for dev build). Restart Revit. The Web `Plugins` Tab will show it as **Unverified (yellow)** — works locally, not distributed.

## Rules

- L3 (delete/move/create/apply_construction_dates) must be `enabledByDefault:false`.
- All `RevitBridge.RunAsync` calls must stay on ExternalEvent; no direct `Document` access off UI thread.
- Temporary view ops (`Isolate/Hide/Override`) must be inside `Transaction` (Revit 2027 requirement).
- Keep `Tool` description concise and English.

## Review Checklist

- [ ] MIT header, no secret keys
- [ ] Tier correct (L1 read / L2 light write / L3 destructive)
- [ ] Handles `Please open a model first.` and `Please select elements first.`
- [ ] Transaction with RollBack on failure
