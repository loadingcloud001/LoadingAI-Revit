# Contributing — MIT Opensource Plugin Shelf

All code is MIT. No billing, no closed-source plugins.

Architecture freezes live in [`docs/adr/`](docs/adr/README.md). Write a **Proposed** ADR before code if you would break one. 0001 is closed; do not append to it.

- [ADR-0002](docs/adr/0002-plugin-shelf-bridge-first.md) — plugin folder, Core-only, Bridge-first, never host.
- [ADR-0003](docs/adr/0003-fullaccess-exceptions.md) — `IRevitFullAccess` is opt-in and listed. **Superseded by [ADR-0009](docs/adr/0009-port-qs-formwork-onto-bridge.md)**: the marker is gone.
- [ADR-0004](docs/adr/0004-host-owned-l3-confirm.md) — host-owned L3: web then native; presets skip the web card; dry-run skips both.
- [ADR-0005](docs/adr/0005-port-modeling-tools-onto-bridge.md) — `modeling-tools` is Bridge-only (`LoadFamily` / `ChangeElementType` / `CreateTopography`).
- [ADR-0006](docs/adr/0006-port-export-tools-onto-bridge.md) — `export-tools` is Bridge-only (`ExportPdf` / `ExportDwg` / `ExportIfc` / `PurgeUnused`).
- [ADR-0007](docs/adr/0007-port-view-tools-onto-bridge.md) — `view-tools` is Bridge-only (`GetScheduleData` / `CreateSchedule` / `CreateFloorPlan` / `Create3DView` / `CreateSectionView` / `CreatePathOfTravel`).
- [ADR-0008](docs/adr/0008-port-structure-cuts-onto-bridge.md) — `structure-cuts` is Bridge-only (`CollectHosts` / `SplitBeamsByColumns` / `SplitFloorsByBeams` / `SplitByLevels` / `SplitByGrids`).
- [ADR-0009](docs/adr/0009-port-qs-formwork-onto-bridge.md) — `qs-formwork` is Bridge-only (`FormworkSetup` / `CreateFormworkPanels` / `CleanupFormwork` / `CopyFormworkParentParams`), and `IRevitFullAccess` is retired.
- [ADR-0010](docs/adr/0010-plugin-load-allowlist.md) — `PluginLoader` refuses an unknown DLL SHA-256 **before** load. Dev folder and recorded hashes still load; `PluginMeta.Verified` is not a gate.

## How to add a plugin

