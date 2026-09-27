# Structure Split Tools (2.0.1)

Four L3 tools, all default-off, Bridge-only. Hosts are collected host-side from `scope=selection|visible|all`, so chat never has to paste hundreds of ids; `IRevitQuery.CollectHostsAsync` exposes the same page to callers that want it directly. Capped runs report `Processed N of M. nextOffset = N.` — pass that as `continueFrom`. The split algorithms live in `src/LoadingAI/Revit/StructureCuts/`; this folder holds only the handler and `plugin.json`.

| Tool | Hosts | Cutters |
|---|---|---|
| `split_beams_by_columns` | structural framing | columns (ids, else selection, else all) |
| `split_floors_by_beams` | floors / foundation slabs | beams + columns (ids, else selection, else bbox hits) |
| `split_by_levels` | walls, columns, beams, floors | storey elevations |
| `split_by_grids` | walls, columns, beams, floors | vertical planes through grids |

Shared knobs: `scope`, `continueFrom`, `dryRun`, `uniqueMark`, `copyParams`, `limit` (default 50, max 200). Naviate-style tools also take optional `elementIds`, `levelIds`/`gridIds`, and `categories[]`.

## Geometry

- Curve members: `FamilyInstance.Split` only when `CanNativeSplit` (unjoined line/arc, not grouped, not pinned). Grouped, pinned, joined, or non-line/arc members recreate the location curve instead. Native Split can abort Revit 2024–2027; SubTransaction cannot catch that, so those members never call Split. Walls recreate `LocationCurve` (no reliable `Wall.Split` on 2024–2027). Stacked and curtain walls are skipped by name. Endpoints are snapshotted before Split so the live LocationCurve is not reused after Revit invalidates it.
- Point-hosted columns get a vertical line from bbox / base–top levels so storey and grid planes still have something to cross.
- Floors reuse the 1.5 region pipeline. Grid lines are 2D cutters. A level plane skips a horizontal slab (`no-level-hit`) and cuts a leaning slab along the storey intersection.
- Each member runs in its own sub-transaction. A dry run returns before any `Transaction` is opened.

Not SolidSolidCut. Not formwork.
