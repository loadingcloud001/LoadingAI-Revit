# ADR-0003 — FullAccess is opt-in and listed

- Status: **Superseded by [ADR-0009](0009-port-qs-formwork-onto-bridge.md)**
- Date: 2026-09-18
- Deciders: LoadingAI architecture
- Extends: [ADR-0002](0002-plugin-shelf-bridge-first.md)
- Does not supersede: [ADR-0001](0001-bridge-first-hardening.md)

> **Superseded.** ADR-0005 through ADR-0009 moved every listed row onto Bridge. The table
> below is empty, and ADR-0009 deleted `IRevitFullAccess` and its support types outright.
> This file is kept for the reasoning, not as a live rule. See ADR-0009 for the current rule:
> plugins are Bridge-only, and there is no opt-in path to `UIApplication`.

## Context

ADR-0001 made `core-tools` Bridge-only. Other shipped plugins still need Autodesk types the Bridge does not expose: schedules/views, formwork solids, beam/floor cuts. `modeling-tools` moved onto Bridge in [ADR-0005](0005-port-modeling-tools-onto-bridge.md); `export-tools` moved onto Bridge in [ADR-0006](0006-port-export-tools-onto-bridge.md); `view-tools` moved onto Bridge in [ADR-0007](0007-port-view-tools-onto-bridge.md); `structure-cuts` moved onto Bridge in [ADR-0008](0008-port-structure-cuts-onto-bridge.md); `qs-formwork` moved onto Bridge in [ADR-0009](0009-port-qs-formwork-onto-bridge.md), which supersedes this ADR and removes the marker.

`IRevitFullAccess` is an **empty marker**. The host (`PluginLoader`) sets `requestFull = typeof(IRevitFullAccess).IsAssignableFrom(pt)` and only then supplies `IPluginContext.FullAccess` (`IRawRevitAccess`). `FakePluginContext.FullAccess` is always null, so FullAccess handlers cannot run in hosted CI.

If FullAccess is treated as the default, every new plugin gets `UIApplication` and Core’s “no RevitAPI” rule dies.

## Options considered

1. **Empty marker + listed exceptions (chosen)** — plugin class implements `IRevitFullAccess`; csproj adds RevitUtil + Nice3point (`Private=false`, `ExcludeAssets=runtime`). Host still wraps work in `RunRevitAsync` / ExternalEvent.
2. **Grow `IRevitBridge` until FullAccess is gone** — right long-term for each op that has a stable DTO; wrong as a blanket. Export/schedules/formwork/cuts are not DTO-ready. Migrating them is a **new ADR per plugin**, not a silent Bridge dump.
3. **Give every plugin raw access** — rejected in ADR-0001 P2. Query tools must not receive `UIApplication`.

## Decision

```mermaid
flowchart LR
  PT[Plugin type]
  PT -->|implements IRevitFullAccess| FA[FullAccess non-null]
  PT -->|omits marker| BR[Bridge only, FullAccess null]
  FA --> RAW[IRawRevitAccess.RunRevitAsync]
  RAW --> UI[UIApplication on Revit thread]
  BR --> DTO[IRevitBridge DTOs]
```

**Listed FullAccess plugins (today):**

| Plugin id | Why FullAccess |
|---|---|
| _(none)_ | the last row, `qs-formwork`, moved onto Bridge in ADR-0009 |

**Rules for those plugins**

- Implement `IRevitFullAccess` on the plugin class (the marker), not on each tool.
- Reference `LoadingAI.RevitUtil` + Nice3point RevitAPI. Still **never** reference the host csproj.
- Model work runs through `context.FullAccess.RunRevitAsync` (Revit UI thread). No `Document` access off-thread.
- Writes still go through host L2/L3 gates. Dry-run skips the L3 TaskDialog. Plugins do not show their own confirm for L3.
- Hosted tests cover `CreateTools` shape + dry-run skip reasons only. Live writes stay in `LoadingAI.RevitTests` (local Revit 2027).

**Not FullAccess**

- `core-tools`, `example.hello`, `modeling-tools`, `export-tools`, `view-tools`, `structure-cuts`, `qs-formwork`, and any new plugin that can be expressed with existing Bridge slices.
- With the table empty there is no plugin left to list, and no `requestFull` in `PluginLoader` to keep: ADR-0009 deleted the marker, `IPluginContext.FullAccess`, `IRawRevitAccess` and `FullAccessSession`. Do not re-add them.

Moving a row in the table onto Bridge is a **new ADR** (0004+), one plugin (or one DTO slice) at a time, with FakeBridge tests before deleting the marker.

## Consequences

- `NonFullAccessPlugins_DoNotMentionAutodeskRevit` skips a folder only if some `.cs` contains `IRevitFullAccess`. Removing the marker from a listed plugin without a Bridge port **fails CI**. ADR-0009 renamed it to `ShippedPlugins_DoNotMentionAutodeskRevit` and deleted the skip, because with zero listed plugins the guard made the test pass vacuously.
- `ShippedPlugin_DoesNotReferenceHost(..., requiresRevitUtil: true)` locked RevitUtil on the one listed plugin. That count is now **zero**: no shipped plugin references `LoadingAI.RevitUtil`. The parameter and its branch are kept as a forward guard, so a future plugin that genuinely needs RevitUtil can still declare it.
- Fake tests must not call FullAccess factories; `FullAccess` is null. ADR-0009 deleted the property, so there is no factory to call.

## Do not

- Do not add FullAccess tools to `core-tools`.
- Do not implement `IRevitFullAccess` on a new plugin: the marker no longer exists (ADR-0009). A plugin that needs a new Revit operation gets a **new Bridge slice**, not a raw `UIApplication` escape hatch.
- Do not pass `UIApplication` through Core.
- Do not re-add `IPluginContext.FullAccess`, `IRawRevitAccess`, `FullAccessSession`, or `PluginLoader`'s `requestFull`; ADR-0009 deleted all four.
- Do not call `Selection.PickObjects` from a plugin; declare presets and let the host pick.
