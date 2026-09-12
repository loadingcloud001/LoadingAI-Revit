# qs-formwork — Formwork QS Tools

Chat tools for formwork quantity takeoff. Port of pyRevit Formwork (v1.8.82) as a FullAccess C# plugin. Enable each L3 tool in Settings.

## Tools

| Tool | Write confirm | Notes |
|---|---|---|
| `formwork_setup` | no | Bind `FW_*` shared parameters on Generic Models and create the panel schema. Run once per project. |
| `create_formwork_panels` | yes | DirectShape panels from selected linked (or host) structural elements. 10 min timeout. |
| `cleanup_formwork` | yes | `action`: `stats`, `delete_all`, `delete_by_type` (`faceType`), `cleanup_orphans`. |
| `copy_formwork_parent_params` | no | Omit `paramNames` to list a parent-parameter catalog; pass names to copy onto `FW_*`. |

If `elementIds` is omitted, the current selection is used. Select elements first.

## Create args

- `thicknessMm` default 18
- `faceTypes` optional (`Wall_Exterior`, `Wall_Interior`, `Slab_Bottom`, `Slab_Side`, `Beam_Bottom`, `Beam_Side`, `Column_Side`, `Foundation_Bottom`, `Foundation_Side`, `Stairs_Soffit`, `Stairs_Side`)
- `collision` default true (skip ≥90% covered panels; boolean trim)
- `deleteExisting` default false (also removes obsolete FWK keys after generate)
- `includeUnselectedLinks` default false

## Geometry (first cut)

Planar extrusion with outer-loop retry, triangulation fallback, bbox + Boolean cover ratio. Full hole-smart-skip / cylindrical 250 mm facets from the pyRevit generator are deferred.

Units at the tool boundary are mm / m² / m³. Panels are Generic Model DirectShapes (`ApplicationId` `RevitAI_Formwork`).
