# Plugins — Opensource (MIT)

This folder documents the runtime plugin directory, not the repo source.

Runtime location: `%AppData%\LoadingAI\Plugins\<pluginId>\plugin.json + *.dll`
Dev location (optional, copied on build if exists): `src/LoadingAI/Plugins/dev/<pluginId>/`

Each plugin folder example:

```
Plugins/hello/
  plugin.json
  LoadingAI.Plugins.Hello.dll
```

`plugin.json` schema:

```json
{
  "id": "hello",
  "name": "Hello",
  "version": "0.1.0",
  "license": "MIT",
  "author": "Community",
  "entryAssembly": "LoadingAI.Plugins.Hello.dll",
  "tools": [
    {"name":"hello","tier":"L1","description":"Says hi","enabledByDefault":true}
  ],
  "verified": false
}
```

- `verified:true` = merged via PR and reviewed.
- `verified:false` = local drop, shown in **Settings** as unverified.
- Enabling/disabling is per-tool in `%AppData%\LoadingAI\settings.json` `plugins:{toolName:bool}`; toggle in Web requires no restart, adding/removing DLL requires restart (AppDomain lock).
- csproj must ProjectReference `LoadingAI.Core` (and `LoadingAI.RevitUtil` if FullAccess). Never reference the host `LoadingAI.csproj`.

See `src/LoadingAI.Core/Plugins/IRevitToolPlugin.cs` and `Plugins/example.hello/` for a minimal Bridge-only example.

Optional **presets**: set `ToolEntry.Presets` to show a chat form before execute (select / number / pick-elements / pick-box). Empty (default) keeps the current L1/L2/L3 path — existing plugins need no change. Pick is host-owned; do not add pick APIs to `IRevitBridge` or call `Selection.PickObjects` from the plugin. Confirm merges form values over AI args; L3 still shows the native TaskDialog.

Shipped plugins (host build copies each to `%AppData%\LoadingAI\Plugins\<id>\`):
- `Plugins/export-tools/` — `export_pdf`, `export_dwg`, `export_ifc`, `purge_unused`
- `Plugins/view-tools/` — `create_schedule`, `get_schedule_data`, `create_floor_plan`, `create_3d_view`, `create_section_view`, `path_of_travel`
- `Plugins/modeling-tools/` — `load_family`, `change_element_type`, `create_topography`
- `Plugins/structure-cuts/` — `split_beams_by_columns`, `split_floors_by_beams` (1.4.0; optional chat presets pick beams/floors/columns, empty pick keeps the selection fallback). See **structure-cuts** below.
- `Plugins/qs-formwork/` — `formwork_setup`, `create_formwork_panels`, `cleanup_formwork`, `copy_formwork_parent_params`

## structure-cuts

| Preset | Kind | Default | Effect |
|---|---|---|---|
| beams / floors / columns | PickElements | selection | empty pick keeps the selection fallback |
| `limit` | Number | 50 | caps how many input elements one run touches (clamped 1–200) |
| `dryRun` | Boolean | off | counts splits/openings without touching the model; the host skips the L3 write Confirm for it |
| `uniqueMark` | Boolean | off | keeps the original Mark on the first piece and appends `-1`, `-2`, … to the rest (blank stays blank) |
| `copyParams` | Boolean | on | copies the original's copyable instance parameters to every piece |

- Both tools run in a single transaction, create every piece before deleting the original, and roll back on failure. A write run selects the new pieces; a dry run never changes the selection.
- Beam splitting uses the real column footprint (shadow on a horizontal plane, then face, base loop, bounding box) instead of the bounding box alone — hollow columns cut on their outer ring only, and round columns are tessellated. Curved beams are split along their tessellated centreline.
- `split_floors_by_beams` only replaces floors it can rebuild: a floor whose solid has no upward horizontal planar face is reported as `unsupported` and left untouched, never re-created with wrong geometry. Existing floor openings are preserved, and a column footprint that falls outside the floor outline is skipped. Beam cut lines are clipped to the floor outline and nudged 1 mm past it so the cut always passes clean through.
- Floor profile loops must be planar and parallel to the XY plane (a Revit `Floor.Create` restriction), which is what the `unsupported` gate enforces.
