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
4. Add `plugin.json` next to your DLL when shipping externally. Optional `tags` (`query` / `view` / `write` / `export` / `structure` / `compat`) feed ToolSelector; omit them and the host infers from the tool name. Do not put tags on `ToolDefinition` (that JSON goes to the LLM).
   ```json
   {"id":"hello","name":"Hello","version":"0.1.0","license":"MIT","author":"You","entryAssembly":"LoadingAI.Plugins.Hello.dll","tools":[{"name":"hello","tier":"L1","description":"...","enabledByDefault":true,"tags":["query"]}],"verified":false}
   ```
5. PR to `main` with `Plugins/README.md` entry. Maintainer reviews for Revit API correctness (no wrong-thread API, Transaction required, no hard crash). Merged plugins become `verified:true`.

## Local dev without PR

Drop `<PluginId>/plugin.json + *.dll` into `%AppData%\LoadingAI\Plugins\` (or `src/LoadingAI/Plugins/dev/` for dev build). Restart Revit. **Settings** shows it as **Unverified** — works locally, not distributed.

## Rules

- L3 (delete/move/create/apply_construction_dates) must be `enabledByDefault:false`.
- All `RevitBridge.RunAsync` calls must stay on ExternalEvent; no direct `Document` access off UI thread.
- Temporary view ops (`Isolate/Hide/Override`) must be inside `Transaction` (Revit 2027 requirement).
- Keep `Tool` description concise and English.
- Shipped core surface is `Plugins/core-tools/` (FullAccess, id `core-tools`). Do not add query/view/param/geometry factories back under `src/LoadingAI/Tools`.
- ToolSelector AlwaysKeep is `get_model_summary`, `get_selection_summary`, `get_element_details`. Everything else wins on keyword/token/tag score. Cap remains 24.
- Bridge-first: L1 query and L2 view/param tools use `context.Bridge` (`IRevitQuery` / `IRevitViewCommand` / `IRevitParamCommand` / `IRevitElementCommand`). `IRevitCreateCommand` is reserved until create ops have DTOs. Do not implement `IRevitFullAccess` unless the op is on the exception list (geometry / export / family). Plugins that omit `IRevitFullAccess` must not mention `Autodesk.Revit`.
- Fail-loud: `RevitBridge.Execute` logs then rethrows. Do not add an empty `catch { }` on the drain loop.
- Turn `TransactionGroup`: the host starts one on the first L2 / L3 write in a user turn (never L1, never dry-run). Nested tool `Transaction`s fold into one undo via `Assimilate`. Empty groups and failures (`Cancelled.`, tool exception, LLM error, max-rounds abort) `RollBack`. Finish the group with `CancellationToken.None` so Stop cannot skip rollback.
- HostPage is the WPF shell. L3 gate and turn `TransactionGroup` live in `ApprovalGate` and `ChatSession`.

## Review Checklist

- [ ] MIT header, no secret keys
- [ ] csproj does not ProjectReference the host
- [ ] Tier correct (L1 read / L2 light write / L3 destructive)
- [ ] Handles `Please open a model first.` and `Please select elements first.`
- [ ] Transaction with RollBack on failure
