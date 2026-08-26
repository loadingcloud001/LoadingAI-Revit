# Contributing — MIT Opensource Plugin Shelf

All code is MIT. No billing, no closed-source plugins.

## How to add a plugin

1. Fork the repo.
2. Create a folder `src/LoadingAI.Plugins.<YourPlugin>/` or add tools directly to `src/LoadingAI/Tools/AllTools.cs` for small tools (the loader treats `core` as the built-in plugin).
3. Implement `IRevitToolPlugin` (see `src/LoadingAI.Core/Plugins/IRevitToolPlugin.cs`). Prefer the revit-free `context.Bridge` for queries; implement the empty `IRevitFullAccess` marker only if you need raw `UIApplication` access:
   ```csharp
   public class HelloPlugin : IRevitToolPlugin {
     public string Id => "hello";
     public string Name => "Hello";
     public string Version => "0.1.0";
     public IEnumerable<ToolEntry> CreateTools(IPluginContext context)
       => new[] { new ToolEntry{ Definition = ..., Handler = async json => "hi", Tier = ToolTier.L1 } };
   }
   ```
   Optional: implement `IPluginLifecycle` (`IsBusy`) so the host can wait before ALC unload (Revit 2025+).
4. Add `plugin.json` next to your DLL when shipping externally:
   ```json
   {"id":"hello","name":"Hello","version":"0.1.0","license":"MIT","author":"You","entryAssembly":"LoadingAI.Plugins.Hello.dll","tools":[{"name":"hello","tier":"L1","description":"...","enabledByDefault":true}],"verified":false}
   ```
5. PR to `main` with `Plugins/README.md` entry. Maintainer reviews for Revit API correctness (no wrong-thread API, Transaction required, no hard crash). Merged plugins become `verified:true`.

## Local dev without PR

Drop `<PluginId>/plugin.json + *.dll` into `%AppData%\LoadingAI\Plugins\` (or `src/LoadingAI/Plugins/dev/` for dev build). Restart Revit. The Web `Plugins` Tab will show it as **Unverified (yellow)** — works locally, not distributed.

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