1. Fork the repo.
2. Create a folder `Plugins/<your-id>/` (plugin.json + csproj + C#). Do not put plugins under `src/LoadingAI.Plugins.*`.
3. Implement `IRevitToolPlugin` (see `src/LoadingAI.Core/Plugins/IRevitToolPlugin.cs`). Use `context.Bridge` (typed DTOs, no RevitAPI). If your op is not on the Bridge yet, add a **new narrow slice** to `IRevitBridge` in the same PR — never reach for `UIApplication`.
   ```csharp
   public class HelloPlugin : IRevitToolPlugin {
     public string Id => "hello";
     public string Name => "Hello";
     public string Version => "0.1.0";
     public IEnumerable<ToolEntry> CreateTools(IPluginContext context)
       => new[] { new ToolEntry{ Definition = ..., Handler = async json => "hi", Tier = ToolTier.L1 } };
   }
   ```
   ProjectReference `src/LoadingAI.Core` — never `src/LoadingAI` (the host). That is the **only** ProjectReference a plugin may have, with `<Private>false</Private>`. Do not reference `src/LoadingAI.RevitUtil` or the Nice3point RevitAPI packages: no shipped plugin does, and ADR-0009 removed the escape hatch that used to justify it.
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

Copy `<PluginId>/plugin.json + *.dll` into `LoadingAI\Plugins\dev` next to the installed `LoadingAI.dll` — `%AppData%\Autodesk\Revit\Addins\<year>\LoadingAI\Plugins\dev`. Create that folder and copy the plugin in by hand; the build does not do it, and nothing else under `Addins\<year>\` is scanned. This folder is not hash-gated. To load from the normal runtime folder `%AppData%\LoadingAI\Plugins\` instead, record the DLL's SHA-256 in `%AppData%\LoadingAI\plugin-allowlist.json`. Restart Revit. **Settings** shows a local plugin as **Unverified** — works locally, not distributed.

An **unknown hash** in `%AppData%\LoadingAI\Plugins` is refused **before** the assembly loads (ADR-0010 stage 1): the plugin shows a **Settings** load issue and none of its tools register, so it cannot reach Confirm L3. `"verified": true` does not override that. A drop-in trust prompt is stage 2 and is not implemented yet.

## Rules

- L3 (delete/move/create/apply_construction_dates) must be `enabledByDefault:false`.
- All `RevitBridge.RunAsync` calls must stay on ExternalEvent; no direct `Document` access off UI thread.
- Temporary view ops (`Isolate/Hide/Override`) must be inside `Transaction` (Revit 2027 requirement).
- Keep `Tool` description concise and English.
- Shipped core surface is `Plugins/core-tools/` (id `core-tools`). `CoreToolsPlugin` is the single Bridge-only type (`AllTools.CreateBridge`). It has never implemented `IRevitFullAccess`. Do not add query/view/param/geometry factories back under `src/LoadingAI/Tools`.
- ToolSelector AlwaysKeep is `get_model_summary`, `get_selection_summary`, `get_element_details`. Everything else wins on keyword/token/tag score. Cap remains 24. First round uses `Select`; later rounds use `Recall` (re-score dropped tools, swap lowest non-AlwaysKeep). Do not raise `DefaultMax`.
- Bridge-first: L1 query (including QueryHealth, Worksharing `sync_info` / `audit_warnings_fix`, and IfcCost `quantity_takeoff_by_category`), L2 view/param (including color overrides, SheetDoc duplicate/template/crop, ParamExcel copy/replace/number, and Worksharing reload/assign), L3 delete/move/create, `save_document`, `load_family`, param mutate, SheetDoc create/place/dimension/tag, and ParamExcel import use `context.Bridge` (`IRevitQuery` / `IRevitViewCommand` / `IRevitParamCommand` / `IRevitElementCommand` / `IRevitCreateCommand` / `IRevitDocumentCommand`). Create tools and `place_view_on_sheet` pass millimetres; the adapter converts with `Metric.MmToFt`. CSV I/O for ParamExcel and IfcCost stays in the tool handler (`ExportPaths`); Adapter does Revit collect/set only. Family load and export are on `IRevitDocumentCommand`; `purge_unused` is on `IRevitElementCommand`; schedule reads are on `IRevitQuery` and `create_schedule` / `create_floor_plan` / `create_3d_view` / `create_section_view` / `path_of_travel` are on `IRevitViewCommand`, with the `rowLimit` clamp and the path-of-travel pair cap enforced Adapter-side. Host element collection and the four structure splits are on `IRevitQuery.CollectHostsAsync` and `IRevitCreateCommand.Split*Async`, with the split algorithms in `src/LoadingAI/Revit/StructureCuts/` and `SplitOptions` shared through `LoadingAI.Core`; the four formwork calls are on `IRevitCreateCommand.Formwork*Async`, with the algorithms in `src/LoadingAI/Revit/Formwork/`. No plugin is FullAccess (ADR-0009). Plugins must not mention `Autodesk.Revit`. See `docs/adr/0001-bridge-first-hardening.md`.
- Fail-loud: `RevitBridge.Execute` must not throw into Revit and must not drop the rest of the batch. It logs (`bridge.execute_failed` / `bridge.action_failed`) and keeps draining. The awaiter still gets the real exception because the `RunAsync` action wrapper calls `tcs.TrySetException`. Do not add an empty `catch { }` anywhere on this path.
- Turn `TransactionGroup`: the host starts one on the first L2 / L3 write in a user turn (never L1, never dry-run). Nested tool `Transaction`s fold into one undo via `Assimilate`. Empty groups and failures (`Cancelled.`, tool exception, LLM error, max-rounds abort) `RollBack`. Finish the group with `CancellationToken.None` so Stop cannot skip rollback.
- HostPage is the WPF shell. L3 gate and turn `TransactionGroup` live in `ApprovalGate` and `ChatSession`. See [ADR-0004](docs/adr/0004-host-owned-l3-confirm.md): plain L3 is web `ToolApproval` then native `Confirm L3:` (default No); presets skip the web card; `ToolPresets.IsDryRun` skips both. Plugins do not `new TaskDialog`. WebView2 kinds live in `src/LoadingAI.Core/Protocol/HostProtocol.cs`: ToHost via `ToHostMessage.TryParse`, FromHost via `FromHostMessage` records (`Serialize` uses runtime type — never `Serialize<FromHostMessage>`). Add a kind + record and a `web/src/bridge.ts` union member — do not add a new `case "foo"` in HostPage, do not `Post` anonymous `{ type = "..." }`. Quota never includes `ticket`.

## Test pyramid

Hosted GitHub Actions **cannot start Revit.exe**. Split coverage:

| Layer | Where | What it proves | Runner |
|---|---|---|---|
| L0 | `src/LoadingAI.Tests` (xUnit) | DTO roundtrips, catalog/source contracts | GitHub-hosted `windows-latest` |
| L1 | `src/LoadingAI.Tests` + `Fakes/FakeRevitBridge` | Tool handlers vs `IRevitBridge` (no-doc, empty selection, success text) | Same hosted CI |
| L2–L3 | `src/LoadingAI.RevitTests` (NUnit) | Live document, Adapter + `AttachCurrentThread`, Discover **core** tools, one rollback. Bridge-only plugins: `CreateTools` + dry-run skip reasons, then one write + `TransactionGroup.RollBack` | **Local** Revit 2027 / future self-hosted runner |

CI (`.github/workflows/ci.yml`) already runs `dotnet test src/LoadingAI.Tests/...` only. `LoadingAI.RevitTests` has `IsTestProject=false` unless `-p:ForceRevitTests=true`, so `dotnet test LoadingAI.sln` will not launch Revit.

Do **not** call `PluginLoader.Discover` from Fake tests — `ToolGuards.ApplyConfirm` shows a TaskDialog. Call `QueryTools` / `ViewTools` / `ParamTools` / `GeometryTools` / `DocumentTools` `Create*` (or `AllTools.CreateBridge`) with `FakePluginContext`.

In-Revit tests must call `RevitBridge.AttachCurrentThread(uiApp)` before awaiting Adapter/`RunAsync`. ricaun occupies the UI thread; the ExternalEvent path deadlocks there.

`src/LoadingAI.RevitTests/revit.runsettings` pins `NUnit.Version=2027` + `Open`/`Close`. ricaun finds `Revit.exe` from Autodesk registry — a non-default Program Files volume is fine. Do **not** commit a machine-specific `Revit.exe` path; `NUnit.Application` is ricaun's console, not Revit. Override with `RICAUN_REVITTEST_TESTADAPTER_NUNIT_*` env vars. Do not commit `.rvt` files.

Local CopyAddin (no `SkipCopyAddin`) also runs `scripts/trust-addin.ps1` so Revit does not ask you to Accept / Always Load after every rebuild. It writes HKCU `SOFTWARE\Autodesk\Revit\Autodesk Revit <year>\CodeSigning\<AddInId>=1` for **this** add-in only and Authenticode-signs the AppData `LoadingAI.dll` with a CurrentUser cert imported into Trusted Publishers. That is not a global security disable. Opt out with `-p:SkipTrustAddin=true`. CI already skips it via `SkipCopyAddin`.

```powershell
dotnet test src/LoadingAI.Tests/LoadingAI.Tests.csproj -c Release -p:SkipCopyAddin=true -p:SkipHostPluginBuild=true
dotnet test src/LoadingAI.RevitTests/LoadingAI.RevitTests.csproj -c Release -p:ForceRevitTests=true -p:RevitVersion=2027 -p:SkipHostPluginBuild=true --settings src/LoadingAI.RevitTests/revit.runsettings
```

## Review Checklist

- [ ] MIT header, no secret keys
- [ ] csproj does not ProjectReference the host
- [ ] Tier correct (L1 read / L2 light write / L3 destructive)
- [ ] Handles `Please open a model first.` and `Please select elements first.`
- [ ] Transaction with RollBack on failure
- [ ] Fake-bridge or in-Revit coverage for new Bridge tools (hosted xUnit first)
