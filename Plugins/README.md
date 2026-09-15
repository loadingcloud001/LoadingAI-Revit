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
- `Plugins/structure-cuts/` — `split_beams_by_columns`, `split_floors_by_beams`, `split_by_levels`, `split_by_grids` (2.0.1; L3 default off). Hosts are collected inside the plugin via `scope=selection|visible|all` so chat never has to paste hundreds of ids. See **structure-cuts** below and `Plugins/structure-cuts/README.md`.
- `Plugins/qs-formwork/` — `formwork_setup`, `create_formwork_panels`, `cleanup_formwork`, `copy_formwork_parent_params`

## structure-cuts

| Preset | Kind | Default | Effect |
|---|---|---|---|
| beams / floors / columns / elements / levels / grids | PickElements | selection | empty pick uses `scope` (selection, visible, or all) |
| `scope` | Select | selection | collect hosts from selection, the active view, or the whole model |
| `continueFrom` | Number | 0 | skip this many matching hosts so a capped run can continue (`nextOffset`) |
| `categories` | Select | all four | walls, columns, beams, floors (Naviate-style tools only) |
| `limit` | Number | 50 | caps how many input elements one run touches (clamped 1–200) |
| `dryRun` | Boolean | off | counts splits/openings without touching the model; the host skips the L3 write Confirm for it |
| `uniqueMark` | Boolean | off | keeps the original Mark on the first piece and appends `-1`, `-2`, … to the rest (blank stays blank) |
| `copyParams` | Boolean | on | copies the original's copyable instance parameters onto the pieces the re-creation fallback makes |

- Every split tool runs in an outer transaction and puts each member back if it fails: each member gets its own sub-transaction, so one host that cannot be split leaves the others alone. A write run selects the new pieces; a dry run never opens a transaction. Empty host ids use `scope` instead of forcing chat to paste hundreds of ids; a capped run reports `nextOffset` for `continueFrom`.
- `split_beams_by_columns` splits a member with Revit's own `FamilyInstance.Split` only when `CanNativeSplit` (unjoined line/arc, not grouped, not pinned). Grouped, pinned, joined, or non-line/arc members recreate the location curve instead — native Split on those is a Revit 2024–2027 process abort that SubTransaction cannot catch. Write transactions swallow warnings so one member does not abort the batch. `copyParams` only has work to do on the re-creation fallback. Cuts are taken highest first off the live location curve; endpoints are snapshotted before Split so the invalidated LocationCurve is never reused. A split that does not land where it was asked to is rolled back and that member is re-created piece by piece instead. The report says how many members each path handled and how many dependent elements the re-created ones carry.
- Beam splitting uses the real column footprint (shadow on a horizontal plane, then face, base loop, bounding box) instead of the bounding box alone — hollow columns cut on their outer ring only, and round columns are tessellated. Curved beams are split along their own curve rather than across a single chord.
- `split_floors_by_beams` only replaces floors it can rebuild: a shape edited slab, a slab with no upward planar face and a slab it cannot rebuild to match its slope are left untouched and counted under their own name — never re-created with wrong geometry. Existing floor openings are preserved, and a column footprint that falls outside the floor outline is skipped. Beam cut lines are clipped to the floor outline and nudged 1 mm past it so the cut always passes clean through.
- A single-plane sloped slab keeps its slope: each piece is built in a horizontal plane at that piece's lowest point and the tilt is carried by a slope arrow, because Revit only accepts profile loops parallel to the XY plane. After a piece is built its own face is read back and compared with the original's, and a piece that does not match is thrown away with the original left as it was.
