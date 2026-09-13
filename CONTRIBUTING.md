# Contributing — MIT Opensource Plugin Shelf

All code is MIT. No billing, no closed-source plugins.

## How to add a plugin

1. Fork the repo.
2. Create a folder `Plugins/<your-id>/` (plugin.json + csproj + C#). Do not put plugins under `src/LoadingAI.Plugins.*`.
3. Implement `IRevitToolPlugin` (see `src/LoadingAI.Core/Plugins/IRevitToolPlugin.cs`). Prefer `context.Bridge` (typed DTOs, no RevitAPI). Implement the empty `IRevitFullAccess` marker only if you need raw `UIApplication` (geometry / export / family).
   ```csharp
   public class HelloPlugin : IRevitToolPlugin {
     public string Id => "hello";
     public string Name => "Hello";
     public string Version => "0.1.0";
     public IEnumerable<ToolEntry> CreateTools(IPluginContext context)
       => new[] { new ToolEntry{ Definition = ..., Handler = async json => "hi", Tier = ToolTier.L1 } };
   }
   ```
   ProjectReference `src/LoadingAI.Core` — never `src/LoadingAI` (the host). FullAccess plugins also reference `src/LoadingAI.RevitUtil` and Nice3point RevitAPI packages (`Private=false`, `ExcludeAssets=runtime`).
   Optional: `context.Progress.Report("Exporting", 40)` for determinate percent. The host already shows a progress bar around every tool, so this is optional.
   Optional: implement `IPluginLifecycle` (`IsBusy`) so the host can wait before ALC unload (Revit 2025+).
   Optional: set `ToolEntry.Presets` so the host shows a chat form (string / number / boolean / select / pick-elements / pick-box) before execute. Leave it empty (the default) to keep today’s path. Confirm merges form values over the LLM args; L3 still shows the native `Confirm L3:` TaskDialog. Do not call `PickObjects` yourself — declare `PresetFieldKind.PickElements` or `PickBox` and the host owns the pick.
   ```csharp
   new ToolEntry {
     Definition = ..., Handler = ..., Tier = ToolTier.L2,
     Presets = new[] {
       new PresetField { Id = "offsetMm", Label = "Offset (mm)", Kind = PresetFieldKind.Number, Required = true, Default = 20 },
       new PresetField { Id = "elementIds", Label = "Elements", Kind = PresetFieldKind.PickElements, Required = true, Category = "Walls" }
     }
   }
   ```
4. Add `plugin.json` next to your DLL when shipping externally:
   ```json
   {"id":"hello","name":"Hello","version":"0.1.0","license":"MIT","author":"You","entryAssembly":"LoadingAI.Plugins.Hello.dll","tools":[{"name":"hello","tier":"L1","description":"...","enabledByDefault":true}],"verified":false}
   ```
5. PR to `main` with `Plugins/README.md` entry. Maintainer reviews for Revit API correctness (no wrong-thread API, Transaction required, no hard crash). Merged plugins become `verified:true`.

## Local dev without PR

Drop `<PluginId>/plugin.json + *.dll` into `%AppData%\LoadingAI\Plugins\` (or `src/LoadingAI/Plugins/dev/` for dev build). Restart Revit. **Settings** shows it as **Unverified** — works locally, not distributed.

## Rules

- L3 (delete/move/create/apply_construction_dates) must be `enabledByDefault:false`.
- All `RevitBridge.RunAsync` calls must stay on ExternalEvent; no direct `Document` access off UI thread.
- Temporary view ops (`Isolate/Hide/Override`) must be inside `Transaction` (Revit 2027 requirement).
- Keep `Tool` description concise and English.
- Bridge-first: L1 query and L2 view/param tools use `context.Bridge`. Do not call `FullAccess` unless the op is on the exception list (geometry / export / family).

## Review Checklist

- [ ] MIT header, no secret keys
- [ ] csproj does not ProjectReference the host
- [ ] Tier correct (L1 read / L2 light write / L3 destructive)
- [ ] Handles `Please open a model first.` and `Please select elements first.`
- [ ] Transaction with RollBack on failure
