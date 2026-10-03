# Plugin Hardening — Phase E: Revit-gated fixes (2026-10-03) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the plugin defects that sit in Revit API calls (review findings PL-07 to PL-11): placement, move/rotate/mirror, view colouring and view tools, and the paging and failure reporting of `structure-cuts`.

**Architecture:** Same rule as the main plan. Whatever can be decided without Revit moves into `LoadingAI.Core` and is unit-tested in CI (`SymbolPick`, `PlacementRules`, `LevelNames.ResolveStrict`, `ViewOverrideLedger<T>`, `SplitCursorStore`, `SplitForecast`). The Revit calls stay in the adapter and are covered two ways: `Source_*` text assertions that CI runs, and NUnit tests in `src/LoadingAI.RevitTests` that CI compiles and a person runs against Revit 2027.

**Tech Stack:** C# (netstandard2.0 Core; net48 / net8 / net10 host), xUnit, NUnit (in-Revit), Revit API 2024–2027.

**Source:** [`docs/PLUGIN_REVIEW_2026-10-03.md`](../../PLUGIN_REVIEW_2026-10-03.md). Main plan: [`2026-10-03-plugin-hardening.md`](2026-10-03-plugin-hardening.md) (Tasks 1–15).

**Prerequisites from the main plan**
- Task 4 (`ToolEntry.Describe`, used by `reload_links` in Task 18).
- Task 10 (`ShippedManifestTests`: a new tool needs a `plugin.json` entry, Task 18).
- Task 3 changed `GeometryTools.cs` (selection guards in move and delete). Task 17 edits one line further down that file and does not touch the guard.

**Phases and gates**

| Task | Finding | What | Gate |
|---|---|---|---|
| 16 | PL-08 | Placement: activate in the transaction, z above the level, no silent level or family fallback, hosted families refused, wall height | CI green, then R2 and R3 on Revit 2027 |
| 17 | PL-09, PL-24 | Move and rotate the set in one call; one unit (mm) on the bridge; honest mirror message | CI green, then R10 |
| 18 | PL-10, PL-11 | Solid colour, `reset_view_overrides`, honest descriptions, `tag_all`, `create_dimension`, sheet tools, `reload_links` | CI green, then R4 to R7 |
| 19 | PL-07 | Stable split cursor, failure counts, rollback of a failed batch, dry-run forecast | CI green, then R8 (R9 cannot be forced; see Step 8) |

Do not merge a task before its Revit step has been run and recorded in the PR description. CI cannot run Revit; a green build proves the code compiles and the text assertions hold, nothing more.

**Not in this plan**
- Per-member failure reasons for `structure-cuts` (about twelve `catch` blocks that today collapse to "split failed"). Task 19 counts failures and rolls back a failed batch; naming each cause is a separate change per algorithm.
- Cascade preview for `delete_elements` (needs a rolled-back `Document.Delete`; see the main plan's out-of-scope list).
- PL-20 (empty-transaction false successes, the Comments fallback in `apply_construction_dates`): waits for an owner decision on the fallback.
- `duplicate_element` offset, `View.Duplicate` on sheets, `LookupParameter` ambiguity (PL-24 remainder).
- A Revit-free test of the split geometry (F7).

**Owner decisions assumed**

| # | Decision | Default used here | Task |
|---|---|---|---|
| 5 | Exports of "everything" need explicit ids or `confirmAll` | Moved to main-plan Task 7 (it needs no Revit) | — |
| 7 | An error-severity Revit failure inside a split batch rolls the whole batch back and reports it, instead of showing Revit's dialog | yes | 19 |
| 8 | `create_family_instance`'s `z` becomes a height above the level (it was an absolute elevation) | yes | 16 |
| 9 | `move_elements` on the bridge takes millimetres like every other bridge method | yes | 17 |

**Branch:** continue on `fix/plugin-hardening-2026-10-03` after Task 15, or start `fix/plugin-revit-2026-10-03` from it.

**Test commands**

xUnit (Windows, .NET 10 SDK; on Linux push and read the CI test job):

```bash
dotnet test src/LoadingAI.Tests/LoadingAI.Tests.csproj -c Release -p:SkipCopyAddin=true -p:SkipHostPluginBuild=true
```

Revit smoke (Windows with Revit 2027; close Revit first, the add-in DLL gets replaced). Add `--filter "FullyQualifiedName~PluginFixesInRevitTests"` to run only this phase:

```powershell
dotnet build src/LoadingAI/LoadingAI.csproj -c Release -p:RevitVersion=2027 -p:SkipTrustAddin=true -m:1
$env:DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = "1"
$env:RICAUN_REVITTEST_TESTADAPTER_NUNIT_LANGUAGE = "ENU"
dotnet test src/LoadingAI.RevitTests/LoadingAI.RevitTests.csproj -c Release `
  -p:ForceRevitTests=true -p:RevitVersion=2027 -p:SkipHostPluginBuild=true -p:SkipTrustAddin=true `
  --filter "FullyQualifiedName~PluginFixesInRevitTests"
```

`src/LoadingAI.RevitTests/README.md` explains the registry value and the startup timeout. A test that needs something the throwaway project lacks (a loaded family, a tag family) calls `Assert.Ignore` with what to load; an ignored test is not a pass, so record which ones ran.

**Conventions that apply to every task**
- Same as the main plan: `IndexOf(..., StringComparison.Ordinal)` in text assertions; plugin projects reference `LoadingAI.Core` only; no `new TaskDialog` outside `ToolGuards.cs`, `ApprovalGate.cs`, `HostPage.xaml.cs`.
- Tool text never says feet. `ToolSurface_DoesNotAdvertiseFeet` forbids "in feet", "(feet)", "sq ft", "cu ft" in the plugin sources.
- `src/LoadingAI/Revit/StructureCuts/` holds exactly 14 `.cs` files (pinned by `StructureCutsContractTests`) and `new Transaction(` appears only in `SplitElements.cs`. New code goes into an existing file or into Core.
- Every in-Revit test writes inside a `TransactionGroup` that is rolled back in `finally`.

---

## File map

| File | Change | Task |
|---|---|---|
| `src/LoadingAI.Core/Revit/PlacementRules.cs` | New: `PlacementRules`, `PlacementKind`, `SymbolPick` | 16 |
| `src/LoadingAI.Core/Revit/LevelNames.cs` | `ResolveStrict` | 16 |
| `src/LoadingAI.Core/Revit/IRevitBridge.cs` | `CreateWallAsync(heightMm)`; `MoveElementsAsync` in mm; `ResetViewOverridesAsync` | 16, 17, 18 |
| `src/LoadingAI/Revit/AdapterRevitBridge.Create.cs` | Placement, wall, rotate, mirror | 16, 17 |
| `src/LoadingAI/Revit/AdapterRevitBridge.Elements.cs` | Move | 17 |
| `src/LoadingAI.Core/Revit/ViewOverrideLedger.cs` | New | 18 |
| `src/LoadingAI/Revit/AdapterRevitBridge.Views.cs`, `.Documents.cs` | Colour helpers, ledger, tags, dimension, sheets, reload | 18 |
| `Plugins/core-tools/Core/GeometryTools.cs`, `ViewTools.cs`, `DocumentTools.cs`, `plugin.json` | Schemas, descriptions, `reset_view_overrides` | 16–18 |
| `src/LoadingAI.Core/Revit/StructureCuts/SplitCursorStore.cs`, `SplitForecast.cs` | New | 19 |
| `src/LoadingAI/Revit/StructureCuts/SplitScope.cs`, `SplitElements.cs`, `SplitOutcome.cs`, `CurveMemberSplit.cs`, the five algorithms | Cursor, transaction wrapper, failures, forecast | 19 |
| `src/LoadingAI.Core/Revit/RevitDtos.cs`, `Plugins/structure-cuts/StructureCutsPlugin.cs`, `AdapterRevitBridge.Workflows.cs` | `SplitResultDto.Failed`, `FromSplit` | 19 |
| `src/LoadingAI.Tests/Fakes/FakeRevitBridge.cs`, `SourceSlice.cs` and one test file per task | | all |
| `src/LoadingAI.RevitTests/PluginFixesInRevitTests.cs` | New fixture; tests added per task | 16–19 |

---

### Task 16: Placement and creation (PL-08)

**Files:**
- Create: `src/LoadingAI.Core/Revit/PlacementRules.cs`
- Modify: `src/LoadingAI.Core/Revit/LevelNames.cs`
- Modify: `src/LoadingAI.Core/Revit/IRevitBridge.cs`
- Modify: `src/LoadingAI/Revit/AdapterRevitBridge.Create.cs`
- Modify: `Plugins/core-tools/Core/GeometryTools.cs`
- Modify: `src/LoadingAI.Tests/Fakes/FakeRevitBridge.cs`
- Create: `src/LoadingAI.Tests/SourceSlice.cs`, `PlacementRulesTests.cs`, `PlacementSourceTests.cs`
- Create: `src/LoadingAI.RevitTests/PluginFixesInRevitTests.cs`

What is wrong today (all in `AdapterRevitBridge.Create.cs`): `create_family_instance` and the array call `FamilySymbol.Activate()` before their transaction starts; `z` is an absolute elevation although the instance is tied to a level; an unknown or blank level silently becomes the first level in collector order; the symbol is found by name alone, so `600 x 600` can resolve to any of several families; a hosted family (a door, a window) cannot be placed by `NewFamilyInstance(point, symbol, level, …)` at all, yet the schema's own example is a door; structural columns need `StructuralType.Column`; the array clamps `count` to 50 without saying so; `create_wall` hard-codes a height of 10 ft and takes the first `WallType`, which can be a curtain or stacked wall.

- [ ] **Step 1: Write the failing tests**

Create `src/LoadingAI.Tests/SourceSlice.cs` (shared by Tasks 16 to 19):

```csharp
using System;
using Xunit;

namespace LoadingAI.Tests;

/// <summary>Cuts one method out of a source file so a text assertion cannot match its neighbour.</summary>
internal static class SourceSlice
{
    /// <summary>From the first <paramref name="start"/> up to, not including, the next <paramref name="end"/>.</summary>
    public static string Between(string source, string start, string end)
    {
        var i = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{start}' was not found.");
        var j = source.IndexOf(end, i + start.Length, StringComparison.Ordinal);
        Assert.True(j > i, $"'{end}' was not found after '{start}'.");
        return source.Substring(i, j - i);
    }

    public static int Count(string source, string needle)
    {
        var n = 0;
        for (var i = source.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = source.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
    }
}
```

Create `src/LoadingAI.Tests/PlacementRulesTests.cs`:

```csharp
using LoadingAI.Revit;

namespace LoadingAI.Tests;

public class PlacementRulesTests
{
    [Fact]
    public void OneLevelBased_IsAccepted()
        => Assert.Null(PlacementRules.Refusal("600 x 600", "OneLevelBased"));

    [Theory]
    [InlineData("OneLevelBasedHosted")]
    [InlineData("WorkPlaneBased")]
    [InlineData("TwoLevelsBased")]
    [InlineData("CurveBased")]
    [InlineData("ViewBased")]
    [InlineData("Adaptive")]
    public void EveryOtherKind_IsRefusedByName(string placement)
    {
        var message = PlacementRules.Refusal("Single-Flush", placement);
        Assert.NotNull(message);
        Assert.Contains("Single-Flush", message);
        Assert.Contains(placement, message);
    }

    [Fact]
    public void AHostedFamily_IsToldItNeedsAHost()
        => Assert.Contains("host", PlacementRules.Refusal("Single-Flush", "OneLevelBasedHosted"), System.StringComparison.OrdinalIgnoreCase);

    [Theory]
    [InlineData("OST_StructuralColumns", PlacementKind.Column)]
    [InlineData("OST_StructuralFoundation", PlacementKind.Footing)]
    [InlineData("OST_Furniture", PlacementKind.NonStructural)]
    [InlineData(null, PlacementKind.NonStructural)]
    public void TheStructuralKind_FollowsTheCategory(string? category, PlacementKind expected)
        => Assert.Equal(expected, PlacementRules.KindFor(category));
}

public class SymbolPickTests
{
    private static readonly (string Family, string Symbol)[] Catalog =
    {
        ("Concrete-Rectangular-Column", "600 x 600"),
        ("Concrete-Rectangular-Column", "450 x 450"),
        ("Steel Column", "600 x 600"),
        ("Desk", "1500 x 750"),
    };

    [Fact]
    public void AUniqueTypeName_IsPicked()
    {
        Assert.Equal(1, SymbolPick.Choose(Catalog, "450 x 450", null, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void ATypeNameInTwoFamilies_IsAmbiguousWithoutAFamily()
    {
        Assert.Equal(-1, SymbolPick.Choose(Catalog, "600 x 600", null, out var error));
        Assert.Contains("2 families", error);
        Assert.Contains("Steel Column", error);
        Assert.Contains("familyName", error);
    }

    [Fact]
    public void TheFamilyDecidesWhenThereAreTwo()
        => Assert.Equal(2, SymbolPick.Choose(Catalog, "600 x 600", "steel column", out _));

    [Fact]
    public void AFamilyThatLacksTheType_DoesNotFallBackToAnotherFamily()
    {
        Assert.Equal(-1, SymbolPick.Choose(Catalog, "450 x 450", "Steel Column", out var error));
        Assert.Contains("Steel Column", error);
        Assert.Contains("600 x 600", error);
    }

    [Fact]
    public void AnUnknownName_ListsWhatIsLoaded()
    {
        Assert.Equal(-1, SymbolPick.Choose(Catalog, "nope", null, out var error));
        Assert.Contains("Available", error);
        Assert.Contains("Desk: 1500 x 750", error);
    }

    [Fact]
    public void Matching_IgnoresCase()
        => Assert.Equal(3, SymbolPick.Choose(Catalog, "1500 X 750", "DESK", out _));
}

public class LevelNamesStrictTests
{
    private static readonly string[] Two = { "Level 1", "Level 2" };

    [Fact]
    public void ANamedLevel_IsFoundIgnoringCase()
    {
        Assert.Equal("Level 2", LevelNames.ResolveStrict("level 2", Two, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void AnUnknownName_IsAnErrorThatListsTheLevels_NotTheFirstLevel()
    {
        Assert.Null(LevelNames.ResolveStrict("Level 9", Two, out var error));
        Assert.Contains("Level 9", error);
        Assert.Contains("Level 1", error);
        Assert.Contains("Level 2", error);
    }

    [Fact]
    public void ABlankName_IsFineWhenThereIsOneLevel()
        => Assert.Equal("Ground", LevelNames.ResolveStrict(null, new[] { "Ground" }, out _));

    [Fact]
    public void ABlankName_IsAnErrorWhenThereAreSeveral()
    {
        Assert.Null(LevelNames.ResolveStrict("  ", Two, out var error));
        Assert.Contains("levelName", error);
        Assert.Contains("Level 1", error);
    }

    [Fact]
    public void NoLevels_IsAnError()
    {
        Assert.Null(LevelNames.ResolveStrict("Level 1", new string[0], out var error));
        Assert.Equal("No levels found.", error);
    }
}
```

Create `src/LoadingAI.Tests/PlacementSourceTests.cs`:

```csharp
using System.IO;
using System.Linq;
using LoadingAI.Plugins;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

/// <summary>
/// The adapter needs a live document, so these assert on its source (CONTRIBUTING.md) and the plugin
/// side with the fake bridge. The behaviour itself is checked by <c>PluginFixesInRevitTests</c>.
/// </summary>
public class PlacementSourceTests
{
    private static string Create() => AdapterSource.Containing("public Task<CommandResultDto> CreateFamilyInstanceAsync");

    private static string FamilyInstanceMethod() => SourceSlice.Between(Create(),
        "public Task<CommandResultDto> CreateFamilyInstanceAsync", "public Task<CommandResultDto> DuplicateElementAsync");

    private static string ArrayMethod() => SourceSlice.Between(Create(),
        "public Task<CommandResultDto> CreateFamilyInstanceArrayAsync", "public Task<CommandResultDto> RotateElementsAsync");

    [Fact]
    public void Activation_HappensInsideTheTransaction()
    {
        foreach (var slice in new[] { FamilyInstanceMethod(), ArrayMethod() })
        {
            var start = slice.IndexOf("tx.Start();", System.StringComparison.Ordinal);
            var activate = slice.IndexOf("Activate();", System.StringComparison.Ordinal);
            Assert.True(start >= 0 && activate > start, "Activate() must come after tx.Start().");
        }
    }

    [Fact]
    public void NoSilentFallbackToTheFirstLevelOrAnyFamily()
    {
        foreach (var slice in new[] { FamilyInstanceMethod(), ArrayMethod() })
        {
            Assert.DoesNotContain("lvl ??=", slice);
            Assert.DoesNotContain("level ??=", slice);
            Assert.Contains("ResolvePlacement(", slice);
        }

        var all = Create();
        Assert.Contains("LevelNames.ResolveStrict(", all);
        Assert.Contains("SymbolPick.Choose(", all);
        Assert.Contains("PlacementRules.Refusal(", all);
    }

    [Fact]
    public void ZIsAHeightAboveTheLevel()
    {
        Assert.Contains("lvl!.Elevation + Metric.MmToFt(z)", FamilyInstanceMethod());
        Assert.Contains("level!.Elevation", ArrayMethod());
    }

    [Fact]
    public void TheArrayCountIsRefusedNotClamped()
    {
        var slice = ArrayMethod();
        Assert.DoesNotContain("Polyfills.Clamp(count", slice);
        Assert.Contains("count must be between 1 and", slice);
    }

    [Fact]
    public void TheWallDefaultIsABasicTypeWithAHeightFromTheCaller()
    {
        var slice = SourceSlice.Between(Create(),
            "public Task<CommandResultDto> CreateWallAsync", "public Task<CommandResultDto> CreateFamilyInstanceAsync");
        Assert.Contains("WallKind.Basic", slice);
        Assert.Contains("heightMm ?? DefaultWallHeightMm", slice);
        Assert.DoesNotContain("level.Id, 10, 0", slice);
    }

    [Fact]
    public async System.Threading.Tasks.Task CreateWall_ForwardsTheHeight_AndOmittingItPassesNull()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = GeometryTools.Create(new FakePluginContext(bridge)).First(e => e.Definition.Function.Name == "create_wall");

        await tool.ExecuteAsync("""{"levelName":"Level 1","x1":0,"y1":0,"x2":4000,"y2":0,"heightMm":3500}""");
        await tool.ExecuteAsync("""{"levelName":"Level 1","x1":0,"y1":0,"x2":4000,"y2":0}""");

        Assert.Equal(new double?[] { 3500, null }, bridge.CreateWallHeights);
    }

    [Fact]
    public void TheToolSchemasTellTheModelWhatChanged()
    {
        var text = File.ReadAllText(Path.Combine(AdapterSource.RepoRoot(), "Plugins", "core-tools", "Core", "GeometryTools.cs"));
        Assert.Contains("heightMm = new { type = \"number\"", text);
        Assert.Contains("height above the level", text);
        Assert.Contains("Hosted families", text);
    }
}
```

- [ ] **Step 2: Run to confirm they fail**

Run: the xUnit command.
Expected: compile errors (`PlacementRules`, `SymbolPick`, `LevelNames.ResolveStrict`, `FakeRevitBridge.CreateWallHeights` do not exist).

- [ ] **Step 3: Core — the pure rules**

Create `src/LoadingAI.Core/Revit/PlacementRules.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace LoadingAI.Revit;

public enum PlacementKind
{
    NonStructural,
    Column,
    Footing,
}

/// <summary>
/// What <c>create_family_instance</c> can place. It calls <c>NewFamilyInstance(point, symbol, level, type)</c>,
/// which only works for a family that stands on a level by itself. Everything else needs a host, a
/// second level, a work plane or a curve, so it is refused with the reason instead of reaching Revit
/// as a guaranteed exception.
/// </summary>
public static class PlacementRules
{
    /// <param name="placementType">The name of Revit's <c>FamilyPlacementType</c> value.</param>
    /// <returns>Null when the family can be placed, else the sentence to show the model.</returns>
    public static string? Refusal(string symbolName, string placementType)
    {
        if (string.Equals(placementType, "OneLevelBased", StringComparison.OrdinalIgnoreCase))
            return null;

        if (string.Equals(placementType, "OneLevelBasedHosted", StringComparison.OrdinalIgnoreCase)
            || string.Equals(placementType, "WorkPlaneBased", StringComparison.OrdinalIgnoreCase))
            return $"'{symbolName}' is a hosted family ({placementType}); it needs a wall, floor or face to sit on. "
                + "This tool places level-based families only (columns, furniture, equipment).";

        return $"'{symbolName}' is a {placementType} family. This tool places single-point, level-based families only.";
    }

    /// <param name="builtInCategoryName">For example <c>OST_StructuralColumns</c>; null when the family has no category.</param>
    public static PlacementKind KindFor(string? builtInCategoryName)
    {
        if (string.Equals(builtInCategoryName, "OST_StructuralColumns", StringComparison.Ordinal))
            return PlacementKind.Column;
        if (string.Equals(builtInCategoryName, "OST_StructuralFoundation", StringComparison.Ordinal))
            return PlacementKind.Footing;
        return PlacementKind.NonStructural;
    }
}

/// <summary>Chooses a family type from the loaded ones without ever guessing between families.</summary>
public static class SymbolPick
{
    /// <returns>The index into <paramref name="candidates"/>, or -1 with <paramref name="error"/> set.</returns>
    public static int Choose(
        IReadOnlyList<(string Family, string Symbol)> candidates, string symbolName, string? familyName, out string? error)
    {
        error = null;
        var matches = new List<int>();
        for (var i = 0; i < candidates.Count; i++)
        {
            if (string.Equals(candidates[i].Symbol, symbolName, StringComparison.OrdinalIgnoreCase))
                matches.Add(i);
        }

        if (!string.IsNullOrWhiteSpace(familyName))
        {
            foreach (var i in matches)
            {
                if (string.Equals(candidates[i].Family, familyName, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            var inFamily = candidates
                .Where(c => string.Equals(c.Family, familyName, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Symbol)
                .Take(10)
                .ToList();
            error = inFamily.Count > 0
                ? $"Symbol '{symbolName}' not found in family '{familyName}'. Types in that family: {string.Join(", ", inFamily)}."
                : $"Family '{familyName}' not found. {Available(candidates)}";
            return -1;
        }

        if (matches.Count == 1)
            return matches[0];

        if (matches.Count == 0)
        {
            error = $"Symbol '{symbolName}' not found. {Available(candidates)}";
            return -1;
        }

        var families = matches.Select(i => candidates[i].Family).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
        error = $"Symbol '{symbolName}' exists in {families.Count} families ({string.Join(", ", families)}). Pass familyName.";
        return -1;
    }

    private static string Available(IReadOnlyList<(string Family, string Symbol)> candidates)
    {
        var first = candidates.Take(10).Select(c => $"{c.Family}: {c.Symbol}").ToList();
        if (first.Count == 0)
            return "No family types are loaded.";
        return "Available: " + string.Join(", ", first) + (candidates.Count > 10 ? ", ..." : "") + ".";
    }
}
```

In `src/LoadingAI.Core/Revit/LevelNames.cs` add above `Suggest`:

```csharp
    /// <summary>
    /// Placement tools must never put something on "some" level. A blank name is accepted only when the
    /// model has exactly one level; an unknown name is an error that lists the levels.
    /// </summary>
    public static string? ResolveStrict(string? wanted, IReadOnlyList<string> names, out string? error)
    {
        error = null;
        if (names == null || names.Count == 0)
        {
            error = "No levels found.";
            return null;
        }

        var list = string.Join(", ", names.Take(12)) + (names.Count > 12 ? ", ..." : "");
        if (string.IsNullOrWhiteSpace(wanted))
        {
            if (names.Count == 1) return names[0];
            error = $"Provide levelName. Levels: {list}.";
            return null;
        }

        var found = Resolve(wanted!, names);
        if (found == null) error = $"{NotFound(wanted!, names)} Levels: {list}.";
        return found;
    }
```

- [ ] **Step 4: The bridge, the fake and the plugin**

`src/LoadingAI.Core/Revit/IRevitBridge.cs`, in `IRevitCreateCommand` replace

```csharp
        double x1, double y1, double x2, double y2,
        string? wallTypeName = null,
        CancellationToken ct = default);

    Task<CommandResultDto> CreateFamilyInstanceAsync(
```

with

```csharp
        double x1, double y1, double x2, double y2,
        string? wallTypeName = null,
        double? heightMm = null,
        CancellationToken ct = default);

    /// <summary>
    /// Places a level-based family. <paramref name="z"/> is millimetres <b>above the level</b>, not an
    /// absolute elevation. A blank level is accepted only when the model has one level.
    /// </summary>
    Task<CommandResultDto> CreateFamilyInstanceAsync(
```

`src/LoadingAI.Tests/Fakes/FakeRevitBridge.cs`: replace the `CreateWallAsync` signature and first statement

```csharp
        string? wallTypeName = null,
        CancellationToken ct = default)
    {
        CreateWallCalls.Add((levelName, x1, y1, x2, y2, wallTypeName));
```

with

```csharp
        string? wallTypeName = null,
        double? heightMm = null,
        CancellationToken ct = default)
    {
        CreateWallCalls.Add((levelName, x1, y1, x2, y2, wallTypeName));
        CreateWallHeights.Add(heightMm);
```

and add next to `CreateWallCalls` (line 645):

```csharp
    /// <summary>The <c>heightMm</c> of each <c>CreateWallAsync</c> call, parallel to <see cref="CreateWallCalls"/>.</summary>
    public List<double?> CreateWallHeights { get; } = new();
```

`Plugins/core-tools/Core/GeometryTools.cs`, `CreateWall`: replace the description and the schema's last property

```csharp
                Description = "Create a wall by start/end points (mm) on a level.",
```
with
```csharp
                Description = "Create a straight wall by start and end points (mm) on a level. heightMm defaults to 3000; wallTypeName defaults to the first basic wall type.",
```

and

```csharp
                        wallTypeName = new { type = "string", description = "Optional wall type name" }
```
with
```csharp
                        wallTypeName = new { type = "string", description = "Optional wall type name" },
                        heightMm = new { type = "number", description = "Optional wall height in mm, default 3000" }
```

In `CreateWallAsync` (the handler) replace `string? wallTypeName = null;` with `string? wallTypeName = null; double? heightMm = null;`, add after the `wallTypeName` read:

```csharp
            if (d.RootElement.TryGetProperty("heightMm", out var h) && h.ValueKind == JsonValueKind.Number) heightMm = h.GetDouble();
```

and replace the call with

```csharp
        return WriteResult(await context.Bridge.CreateWallAsync(levelName, x1, y1, x2, y2, wallTypeName, heightMm));
```

`CreateCreateFamilyInstance`: replace the description and the property texts:

```csharp
                Description = "Place a level-based family instance (column, furniture, equipment) by type name at a point (mm). z is the height above the level in mm, default 0. Hosted families such as doors and windows are refused. If several families share the type name, pass familyName.",
```

```csharp
                        symbolName = new { type = "string", description = "Family type name, e.g. '600 x 600'" },
                        familyName = new { type = "string", description = "Family name, e.g. 'Concrete-Rectangular-Column'. Needed when several families have the same type name." },
                        levelName = new { type = "string", description = "Level name. May be omitted only when the model has a single level." },
                        x = new { type = "number" }, y = new { type = "number" }, z = new { type = "number", description = "Optional height above the level in mm, default 0" }
```

`CreateFamilyInstanceArray`: replace the description with `"Array level-based family instances along a line, count 1 to 50. Params: symbolName, familyName (needed when several families share the type name), startX,startY, endX,endY in mm, count, levelName (optional only when the model has one level). Hosted families are refused."` and add `familyName = new { type = "string" },` before `levelName = new { type = "string" }` in its schema. In its handler `CreateFamilyInstanceArrayAsync` replace `int cnt = 5; string? lvl = null;` with `int cnt = 5; string? lvl = null; string? fam = null;`, add after the `levelName` read

```csharp
            if (d.RootElement.TryGetProperty("familyName", out var f) && f.ValueKind == JsonValueKind.String) fam = f.GetString();
```

and end the call with `cnt, lvl, fam)`.

The array needs the family name for the same reason the single call does (`SymbolPick` refuses an ambiguous type name). In `IRevitBridge.cs` replace

```csharp
        int count,
        string? levelName = null,
        CancellationToken ct = default);

    Task<CommandResultDto> RotateElementsAsync(
```

with

```csharp
        int count,
        string? levelName = null,
        string? familyName = null,
        CancellationToken ct = default);

    Task<CommandResultDto> RotateElementsAsync(
```

and in `FakeRevitBridge.CreateFamilyInstanceArrayAsync` add `string? familyName = null,` after `string? levelName = null,` (the recorded tuple stays as it is). Existing positional callers still compile because the new parameter is optional.

- [ ] **Step 5: The adapter**

In `src/LoadingAI/Revit/AdapterRevitBridge.Create.cs` add under the `using` lines `using StructuralType = Autodesk.Revit.DB.Structure.StructuralType;` and at the top of the class body:

```csharp
    private const double DefaultWallHeightMm = 3000;
    private const int MaxArrayCount = 50;

    /// <summary>
    /// The symbol and level a placement tool asked for, or the sentence that says why not. Nothing
    /// falls back to "the first one in the collector": a wrong family or level is a wrong model.
    /// </summary>
    private static string? ResolvePlacement(
        Document doc, string symbolName, string? familyName, string? levelName,
        out FamilySymbol? symbol, out Level? level, out StructuralType structuralType)
    {
        symbol = null;
        level = null;
        structuralType = StructuralType.NonStructural;

        var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToList();
        var index = SymbolPick.Choose(symbols.Select(s => (s.FamilyName, s.Name)).ToList(), symbolName, familyName, out var pickError);
        if (index < 0) return pickError;
        symbol = symbols[index];

        var refusal = PlacementRules.Refusal(symbol.Name, symbol.Family.FamilyPlacementType.ToString());
        if (refusal != null) return refusal;

        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
        var resolved = LevelNames.ResolveStrict(levelName, levels.Select(l => l.Name).ToList(), out var levelError);
        if (resolved == null) return levelError;
        level = levels.First(l => l.Name == resolved);

        structuralType = PlacementRules.KindFor(symbol.Category?.BuiltInCategory.ToString()) switch
        {
            PlacementKind.Column => StructuralType.Column,
            PlacementKind.Footing => StructuralType.Footing,
            _ => StructuralType.NonStructural,
        };
        return null;
    }
```

`CreateWallAsync`: apply the three hunks.

1. Replace
```csharp
        string? wallTypeName = null,
        CancellationToken ct = default)
```
with
```csharp
        string? wallTypeName = null,
        double? heightMm = null,
        CancellationToken ct = default)
```
2. Replace
```csharp
                    var wt = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().FirstOrDefault();
                    if (wt == null) return new CommandResultDto(false, "No wall types found.");
```
with
```csharp
                    // A plain wall, never a curtain or stacked type that happens to sort first.
                    var wt = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
                        .FirstOrDefault(t => t.Kind == WallKind.Basic);
                    if (wt == null) return new CommandResultDto(false, "No basic wall type found. Pass wallTypeName.");
```
3. Replace
```csharp
                var p1 = new XYZ(Metric.MmToFt(x1), Metric.MmToFt(y1), 0);
```
with
```csharp
                var height = heightMm ?? DefaultWallHeightMm;
                if (height < 100 || height > 300000)
                    return new CommandResultDto(false, $"heightMm must be between 100 and 300000 (got {height:F0}).");
                var p1 = new XYZ(Metric.MmToFt(x1), Metric.MmToFt(y1), 0);
```
and `Wall.Create(doc, line, wallTypeId, level.Id, 10, 0, false, false)` with `Wall.Create(doc, line, wallTypeId, level.Id, Metric.MmToFt(height), 0, false, false)`, and in the success text replace `mm type {wallTypeId}.` with `mm, height {height:F0} mm, type {wallTypeId}.`

Replace the whole `CreateFamilyInstanceAsync` method with:

```csharp
    public Task<CommandResultDto> CreateFamilyInstanceAsync(
        string symbolName,
        double x, double y,
        string? familyName = null,
        string? levelName = null,
        double z = 0,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");

            try
            {
                var refusal = ResolvePlacement(doc, symbolName, familyName, levelName, out var sym, out var lvl, out var structural);
                if (refusal != null) return new CommandResultDto(false, refusal);

                // z is a height above the level, in the same millimetres as x and y.
                var pt = new XYZ(Metric.MmToFt(x), Metric.MmToFt(y), lvl!.Elevation + Metric.MmToFt(z));
                using var tx = new Transaction(doc, "LoadingAI for Revit: create instance");
                tx.Start();
                try
                {
                    // Activating edits the document, so it belongs inside the transaction.
                    if (!sym!.IsActive) sym.Activate();
                    var inst = doc.Create.NewFamilyInstance(pt, sym, lvl, structural);
                    tx.Commit();
                    var id = inst.Id.ToString();
                    return new CommandResultDto(
                        true,
                        $"Created instance {id} '{sym.Name}' ({sym.FamilyName}) at ({x:F0},{y:F0}) mm, {z:F0} mm above {lvl.Name}.",
                        1,
                        ElementIds: new[] { id });
                }
                catch (Exception ex)
                {
                    try { tx.RollBack(); } catch { }
                    return new CommandResultDto(false, $"Failed: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                return new CommandResultDto(false, $"Failed: {ex.Message}");
            }
        }, ct);
    }
```

Replace the whole `CreateFamilyInstanceArrayAsync` method with:

```csharp
    public Task<CommandResultDto> CreateFamilyInstanceArrayAsync(
        string symbolName,
        double startX, double startY, double endX, double endY,
        int count,
        string? levelName = null,
        string? familyName = null,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            if (count < 1 || count > MaxArrayCount)
                return new CommandResultDto(false, $"count must be between 1 and {MaxArrayCount} (got {count}).");

            try
            {
                var refusal = ResolvePlacement(doc, symbolName, familyName, levelName, out var symbol, out var level, out var structural);
                if (refusal != null) return new CommandResultDto(false, refusal);

                var sx = Metric.MmToFt(startX); var sy = Metric.MmToFt(startY);
                var exFt = Metric.MmToFt(endX); var eyFt = Metric.MmToFt(endY);
                using var tx = new Transaction(doc, $"LoadingAI for Revit: array {symbolName} x{count}");
                tx.Start();
                try
                {
                    if (!symbol!.IsActive) symbol.Activate();
                    var ids = new List<ElementId>();
                    for (int i = 0; i < count; i++)
                    {
                        double t = (double)i / (count - 1 == 0 ? 1 : count - 1);
                        var pt = new XYZ(sx + (exFt - sx) * t, sy + (eyFt - sy) * t, level!.Elevation);
                        var inst = doc.Create.NewFamilyInstance(pt, symbol, level, structural);
                        ids.Add(inst.Id);
                    }
                    tx.Commit();
                    var idStrs = ids.Select(id => id.ToString()).ToList();
                    return new CommandResultDto(
                        true,
                        $"Created {idStrs.Count} {symbol.Name} on {level!.Name}: {string.Join(",", idStrs.Take(5))}{(idStrs.Count > 5 ? ",..." : "")}",
                        idStrs.Count,
                        ElementIds: idStrs);
                }
                catch (Exception ex)
                {
                    try { tx.RollBack(); } catch { }
                    return new CommandResultDto(false, $"Failed: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                return new CommandResultDto(false, $"Failed: {ex.Message}");
            }
        }, ct);
    }
```

- [ ] **Step 6: Run and commit**

Run: the xUnit command.
Expected: PASS, including `L3CommandContractTests` and `FakeBridgeToolTests` (the existing `CreateWall` tests use positional arguments and still compile).

```bash
git add src Plugins
git commit -m "fix(create): activate in the transaction, z above the level, no silent level or family, wall height"
```

- [ ] **Step 7: Revit smoke (R2, R3). Required before merge**

Create `src/LoadingAI.RevitTests/PluginFixesInRevitTests.cs`. This file is shared by Tasks 16 to 19; later tasks add `[Test]` methods to it.

```csharp
using System.IO;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LoadingAI.Revit;
using LoadingAI.Tools;
using NUnit.Framework;

namespace LoadingAI.RevitTests;

/// <summary>
/// Live Revit 2027 checks for plan 2026-10-03-plugin-revit-fixes.md. Every test writes inside a
/// TransactionGroup that is rolled back in <c>finally</c>, so nothing persists. CI compiles this file
/// and does not run it (see README.md in this folder). A test that needs something the throwaway
/// project does not have calls Assert.Ignore and says what to load.
/// </summary>
[TestFixture]
public class PluginFixesInRevitTests
{
    private UIApplication _uiApp = null!;
    private RevitBridge? _bridge;
    private AdapterRevitBridge? _adapter;
    private string? _tempModelPath;
    private Document? _ownedDoc;

    private AdapterRevitBridge Adapter
        => _adapter ??= new AdapterRevitBridge(_bridge ?? throw new InvalidOperationException("RevitBridge was not created."));

    [OneTimeSetUp]
    public void OneTimeSetUp(UIApplication uiApp)
    {
        _uiApp = uiApp;
        _bridge = new RevitBridge();
        _bridge.AttachCurrentThread(_uiApp);
        if (_uiApp.ActiveUIDocument?.Document != null)
            return;

        try
        {
            var doc = _uiApp.Application.NewProjectDocument(UnitSystem.Metric);
            _tempModelPath = Path.Combine(Path.GetTempPath(), $"LoadingAI.PluginFixes.{Guid.NewGuid():N}.rvt");
            doc.SaveAs(_tempModelPath);
            doc.Close(false);
            _ownedDoc = _uiApp.OpenAndActivateDocument(_tempModelPath)?.Document;
            _bridge.AttachCurrentThread(_uiApp);
        }
        catch (Exception ex)
        {
            Assert.Ignore("Could not create a throwaway project for the plugin-fix tests: " + ex.Message);
        }
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        try { _bridge?.Dispose(); } catch { }
        _bridge = null;
        try
        {
            if (_ownedDoc is { IsValidObject: true })
                _ownedDoc.Close(false);
        }
        catch { /* Revit may already have closed it. */ }

        if (!string.IsNullOrEmpty(_tempModelPath))
        {
            try { File.Delete(_tempModelPath); } catch { }
            try { File.Delete(_tempModelPath + ".backup"); } catch { }
        }
    }

    [SetUp]
    public void RequireDocument()
    {
        if (_uiApp.ActiveUIDocument?.Document == null)
            Assert.Ignore("Open a Revit model before running the plugin-fix tests.");
    }

    private static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();

    private static Level NewLevel(Document doc, string name, double elevationMm)
    {
        using var tx = new Transaction(doc, "LoadingAI.RevitTests level " + name);
        tx.Start();
        var level = Level.Create(doc, Metric.MmToFt(elevationMm));
        level.Name = name;
        tx.Commit();
        return level;
    }

    private static Wall NewWall(Document doc, Level level, double x1Mm, double y1Mm, double x2Mm, double y2Mm, double heightMm = 3000)
    {
        var type = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().First(t => t.Kind == WallKind.Basic);
        using var tx = new Transaction(doc, "LoadingAI.RevitTests wall");
        tx.Start();
        var wall = Wall.Create(
            doc,
            Line.CreateBound(new XYZ(Metric.MmToFt(x1Mm), Metric.MmToFt(y1Mm), 0), new XYZ(Metric.MmToFt(x2Mm), Metric.MmToFt(y2Mm), 0)),
            type.Id, level.Id, Metric.MmToFt(heightMm), 0, false, false);
        tx.Commit();
        return wall;
    }

    /// <summary>A loaded family type that stands on a level by itself, inactive ones first.</summary>
    private static FamilySymbol? LevelBasedSymbol(Document doc)
        => new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .Where(s => PlacementRules.Refusal(s.Name, s.Family.FamilyPlacementType.ToString()) == null)
            .OrderBy(s => s.IsActive)
            .FirstOrDefault();

    // ---- Task 16 -------------------------------------------------------------------------

    [Test]
    public void CreateFamilyInstance_PutsZAboveTheNamedLevel_AndActivatesInsideTheTransaction()
    {
        var doc = _uiApp.ActiveUIDocument.Document;
        var symbol = LevelBasedSymbol(doc);
        if (symbol == null) Assert.Ignore("Load a level-based family (a column or a piece of furniture) into the open model.");

        using var group = new TransactionGroup(doc, "LoadingAI.RevitTests create_family_instance z");
        group.Start();
        try
        {
            var upper = NewLevel(doc, "LAI-Test Upper", 6000);
            var result = Run(Adapter.CreateFamilyInstanceAsync(symbol!.Name, 0, 0, symbol.FamilyName, upper.Name, 500));

            Assert.That(result.Ok, Is.True, result.Message);
            var instance = doc.GetElement(AllTools.ParseEid(result.ElementIds![0]));
            var z = ((LocationPoint)instance.Location).Point.Z;
            Assert.That(z, Is.EqualTo(upper.Elevation + Metric.MmToFt(500)).Within(1e-4), "z is a height above the level, not an absolute elevation (R3)");
        }
        finally
        {
            group.RollBack();
        }
    }

    [Test]
    public void CreateFamilyInstance_UnknownLevel_IsRefused_NotPlacedOnTheFirstLevel()
    {
        var doc = _uiApp.ActiveUIDocument.Document;
        var symbol = LevelBasedSymbol(doc);
        if (symbol == null) Assert.Ignore("Load a level-based family into the open model.");

        using var group = new TransactionGroup(doc, "LoadingAI.RevitTests create_family_instance level");
        group.Start();
        try
        {
            var before = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).GetElementCount();
            var result = Run(Adapter.CreateFamilyInstanceAsync(symbol!.Name, 0, 0, symbol.FamilyName, "No Such Level"));

            Assert.That(result.Ok, Is.False);
            Assert.That(result.Message, Does.Contain("No Such Level"));
            Assert.That(new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).GetElementCount(), Is.EqualTo(before));
        }
        finally
        {
            group.RollBack();
        }
    }

    [Test]
    public void CreateWall_UsesTheRequestedHeight_AndABasicType()
    {
        var doc = _uiApp.ActiveUIDocument.Document;
        var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).First();

        using var group = new TransactionGroup(doc, "LoadingAI.RevitTests create_wall height");
        group.Start();
        try
        {
            var result = Run(Adapter.CreateWallAsync(level.Name, 0, 0, 4000, 0, null, 3500));

            Assert.That(result.Ok, Is.True, result.Message);
            var wall = (Wall)doc.GetElement(AllTools.ParseEid(result.ElementIds![0]));
            Assert.That(wall.WallType.Kind, Is.EqualTo(WallKind.Basic));
            Assert.That(wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM).AsDouble(), Is.EqualTo(Metric.MmToFt(3500)).Within(1e-4));
        }
        finally
        {
            group.RollBack();
        }
    }

    // ---- Task 1 of the main plan (R1). It passes once unit-aware parameter writes are merged. --------

    [Test]
    public void SetParameter_LengthIsMillimetres_NotFeet()
    {
        var doc = _uiApp.ActiveUIDocument.Document;

        using var group = new TransactionGroup(doc, "LoadingAI.RevitTests set_parameter units");
        group.Start();
        try
        {
            var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).First();
            var wall = NewWall(doc, level, 0, 0, 4000, 0);

            var result = Run(Adapter.SetParameterAsync(wall.Id.ToString(), "Unconnected Height", "3000"));

            Assert.That(result.Ok, Is.True, result.Message);
            Assert.That(
                wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM).AsDouble(),
                Is.EqualTo(Metric.MmToFt(3000)).Within(1e-4),
                "R1: 3000 means 3000 mm; before the units task it stored 3000 feet");
        }
        finally
        {
            group.RollBack();
        }
    }
}
```

Run it with the Revit smoke command. Expected: the wall-height test passes; the two family tests pass or report `Ignored` with the instruction to load a family; `SetParameter_LengthIsMillimetres_NotFeet` is the live check for main-plan Task 1 (R1) and fails until that task is merged. If they are ignored, open a model that has a column family loaded and run again; then record R2 (an inactive type is now placed: look for a symbol with `IsActive == false` in the NUnit output) and R3 (`z` above the level) in the PR.

```bash
git add src/LoadingAI.RevitTests
git commit -m "test(revit): in-Revit checks for placement and wall height"
```

---

### Task 17: Move, rotate and mirror (PL-09, PL-24)

**Files:**
- Modify: `src/LoadingAI.Core/Revit/IRevitBridge.cs`
- Modify: `src/LoadingAI/Revit/AdapterRevitBridge.Elements.cs`, `AdapterRevitBridge.Create.cs`
- Modify: `Plugins/core-tools/Core/GeometryTools.cs`
- Modify: `src/LoadingAI.Tests/Fakes/FakeRevitBridge.cs`, `WriteHonestyAndProviderCatalogTests.cs`
- Create: `src/LoadingAI.Tests/TransformTests.cs`
- Modify: `src/LoadingAI.RevitTests/PluginFixesInRevitTests.cs`

What is wrong today: `MoveElementsAsync` and `RotateElementsAsync` call `ElementTransformUtils.MoveElement` / `RotateElement` once per id. A wall and a door hosted on it, both in the set, are moved twice: once with the host and once on their own. The plural APIs move the set as one operation. `MoveElementsAsync` also takes **feet** (the plugin converts, `GeometryTools.cs:76`, and the result says "ft") while every `Create*`, the rotate origin and the mirror line take **millimetres**, under parameter names that do not say which. `mirror_elements` mirrors in place (`mirrorCopies: false`) but reports "→ N new elements" and counts the (empty) list of copies as the changed count.

- [ ] **Step 1: Write the failing tests**

Create `src/LoadingAI.Tests/TransformTests.cs`:

```csharp
using System.Linq;
using LoadingAI.Revit;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

public class TransformTests
{
    [Fact]
    public void TheBridgeTakesMillimetres_LikeEveryOtherMethod()
    {
        var names = typeof(IRevitElementCommand).GetMethod("MoveElementsAsync")!.GetParameters().Select(p => p.Name);
        Assert.Equal(new[] { "elementIds", "dxMm", "dyMm", "dzMm", "ct" }, names);
    }

    [Fact]
    public async System.Threading.Tasks.Task MoveElements_PassesMillimetresThrough_NotFeet()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = GeometryTools.Create(new FakePluginContext(bridge)).First(e => e.Definition.Function.Name == "move_elements");

        await tool.ExecuteAsync("""{"elementIds":["10","11"],"dx":1000,"dy":0,"dz":250}""");

        var call = Assert.Single(bridge.MoveElementsCalls);
        Assert.Equal((1000d, 0d, 250d), (call.Dx, call.Dy, call.Dz));
    }

    [Fact]
    public void Move_MovesTheWholeSetInOneCall()
    {
        var src = AdapterSource.Containing("public Task<CommandResultDto> MoveElementsAsync");
        var slice = SourceSlice.Between(src, "public Task<CommandResultDto> MoveElementsAsync", "public Task<CommandResultDto> ChangeElementTypeAsync");
        Assert.Contains("ElementTransformUtils.MoveElements(doc, ids, vec);", slice);
        Assert.DoesNotContain("ElementTransformUtils.MoveElement(", slice);
        Assert.DoesNotContain("foreach", slice);
        Assert.Contains("Metric.MmToFt(dxMm)", slice);
        Assert.DoesNotContain(" ft", slice);
        Assert.Contains(" mm.", slice);
    }

    [Fact]
    public void Rotate_RotatesTheWholeSetInOneCall_AndNamesTheCentreInMillimetres()
    {
        var src = AdapterSource.Containing("public Task<CommandResultDto> RotateElementsAsync");
        var slice = SourceSlice.Between(src, "public Task<CommandResultDto> RotateElementsAsync", "public Task<CommandResultDto> MirrorElementsAsync");
        Assert.Contains("ElementTransformUtils.RotateElements(doc, eids, axis, rad);", slice);
        Assert.DoesNotContain("ElementTransformUtils.RotateElement(", slice);
        Assert.Contains("Metric.FtToMm(origin.X)", slice);
    }

    [Fact]
    public void Mirror_CountsTheElementsItMirrored_NotTheCopiesItDidNotMake()
    {
        var src = AdapterSource.Containing("public Task<CommandResultDto> MirrorElementsAsync");
        var slice = SourceSlice.Between(src, "public Task<CommandResultDto> MirrorElementsAsync", "public Task<CommandResultDto> SplitWallAsync");
        Assert.Contains("in place", slice);
        Assert.Contains("eids.Count);", slice);
        Assert.DoesNotContain("new elements", slice);
        Assert.DoesNotContain("newIds.Count", slice);
    }
}
```

Replace `RotateElements_DoesNotSwallowPerElementFailures` in `src/LoadingAI.Tests/WriteHonestyAndProviderCatalogTests.cs` (the comment block above it and the test, lines 20 to 48) with the version below. The old test pinned the per-element loop that this task removes; the guarantee it protected (a rotate cannot skip an element and still say it succeeded) now comes from the single call, which either rotates all or throws.

```csharp
    // Bug 1 (2026-09-29): RotateElementsAsync swallowed per-element failures with `catch { }` and then
    // returned Ok=true with the full requested count, so a rotate that moved 9 of 12 elements reported
    // "Rotated 12 elements". The set is now rotated in one call: Revit rotates every element or throws,
    // and the throw rolls the transaction back.
    [Fact]
    public void RotateElements_IsAllOrNothing()
    {
        var src = AdapterSource.Containing("public Task<CommandResultDto> RotateElementsAsync");
        var i = src.IndexOf("public Task<CommandResultDto> RotateElementsAsync", System.StringComparison.Ordinal);
        Assert.True(i >= 0, "RotateElementsAsync not found");
        var end = src.IndexOf("public Task<CommandResultDto> MirrorElementsAsync", i, System.StringComparison.Ordinal);
        Assert.True(end > i, "could not bound the RotateElementsAsync slice");
        var slice = src.Substring(i, end - i);

        Assert.DoesNotContain("foreach (var id in eids)", slice);
        Assert.DoesNotContain("RotateElement(doc, id, axis, rad)", slice);

        var rotate = slice.IndexOf("ElementTransformUtils.RotateElements(doc, eids, axis, rad);", System.StringComparison.Ordinal);
        Assert.True(rotate >= 0, "the set must be rotated in one call");
        var commit = slice.IndexOf("tx.Commit();", rotate, System.StringComparison.Ordinal);
        Assert.True(commit > rotate, "the commit follows the rotate");
        var rollback = slice.IndexOf("tx.RollBack();", commit, System.StringComparison.Ordinal);
        Assert.True(rollback > commit, "a throw from the rotate lands in the catch that rolls back");

        // The success count is only reachable on the all-rotated path.
        Assert.Contains("$\"Rotated {eids.Count} elements", slice);
    }
```

- [ ] **Step 2: Run to confirm they fail**

Run: the xUnit command.
Expected: `TransformTests` fail (names, `MoveElements(`, `RotateElements(`, "in place"); `RotateElements_IsAllOrNothing` fails.

- [ ] **Step 3: Bridge, fake and plugin**

`src/LoadingAI.Core/Revit/IRevitBridge.cs`, in `IRevitElementCommand` replace

```csharp
    Task<CommandResultDto> MoveElementsAsync(
        IReadOnlyList<string> elementIds, double dx, double dy, double dz,
        CancellationToken ct = default);
```

with

```csharp
    /// <summary>
    /// Moves the whole set in one call, so a host and the elements hosted on it move once. The vector
    /// is millimetres, like every other method on this bridge; the adapter converts.
    /// </summary>
    Task<CommandResultDto> MoveElementsAsync(
        IReadOnlyList<string> elementIds, double dxMm, double dyMm, double dzMm,
        CancellationToken ct = default);
```

`src/LoadingAI.Tests/Fakes/FakeRevitBridge.cs`: in `MoveElementsAsync` rename the parameters (`dx, dy, dz` to `dxMm, dyMm, dzMm`) and the tuple line to `MoveElementsCalls.Add((elementIds, dxMm, dyMm, dzMm));`. The tuple's element names stay `Dx, Dy, Dz`.

`Plugins/core-tools/Core/GeometryTools.cs`: replace

```csharp
        return WriteResult(await context.Bridge.MoveElementsAsync(eids, Metric.MmToFt(dx), Metric.MmToFt(dy), Metric.MmToFt(dz)));
```

with

```csharp
        return WriteResult(await context.Bridge.MoveElementsAsync(eids, dx, dy, dz));
```

and the three descriptions:

```csharp
                Description = "Move elements by vector (mm).",
```
becomes
```csharp
                Description = "Move elements by a vector (mm). The set moves together, so a wall and the doors hosted on it move once.",
```

```csharp
                Description = "Rotate elements around Z (pyRevit: Rotate). Params: elementIds, angleDegrees, originX,originY optional (else centroid).",
```
becomes
```csharp
                Description = "Rotate elements around a vertical axis. Params: elementIds, angleDegrees, originX,originY in mm (optional; default the centre of the elements' bounding boxes). The set rotates together.",
```

```csharp
                Description = "Mirror elements across line (pyRevit). Params: elementIds, x1,y1,x2,y2 in mm.",
```
becomes
```csharp
                Description = "Mirror elements in place across the vertical plane through the line (x1,y1)-(x2,y2), all in mm. Makes no copies.",
```

- [ ] **Step 4: Adapter**

`src/LoadingAI/Revit/AdapterRevitBridge.Elements.cs`: replace the whole `MoveElementsAsync` method with

```csharp
    public Task<CommandResultDto> MoveElementsAsync(
        IReadOnlyList<string> elementIds, double dxMm, double dyMm, double dzMm,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            if (elementIds == null || elementIds.Count == 0)
                return new CommandResultDto(false, "Provide elementIds.");

            using var tx = new Transaction(doc, $"LoadingAI for Revit: move {elementIds.Count} elements");
            tx.Start();
            try
            {
                var vec = new XYZ(Metric.MmToFt(dxMm), Metric.MmToFt(dyMm), Metric.MmToFt(dzMm));
                var ids = elementIds.Select(AllTools.ParseEid).ToList();
                // One call for the whole set: Revit moves a host and the elements hosted on it once.
                // Moving each id on its own moves a door twice when its wall is in the set as well.
                // All or nothing: a throw rolls the transaction back, so there is no partial move.
                ElementTransformUtils.MoveElements(doc, ids, vec);
                tx.Commit();
                return new CommandResultDto(true, $"Moved {ids.Count} element(s) by ({dxMm:0.##}, {dyMm:0.##}, {dzMm:0.##}) mm.", ids.Count);
            }
            catch (Exception ex)
            {
                try { tx.RollBack(); } catch { }
                return new CommandResultDto(false, $"Failed: {ex.Message}. No elements were moved.");
            }
        }, ct);
    }
```

`src/LoadingAI/Revit/AdapterRevitBridge.Create.cs`, `RotateElementsAsync`: replace

```csharp
                    // All-or-nothing: a rotate that silently skips elements reports a
                    // false success and leaves the model in a half-transformed state.
                    var failed = new List<string>();
                    foreach (var id in eids)
                    {
                        try { ElementTransformUtils.RotateElement(doc, id, axis, rad); }
                        catch (Exception elementEx) { failed.Add($"{id}: {elementEx.Message}"); }
                    }

                    if (failed.Count > 0)
                    {
                        try { tx.RollBack(); } catch { }
                        return new CommandResultDto(
                            false,
                            $"Failed: could not rotate {failed.Count} of {eids.Count} elements {angleDegrees}° ({string.Join("; ", failed)}). No elements were changed.");
                    }

                    tx.Commit();
                    return new CommandResultDto(true, $"Rotated {eids.Count} elements {angleDegrees}° around {origin}", eids.Count);
                }
                catch (Exception ex)
                {
                    try { tx.RollBack(); } catch { }
                    return new CommandResultDto(false, $"Failed: {ex.Message}");
                }
```

with

```csharp
                    // One call for the whole set, for the reason given on MoveElementsAsync. It rotates
                    // every element or throws, and the throw rolls the transaction back, so a
                    // half-rotated model cannot be reported as a success.
                    ElementTransformUtils.RotateElements(doc, eids, axis, rad);
                    tx.Commit();
                    return new CommandResultDto(
                        true,
                        $"Rotated {eids.Count} elements {angleDegrees}° around ({Metric.FtToMm(origin.X):F0}, {Metric.FtToMm(origin.Y):F0}) mm",
                        eids.Count);
                }
                catch (Exception ex)
                {
                    try { tx.RollBack(); } catch { }
                    return new CommandResultDto(false, $"Failed: {ex.Message}. No elements were changed.");
                }
```

`MirrorElementsAsync`: replace

```csharp
                    var newIds = ElementTransformUtils.MirrorElements(doc, eids, plane, false);
                    tx.Commit();
                    return new CommandResultDto(true, $"Mirrored {eids.Count} -> {newIds.Count} new elements", newIds.Count);
```

with

```csharp
                    // mirrorCopies is false: the elements are mirrored where they stand, nothing is copied.
                    ElementTransformUtils.MirrorElements(doc, eids, plane, false);
                    tx.Commit();
                    return new CommandResultDto(
                        true,
                        $"Mirrored {eids.Count} element(s) in place across the line ({x1:F0},{y1:F0})-({x2:F0},{y2:F0}) mm.",
                        eids.Count);
```

- [ ] **Step 5: Run and commit**

Run: the xUnit command.
Expected: PASS.

```bash
git add src Plugins
git commit -m "fix(transform): move and rotate the set in one call, millimetres on the bridge, honest mirror result"
```

- [ ] **Step 6: Revit smoke (R10). Required before merge**

Add to `PluginFixesInRevitTests`, below the Task 16 tests:

```csharp
    // ---- Task 17 -------------------------------------------------------------------------

    [Test]
    public void MoveElements_MovesAHostedDoorWithItsWallExactlyOnce()
    {
        var doc = _uiApp.ActiveUIDocument.Document;
        var symbol = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .FirstOrDefault(s => s.Family.FamilyPlacementType == FamilyPlacementType.OneLevelBasedHosted);
        if (symbol == null) Assert.Ignore("Load a door or window family (a wall-hosted one) into the open model.");

        using var group = new TransactionGroup(doc, "LoadingAI.RevitTests move wall and door");
        group.Start();
        try
        {
            var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).First();
            var wall = NewWall(doc, level, 0, 0, 6000, 0);
            FamilyInstance door;
            using (var tx = new Transaction(doc, "LoadingAI.RevitTests door"))
            {
                tx.Start();
                if (!symbol!.IsActive) symbol.Activate();
                door = doc.Create.NewFamilyInstance(
                    new XYZ(Metric.MmToFt(3000), 0, level.Elevation), symbol, wall, level,
                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                tx.Commit();
            }

            var before = ((LocationPoint)door.Location).Point.X;
            var result = Run(Adapter.MoveElementsAsync(new[] { wall.Id.ToString(), door.Id.ToString() }, 1000, 0, 0));

            Assert.That(result.Ok, Is.True, result.Message);
            var after = ((LocationPoint)door.Location).Point.X;
            Assert.That(after - before, Is.EqualTo(Metric.MmToFt(1000)).Within(1e-4), "the door moves with its wall once, not twice (R10)");
        }
        finally
        {
            group.RollBack();
        }
    }
```

Run the Revit smoke command. Expected: pass, or `Ignored` with the instruction to load a door family. If it is ignored, load one and run again. The review predicts that the old one-id-at-a-time loop moves the door 2 000 mm; run the test once on the base commit if you want that before-and-after in the PR description.

```bash
git add src/LoadingAI.RevitTests
git commit -m "test(revit): a hosted door moves once with its wall"
```

---

### Task 18: View tools that do what they say (PL-10, PL-11)

**Files:**
- Create: `src/LoadingAI.Core/Revit/ViewOverrideLedger.cs`
- Modify: `src/LoadingAI.Core/Revit/IRevitBridge.cs`
- Modify: `src/LoadingAI/Revit/AdapterRevitBridge.Views.cs`, `AdapterRevitBridge.Documents.cs`
- Modify: `Plugins/core-tools/Core/ViewTools.cs`, `DocumentTools.cs`, `Plugins/core-tools/plugin.json`
- Modify: `src/LoadingAI.Tests/Fakes/FakeRevitBridge.cs`
- Create: `src/LoadingAI.Tests/ViewOverrideLedgerTests.cs`, `ViewFixesTests.cs`
- Modify: `src/LoadingAI.RevitTests/PluginFixesInRevitTests.cs`

What is wrong today (`AdapterRevitBridge.Views.cs`, `.Documents.cs`):

- `override_color_in_view`, `color_by_parameter` and `color_by_construction_schedule` call `SetElementOverrides` / `SetCategoryOverrides`. Those overrides are saved in the project and sync to the team. Their descriptions say "temporary", and the schedule tool ends with "To reset: say 'reset view' (reset_temporary_view)", which clears hide/isolate and nothing else. No call sets a fill pattern id, so a surface with no pattern shows no colour in Shaded or Consistent Colors.
- `tag_all` places every tag at `LocationPoint ?? XYZ.Zero`: all wall tags land on the project origin. It swallows every failure and reports success with a count.
- `create_dimension` passes `new Reference(wall)`, an element reference that dimensions normally reject.
- `create_sheet` finds title blocks whose *family name contains "Title"*; "A1 Sheet" or "TB_A1" are never found. `place_view_on_sheet` has no `CanAddViewToSheet` check and cannot place a schedule.
- `reload_links` calls `RevitLinkType.Reload()` inside a transaction in a bare `try/catch` and returns `Ok = true` with "Reloaded 0/5" when every reload throws. Link loading is normally done outside a transaction.

The fix for the colour tools is not a new "temporary" mode. It is a ledger: the first time a tool overrides an element or category in a view, the view's previous override is remembered, and a new tool `reset_view_overrides` puts it back. The ledger lives for the Revit session; an override that predates it, or was made by hand, is never touched.

- [ ] **Step 1: Write the failing tests**

Create `src/LoadingAI.Tests/ViewOverrideLedgerTests.cs`:

```csharp
using System.Linq;
using LoadingAI.Revit;

namespace LoadingAI.Tests;

public class ViewOverrideLedgerTests
{
    [Fact]
    public void TheFirstValueWins_SoColouringTwiceStillRestoresTheOriginal()
    {
        var ledger = new ViewOverrideLedger<string>();
        ledger.Remember("7", ViewOverrideKey.Element("100"), "original");
        ledger.Remember("7", ViewOverrideKey.Element("100"), "red");

        var entry = Assert.Single(ledger.Snapshot("7"));
        Assert.Equal("e:100", entry.Key);
        Assert.Equal("original", entry.Value);
    }

    [Fact]
    public void AnElementAndACategoryWithTheSameNumberDoNotCollide()
    {
        var ledger = new ViewOverrideLedger<string>();
        ledger.Remember("7", ViewOverrideKey.Element("5"), "element");
        ledger.Remember("7", ViewOverrideKey.Category("5"), "category");

        Assert.Equal(2, ledger.Count("7"));
        Assert.True(ViewOverrideKey.IsCategory(ViewOverrideKey.Category("5")));
        Assert.False(ViewOverrideKey.IsCategory(ViewOverrideKey.Element("5")));
        Assert.Equal("5", ViewOverrideKey.IdOf(ViewOverrideKey.Category("5")));
    }

    [Fact]
    public void ViewsAreKeptApart_AndForgetDropsOnlyTheNamedOne()
    {
        var ledger = new ViewOverrideLedger<string>();
        ledger.Remember("7", ViewOverrideKey.Element("1"), "a");
        ledger.Remember("8", ViewOverrideKey.Element("1"), "b");

        ledger.Forget("7");

        Assert.Equal(0, ledger.Count("7"));
        Assert.Equal(1, ledger.Count("8"));
    }

    [Fact]
    public void TryTake_RemovesTheEntryAndSaysWhenThereWasNone()
    {
        var ledger = new ViewOverrideLedger<string>();
        ledger.Remember("7", ViewOverrideKey.Element("1"), "a");

        Assert.True(ledger.TryTake("7", ViewOverrideKey.Element("1"), out var before));
        Assert.Equal("a", before);
        Assert.False(ledger.TryTake("7", ViewOverrideKey.Element("1"), out _));
        Assert.Equal(0, ledger.Count("7"));
    }

    [Fact]
    public void ASnapshotIsACopy()
    {
        var ledger = new ViewOverrideLedger<string>();
        ledger.Remember("7", ViewOverrideKey.Element("1"), "a");
        var snapshot = ledger.Snapshot("7");

        ledger.Remember("7", ViewOverrideKey.Element("2"), "b");

        Assert.Single(snapshot);
        Assert.Equal(2, ledger.Snapshot("7").Count);
    }
}
```

Create `src/LoadingAI.Tests/ViewFixesTests.cs`:

```csharp
using System.IO;
using System.Linq;
using LoadingAI.Plugins;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

/// <summary>
/// The adapter needs a live document, so the Revit calls are asserted on source (CONTRIBUTING.md) and
/// checked for real by <c>PluginFixesInRevitTests</c>. The plugin side runs against the fake bridge.
/// </summary>
public class ViewFixesTests
{
    private static string Views() => AdapterSource.Containing("public Task<CommandResultDto> OverrideColorInViewAsync");

    private static string ColourTools() => SourceSlice.Between(Views(),
        "public Task<CommandResultDto> OverrideColorInViewAsync", "public Task<CommandResultDto> CreateSheetAsync");

    [Fact]
    public void EveryColourToolPaintsASolidFill()
    {
        var src = Views();
        Assert.Equal(3, SourceSlice.Count(src, "ColorOverride(doc,"));
        Assert.Equal(1, SourceSlice.Count(src, "SetSurfaceForegroundPatternColor("));
        Assert.Contains("SetSurfaceForegroundPatternId(", src);
        Assert.Contains("pattern.IsSolidFill", src);
    }

    [Fact]
    public void EveryOverrideGoesThroughTheLedger()
    {
        var tools = ColourTools();
        Assert.DoesNotContain("view.SetElementOverrides(", tools);
        Assert.DoesNotContain("view.SetCategoryOverrides(", tools);
        Assert.Contains("ApplyElementOverride(", tools);
        Assert.Contains("ApplyCategoryOverride(", tools);
        Assert.Contains("OverrideLedger.Remember(", Views());
    }

    [Fact]
    public void TheMisleadingResetHintIsGone()
    {
        var src = Views();
        Assert.DoesNotContain("say 'reset view' (reset_temporary_view)", src);
        Assert.Contains("reset_view_overrides", src);
    }

    [Fact]
    public void TagAll_PlacesEachTagOnItsElement_AndReportsWhatFailed()
    {
        var slice = SourceSlice.Between(Views(),
            "public Task<CommandResultDto> TagAllInActiveViewAsync", "private const int MaxTagAll");
        Assert.DoesNotContain("XYZ.Zero", slice);
        Assert.Contains("TagPoint(el, view)", slice);
        Assert.Contains("tagged == 0", slice);
        Assert.Contains("tx.RollBack();", slice);
        Assert.Contains("reasons.Add(", slice);
        Assert.DoesNotContain("catch { }", slice.Substring(0, slice.IndexOf("tx.Commit();", System.StringComparison.Ordinal)));
    }

    [Fact]
    public void Dimension_UsesFaceReferences_NotTheWallElement()
    {
        var slice = SourceSlice.Between(Views(),
            "public Task<CommandResultDto> CreateDimensionBetweenWallsAsync", "public Task<CommandResultDto> TagAllInActiveViewAsync");
        Assert.DoesNotContain("new Reference(w1)", slice);
        Assert.DoesNotContain("new Reference(w2)", slice);
        Assert.Contains("FacingFace(", slice);
        Assert.Contains("not parallel", slice);
        Assert.Contains("HostObjectUtils.GetSideFaces(", Views());
    }

    [Fact]
    public void CreateSheet_FindsTitleBlocksByCategory_AndListsWhatExists()
    {
        var slice = SourceSlice.Between(Views(),
            "public Task<CommandResultDto> CreateSheetAsync", "public Task<CommandResultDto> PlaceViewOnSheetAsync");
        Assert.DoesNotContain("FamilyName.Contains(\"Title\")", slice);
        Assert.Contains("OST_TitleBlocks", slice);
        Assert.Contains("Available:", slice);
    }

    [Fact]
    public void PlaceView_ChecksItCanBePlaced_AndPlacesSchedules()
    {
        var slice = SourceSlice.Between(Views(),
            "public Task<CommandResultDto> PlaceViewOnSheetAsync", "public Task<CommandResultDto> DuplicateViewAsync");
        Assert.Contains("Viewport.CanAddViewToSheet(", slice);
        Assert.Contains("ScheduleSheetInstance.Create(", slice);
    }

    [Fact]
    public void ReloadLinks_RunsOutsideATransaction_AndCountsOnlyRealLoads()
    {
        var src = AdapterSource.Containing("public Task<CommandResultDto> ReloadLinksAsync");
        var slice = SourceSlice.Between(src, "public Task<CommandResultDto> ReloadLinksAsync", "public Task<CommandResultDto> AssignWorksetAsync");
        Assert.DoesNotContain("new Transaction(", slice);
        Assert.Contains("LinkLoadResultType.LinkLoaded", slice);
        Assert.Contains("$\"Reloaded {ok}/{links.Count} RVT links\"", slice);
        Assert.Contains("failed.Add(", slice);
    }

    [Fact]
    public void TheToolTextNoLongerCallsSavedOverridesTemporary()
    {
        var text = File.ReadAllText(Path.Combine(AdapterSource.RepoRoot(), "Plugins", "core-tools", "Core", "ViewTools.cs"));
        Assert.DoesNotContain("Set temporary color override", text);
        Assert.DoesNotContain("pyRevit: Tag All", text);
        Assert.True(SourceSlice.Count(text, "reset_view_overrides") >= 4, "three colour tools, reset_temporary_view and the new tool name it");
    }

    private static ToolEntry Tool(FakeRevitBridge bridge, string name)
        => ViewTools.Create(new FakePluginContext(bridge)).First(e => e.Definition.Function.Name == name);

    [Fact]
    public async System.Threading.Tasks.Task ResetViewOverrides_IsAnL2Write_ThatCallsTheBridge()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = Tool(bridge, "reset_view_overrides");

        var result = await tool.ExecuteAsync("{}");

        Assert.Equal(ToolTier.L2, tool.Tier);
        Assert.Equal(1, bridge.ResetViewOverridesCallCount);
        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(3, result.ChangedCount);
    }

    [Fact]
    public async System.Threading.Tasks.Task ResetViewOverrides_WithoutAModel_FailsWithoutClaimingAWrite()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };

        var result = await Tool(bridge, "reset_view_overrides").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
    }

    [Fact]
    public void TheShippedManifestListsTheNewTool()
    {
        var json = File.ReadAllText(Path.Combine(AdapterSource.RepoRoot(), "Plugins", "core-tools", "plugin.json"));
        Assert.Contains("\"name\": \"reset_view_overrides\"", json);
    }

    [Fact]
    public void ReloadLinks_SaysItIsNotAnUndoableEdit()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = DocumentTools.Create(new FakePluginContext(bridge)).First(e => e.Definition.Function.Name == "reload_links");

        Assert.NotNull(tool.Describe);
        var summary = tool.Describe!("{}").GetAwaiter().GetResult();
        Assert.Contains("Ctrl+Z will not reverse", summary);
    }
}
```

- [ ] **Step 2: Run to confirm they fail**

Run: the xUnit command.
Expected: compile errors (`ViewOverrideLedger`, `ViewOverrideKey`, `FakeRevitBridge.ResetViewOverridesCallCount`) and then the source assertions fail.

- [ ] **Step 3: Core, interface, fake, plugin tool, manifest**

Create `src/LoadingAI.Core/Revit/ViewOverrideLedger.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace LoadingAI.Revit;

/// <summary>
/// Keys for <see cref="ViewOverrideLedger{T}"/>. An element and a category can carry the same number,
/// so the kind is part of the key.
/// </summary>
public static class ViewOverrideKey
{
    public static string Element(string id) => "e:" + id;

    public static string Category(string id) => "c:" + id;

    public static bool IsCategory(string key) => key.StartsWith("c:", StringComparison.Ordinal);

    public static string IdOf(string key) => key.Substring(2);
}

/// <summary>
/// What a view looked like before LoadingAI coloured it. The colour tools write graphic overrides into
/// the project, where they stay and sync like any other edit; this ledger remembers the override an
/// element or category had the first time a tool touched it, so <c>reset_view_overrides</c> can put
/// that back instead of clearing everything. It lives for the Revit session, and the first value wins:
/// colouring twice must not make the second colour the one to restore.
/// </summary>
/// <typeparam name="T">The host's override type. Core never needs to know what it holds.</typeparam>
public sealed class ViewOverrideLedger<T> where T : class
{
    private readonly object _gate = new object();
    private readonly Dictionary<string, Dictionary<string, T>> _byView =
        new Dictionary<string, Dictionary<string, T>>(StringComparer.Ordinal);

    public void Remember(string viewId, string key, T before)
    {
        lock (_gate)
        {
            if (!_byView.TryGetValue(viewId, out var entries))
            {
                entries = new Dictionary<string, T>(StringComparer.Ordinal);
                _byView[viewId] = entries;
            }

            if (!entries.ContainsKey(key))
                entries[key] = before;
        }
    }

    /// <summary>Removes and returns what was remembered for <paramref name="key"/>, if anything.</summary>
    public bool TryTake(string viewId, string key, out T? before)
    {
        lock (_gate)
        {
            before = null;
            if (!_byView.TryGetValue(viewId, out var entries) || !entries.TryGetValue(key, out var found))
                return false;

            before = found;
            entries.Remove(key);
            if (entries.Count == 0)
                _byView.Remove(viewId);
            return true;
        }
    }

    public IReadOnlyList<KeyValuePair<string, T>> Snapshot(string viewId)
    {
        lock (_gate)
        {
            return _byView.TryGetValue(viewId, out var entries)
                ? new List<KeyValuePair<string, T>>(entries)
                : new List<KeyValuePair<string, T>>();
        }
    }

    public int Count(string viewId)
    {
        lock (_gate)
            return _byView.TryGetValue(viewId, out var entries) ? entries.Count : 0;
    }

    public void Forget(string viewId)
    {
        lock (_gate)
            _byView.Remove(viewId);
    }
}
```

`src/LoadingAI.Core/Revit/IRevitBridge.cs`, in `IRevitViewCommand` after `ResetViewVisibilityAsync` add:

```csharp
    /// <summary>
    /// Puts back the graphic overrides the active view had before the colour tools first changed them
    /// in this Revit session. Overrides that predate the session, or were set by hand, are untouched.
    /// </summary>
    Task<CommandResultDto> ResetViewOverridesAsync(CancellationToken ct = default);
```

`src/LoadingAI.Tests/Fakes/FakeRevitBridge.cs`, after `ResetViewVisibilityAsync` add:

```csharp
    public int ResetViewOverridesCallCount { get; private set; }

    public CommandResultDto? NextResetViewOverridesResult { get; set; }

    public Task<CommandResultDto> ResetViewOverridesAsync(CancellationToken ct = default)
    {
        ResetViewOverridesCallCount++;
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        return Task.FromResult(NextResetViewOverridesResult
            ?? new CommandResultDto(true, "Restored 3 override(s) in 'Level 1'.", 3, "Level 1"));
    }
```

`Plugins/core-tools/Core/ViewTools.cs`: in `Create` add `yield return CreateResetViewOverrides(context);` after `CreateColorByParameter(context)`, and add above `CreateCreateSheet`:

```csharp
    internal static ToolEntry CreateResetViewOverrides(IPluginContext context) => new()
    {
        Tier = ToolTier.L2,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "reset_view_overrides",
                Description = "Put back the graphic overrides the active view had before override_color_in_view, color_by_parameter or color_by_construction_schedule changed them. Works for this Revit session only; Ctrl+Z also undoes a colouring. Does not touch temporary hide/isolate (use reset_temporary_view).",
                Parameters = new { type = "object", properties = new { }, required = Array.Empty<string>() }
            }
        },
        Handler = async _ => (await ResetViewOverridesAsync(context)).Text,
        StructuredHandler = _ => ResetViewOverridesAsync(context)
    };

    private static async Task<ToolExecutionResult> ResetViewOverridesAsync(IPluginContext context)
        => WriteResult(await context.Bridge.ResetViewOverridesAsync());
```

`Plugins/core-tools/plugin.json`: add after the `color_by_parameter` entry (same indentation as its neighbours):

```json
    {
      "name": "reset_view_overrides",
      "tier": "L2",
      "description": "Put back the active view's graphic overrides from before LoadingAI coloured it.",
      "enabledByDefault": true,
      "tags": [
        "view",
        "write"
      ]
    },
```

- [ ] **Step 4: The adapter — colour helpers, the ledger and the reset**

In `src/LoadingAI/Revit/AdapterRevitBridge.Views.cs`, add `using System.Globalization;` is **not** needed. Insert at the top of the class body, before `IsolateInViewAsync`:

```csharp
    // ---- colour overrides: solid fill, and a ledger so they can be undone --------------------------

    /// <summary>What each view looked like before a colour tool first touched it. This Revit session only.</summary>
    private static readonly ViewOverrideLedger<OverrideGraphicSettings> OverrideLedger = new();

    private static string ViewKey(View view) => view.Id.ToString();

    /// <summary>The first solid drafting fill in the document, found by what it is rather than by its localised name.</summary>
    private static ElementId SolidFillPatternId(Document doc)
    {
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)))
        {
            var pattern = ((FillPatternElement)element).GetFillPattern();
            if (pattern.IsSolidFill && pattern.Target == FillPatternTarget.Drafting)
                return element.Id;
        }

        return ElementId.InvalidElementId;
    }

    /// <summary>
    /// Line and surface colour with a solid fill, so the colour shows in Shaded and Consistent Colors as
    /// well as in Hidden Line. A surface with no pattern shows only its edges.
    /// </summary>
    private static OverrideGraphicSettings ColorOverride(Document doc, Color color, int? lineWeight = null)
    {
        var ogs = new OverrideGraphicSettings();
        ogs.SetProjectionLineColor(color);
        ogs.SetSurfaceForegroundPatternColor(color);
        ogs.SetCutForegroundPatternColor(color);
        var solid = SolidFillPatternId(doc);
        if (solid != ElementId.InvalidElementId)
        {
            ogs.SetSurfaceForegroundPatternId(solid);
            ogs.SetCutForegroundPatternId(solid);
        }

        if (lineWeight.HasValue)
            ogs.SetProjectionLineWeight(lineWeight.Value);
        return ogs;
    }

    private static void ApplyElementOverride(View view, ElementId id, OverrideGraphicSettings settings)
    {
        OverrideLedger.Remember(ViewKey(view), ViewOverrideKey.Element(id.ToString()), view.GetElementOverrides(id));
        view.SetElementOverrides(id, settings);
    }

    private static void ApplyCategoryOverride(View view, ElementId categoryId, OverrideGraphicSettings settings)
    {
        OverrideLedger.Remember(ViewKey(view), ViewOverrideKey.Category(categoryId.ToString()), view.GetCategoryOverrides(categoryId));
        view.SetCategoryOverrides(categoryId, settings);
    }

    /// <summary>Back to what the view had before LoadingAI first touched the element; to no override when nothing was recorded.</summary>
    private static void ClearElementOverride(View view, ElementId id)
    {
        var restored = OverrideLedger.TryTake(ViewKey(view), ViewOverrideKey.Element(id.ToString()), out var before);
        view.SetElementOverrides(id, restored ? before! : new OverrideGraphicSettings());
    }

    private static void ClearCategoryOverride(View view, ElementId categoryId)
    {
        var restored = OverrideLedger.TryTake(ViewKey(view), ViewOverrideKey.Category(categoryId.ToString()), out var before);
        view.SetCategoryOverrides(categoryId, restored ? before! : new OverrideGraphicSettings());
    }

    public Task<CommandResultDto> ResetViewOverridesAsync(CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            var view = doc.ActiveView;
            if (view == null) return new CommandResultDto(false, "No active view.");
            if (view.IsTemplate) return new CommandResultDto(false, "Cannot reset overrides in a view template.");

            var entries = OverrideLedger.Snapshot(ViewKey(view));
            if (entries.Count == 0)
                return new CommandResultDto(
                    true,
                    $"Nothing to restore in '{view.Name}': LoadingAI has not coloured it in this Revit session. Overrides set earlier or by hand are left as they are.",
                    0,
                    view.Name);

            using var tx = new Transaction(doc, $"LoadingAI for Revit: restore overrides in {view.Name}");
            tx.Start();
            try
            {
                int restored = 0, gone = 0;
                foreach (var entry in entries)
                {
                    var id = AllTools.ParseEid(ViewOverrideKey.IdOf(entry.Key));
                    try
                    {
                        if (ViewOverrideKey.IsCategory(entry.Key)) view.SetCategoryOverrides(id, entry.Value);
                        else if (doc.GetElement(id) != null) view.SetElementOverrides(id, entry.Value);
                        else { gone++; continue; }
                        restored++;
                    }
                    catch
                    {
                        gone++;
                    }
                }

                tx.Commit();
                OverrideLedger.Forget(ViewKey(view));
                var msg = $"Restored {restored} override(s) in '{view.Name}'.";
                if (gone > 0) msg += $" {gone} element(s) no longer exist or could not be restored.";
                return new CommandResultDto(true, msg, restored, view.Name);
            }
            catch (Exception ex)
            {
                try { tx.RollBack(); } catch { }
                return new CommandResultDto(false, $"Failed: {ex.Message}");
            }
        }, ct);
    }

```

`OverrideColorInViewAsync`: replace

```csharp
                var colorName = string.IsNullOrWhiteSpace(color) ? "red" : color;
                Color clr = new Color(255, 0, 0);
                if (!reset) clr = ParseColor(colorName);
                var ogs = new OverrideGraphicSettings();
                if (!reset)
                {
                    ogs.SetProjectionLineColor(clr);
                    ogs.SetSurfaceForegroundPatternColor(clr);
                    ogs.SetCutForegroundPatternColor(clr);
                }
```

with

```csharp
                var colorName = string.IsNullOrWhiteSpace(color) ? "red" : color;
                OverrideGraphicSettings? ogs = reset ? null : ColorOverride(doc, ParseColor(colorName));
```

replace

```csharp
                        view.SetCategoryOverrides(catObj.Id, reset ? new OverrideGraphicSettings() : ogs);
```

with

```csharp
                        if (reset) ClearCategoryOverride(view, catObj.Id);
                        else ApplyCategoryOverride(view, catObj.Id, ogs!);
```

replace

```csharp
                    foreach (var id in ids) view.SetElementOverrides(id, reset ? new OverrideGraphicSettings() : ogs);
```

with

```csharp
                    foreach (var id in ids)
                    {
                        if (reset) ClearElementOverride(view, id);
                        else ApplyElementOverride(view, id, ogs!);
                    }
```

and end the two "set" messages with the same sentence. Replace

```csharp
                            : $"Set {colorName} override for {category} in '{view.Name}'.";
```

with

```csharp
                            : $"Set {colorName} override for {category} in '{view.Name}'. Saved in the view; reset_view_overrides puts the previous look back.";
```

and

```csharp
                        : $"Set {colorName} override for {ids.Count} element(s) in '{view.Name}'.";
```

with

```csharp
                        : $"Set {colorName} override for {ids.Count} element(s) in '{view.Name}'. Saved in the view; reset_view_overrides puts the previous look back.";
```

(The two reset messages keep their wording.)

`ColorByConstructionScheduleAsync`: replace

```csharp
                        var clr = colors[i];
                        var ogs = new OverrideGraphicSettings();
                        ogs.SetProjectionLineColor(clr);
                        ogs.SetSurfaceForegroundPatternColor(clr);
                        ogs.SetCutForegroundPatternColor(clr);
                        ogs.SetProjectionLineWeight(4);
                        foreach (var id in ids)
                        {
                            view.SetElementOverrides(id, ogs);
                            total++;
                        }
```

with

```csharp
                        var clr = colors[i];
                        var ogs = ColorOverride(doc, clr, lineWeight: 4);
                        foreach (var id in ids)
                        {
                            ApplyElementOverride(view, id, ogs);
                            total++;
                        }
```

and replace

```csharp
                    msg += "\nTo reset: say 'reset view' (reset_temporary_view).";
```

with

```csharp
                    msg += "\nSaved in the view's graphic overrides. To undo: reset_view_overrides, or Ctrl+Z.";
```

`ColorByParameterAsync`: replace

```csharp
                        var clr = colors[i];
                        var ogs = new OverrideGraphicSettings();
                        ogs.SetProjectionLineColor(clr);
                        ogs.SetSurfaceForegroundPatternColor(clr);
                        ogs.SetCutForegroundPatternColor(clr);
                        foreach (var id in kv.Value) { view.SetElementOverrides(id, ogs); total++; }
```

with

```csharp
                        var clr = colors[i];
                        var ogs = ColorOverride(doc, clr);
                        foreach (var id in kv.Value) { ApplyElementOverride(view, id, ogs); total++; }
```

and replace

```csharp
                        $"Colored {total} element(s) in '{view.Name}' by '{paramName}' ({groups.Count} values). Palette {paletteName}.\nLegend:\n" + string.Join("\n", legend),
```

with

```csharp
                        $"Colored {total} element(s) in '{view.Name}' by '{paramName}' ({groups.Count} values). Palette {paletteName}.\nLegend:\n" + string.Join("\n", legend)
                            + "\nSaved in the view's graphic overrides. To undo: reset_view_overrides, or Ctrl+Z.",
```

- [ ] **Step 5: The adapter — `tag_all`, `create_dimension`, sheets**

`TagAllInActiveViewAsync`: replace

```csharp
                var elems = new FilteredElementCollector(doc, view.Id)
                    .OfCategory(bic.Value).WhereElementIsNotElementType().ToElements().Take(50).ToList();
                int tagged = 0;
                using var tx = new Transaction(doc, "LoadingAI for Revit: tag all");
                tx.Start();
                try
                {
                    foreach (var el in elems)
                    {
                        try
                        {
                            var loc = (el.Location as LocationPoint)?.Point ?? XYZ.Zero;
                            IndependentTag.Create(doc, view.Id, new Reference(el), true, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, loc);
                            tagged++;
                        }
                        catch { }
                    }
                    tx.Commit();
                    return new CommandResultDto(true, $"Tagged {tagged}/{elems.Count} {category} in {view.Name}", tagged);
                }
```

with

```csharp
                var all = new FilteredElementCollector(doc, view.Id)
                    .OfCategory(bic.Value).WhereElementIsNotElementType().ToElements();
                var elems = all.Take(MaxTagAll).ToList();
                if (elems.Count == 0)
                    return new CommandResultDto(false, $"No {category} are visible in '{view.Name}'.");
                int tagged = 0;
                var reasons = new List<string>();
                using var tx = new Transaction(doc, "LoadingAI for Revit: tag all");
                tx.Start();
                try
                {
                    foreach (var el in elems)
                    {
                        try
                        {
                            var at = TagPoint(el, view);
                            if (at == null) { reasons.Add($"{el.Id}: no location in this view"); continue; }
                            IndependentTag.Create(doc, view.Id, new Reference(el), true, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, at);
                            tagged++;
                        }
                        catch (Exception tagEx) { reasons.Add($"{el.Id}: {tagEx.Message}"); }
                    }

                    if (tagged == 0)
                    {
                        tx.RollBack();
                        return new CommandResultDto(false, $"Failed: tagged 0 of {elems.Count} {category} in '{view.Name}'. {Why(reasons)}");
                    }

                    tx.Commit();
                    var capped = all.Count > elems.Count ? $" (the first {elems.Count} of {all.Count}; the rest were not tagged)" : "";
                    var missed = tagged < elems.Count ? $" Not tagged: {elems.Count - tagged}. {Why(reasons)}" : "";
                    return new CommandResultDto(true, $"Tagged {tagged}/{elems.Count} {category} in {view.Name}{capped}.{missed}", tagged);
                }
```

and append, as the last members of the class (after `TagAllInActiveViewAsync`):

```csharp
    private const int MaxTagAll = 50;

    /// <summary>Where a tag for this element should sit: its point, the middle of its curve, or the middle of its box in the view.</summary>
    private static XYZ? TagPoint(Element el, View view)
    {
        if (el.Location is LocationPoint point) return point.Point;
        if (el.Location is LocationCurve { Curve: { } curve }) return curve.Evaluate(0.5, true);
        var box = el.get_BoundingBox(view);
        return box == null ? null : (box.Min + box.Max) * 0.5;
    }

    private static string Why(List<string> reasons)
        => reasons.Count == 0 ? "" : "Reasons: " + string.Join("; ", reasons.Take(3)) + (reasons.Count > 3 ? "; ..." : "") + ".";

    /// <summary>The side face of a wall whose normal points along <paramref name="facing"/>, as a reference a dimension accepts.</summary>
    private static Reference? FacingFace(Document doc, Wall wall, XYZ facing)
    {
        foreach (var layer in new[] { ShellLayerType.Exterior, ShellLayerType.Interior })
        {
            IList<Reference>? faces;
            try { faces = HostObjectUtils.GetSideFaces(wall, layer); }
            catch { continue; }
            if (faces == null || faces.Count == 0) continue;
            if (doc.GetElement(faces[0]).GetGeometryObjectFromReference(faces[0]) is PlanarFace face
                && face.FaceNormal.DotProduct(facing) > 0.5)
                return faces[0];
        }

        return null;
    }
```

`CreateDimensionBetweenWallsAsync`: replace the whole method with:

```csharp
    public Task<CommandResultDto> CreateDimensionBetweenWallsAsync(
        string wallId1, string wallId2, string? viewId = null,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            try
            {
                View? view = string.IsNullOrWhiteSpace(viewId)
                    ? uidoc!.ActiveView
                    : doc.GetElement(AllTools.ParseEid(viewId)) as View;
                if (view == null) return new CommandResultDto(false, "View not found.");
                if (view.ViewType is ViewType.ThreeD or ViewType.Schedule or ViewType.DrawingSheet or ViewType.Legend)
                    return new CommandResultDto(false, $"Dimensions cannot be created in a {view.ViewType} view. Use a plan, section or elevation.");
                var w1 = doc.GetElement(AllTools.ParseEid(wallId1)) as Wall;
                var w2 = doc.GetElement(AllTools.ParseEid(wallId2)) as Wall;
                if (w1 == null || w2 == null) return new CommandResultDto(false, "Walls not found.");
                if ((w1.Location as LocationCurve)?.Curve is not Line l1 || (w2.Location as LocationCurve)?.Curve is not Line l2)
                    return new CommandResultDto(false, "create_dimension supports straight walls only.");

                // Two parallel walls: dimension the clear distance between their facing faces.
                var d1 = l1.Direction;
                if (d1.CrossProduct(l2.Direction).GetLength() > 1e-3)
                    return new CommandResultDto(false, "The walls are not parallel. Dimension two parallel walls.");

                var normal = new XYZ(-d1.Y, d1.X, 0).Normalize();
                var across = normal.DotProduct(l2.Evaluate(0.5, true) - l1.Evaluate(0.5, true));
                if (Math.Abs(across) < 1e-6)
                    return new CommandResultDto(false, "The walls are on the same line; there is no distance to dimension.");
                var toward = across > 0 ? normal : normal.Negate();

                var face1 = FacingFace(doc, w1, toward);
                var face2 = FacingFace(doc, w2, toward.Negate());
                if (face1 == null || face2 == null)
                    return new CommandResultDto(false, "Could not find the facing wall faces to dimension.");

                // The dimension line runs across the walls, one foot beyond the end of the first wall.
                var anchor = l1.GetEndPoint(1) + d1 * 1.0;
                var line = Line.CreateBound(anchor, anchor + toward * Math.Abs(across));

                using var tx = new Transaction(doc, "LoadingAI for Revit: create dimension");
                tx.Start();
                try
                {
                    var references = new ReferenceArray();
                    references.Append(face1);
                    references.Append(face2);
                    var dim = doc.Create.NewDimension(view, line, references);
                    tx.Commit();
                    return new CommandResultDto(true, $"Created dimension {dim.Id} between {wallId1} and {wallId2} in {view.Name}", 1, ElementIds: new[] { dim.Id.ToString() });
                }
                catch (Exception inner)
                {
                    try { tx.RollBack(); } catch { }
                    return new CommandResultDto(false, $"Failed: {inner.Message}");
                }
            }
            catch (Exception failEx)
            {
                return new CommandResultDto(false, $"Failed: {failEx.Message}");
            }
        }, ct);
    }
```

`CreateSheetAsync`: replace

```csharp
                FamilySymbol? tb = null;
                if (!string.IsNullOrWhiteSpace(titleBlockName))
                {
                    tb = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
                        .Cast<FamilySymbol>()
                        .FirstOrDefault(s => s.FamilyName.Contains("Title") && s.Name.Equals(titleBlockName, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    tb = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
                        .WhereElementIsElementType().Cast<FamilySymbol>().FirstOrDefault();
                }
                if (tb == null) return new CommandResultDto(false, "No title block family found.");
```

with

```csharp
                var blocks = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .WhereElementIsElementType().Cast<FamilySymbol>().ToList();
                if (blocks.Count == 0)
                    return new CommandResultDto(false, "No title block family is loaded in this model.");
                FamilySymbol tb;
                if (string.IsNullOrWhiteSpace(titleBlockName))
                {
                    tb = blocks[0];
                }
                else
                {
                    // By the type name, or "Family: Type" as Revit shows it. Family names such as "A1 Sheet"
                    // carry no "Title", so the old name test never found them.
                    var matches = blocks.Where(s =>
                        s.Name.Equals(titleBlockName, StringComparison.OrdinalIgnoreCase)
                        || $"{s.FamilyName}: {s.Name}".Equals(titleBlockName, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matches.Count == 0)
                        return new CommandResultDto(false,
                            $"Title block '{titleBlockName}' not found. Available: {string.Join(", ", blocks.Take(10).Select(s => $"{s.FamilyName}: {s.Name}"))}.");
                    if (matches.Count > 1)
                        return new CommandResultDto(false,
                            $"Title block '{titleBlockName}' exists in {matches.Count} families ({string.Join(", ", matches.Select(s => s.FamilyName))}). Pass 'Family: Type'.");
                    tb = matches[0];
                }
```

`PlaceViewOnSheetAsync`: replace

```csharp
                using var tx = new Transaction(doc, $"LoadingAI for Revit: place {viewId} on {sheetId}");
                tx.Start();
                try
                {
                    var vp = Viewport.Create(doc, sheet.Id, view.Id, new XYZ(x, y, 0));
                    tx.Commit();
                    return new CommandResultDto(true, $"Placed view {viewId} on sheet {sheetId} at ({x},{y}) viewport {vp.Id}", 1, ElementIds: new[] { vp.Id.ToString() });
                }
```

with

```csharp
                // A schedule goes on a sheet as a ScheduleSheetInstance; every other view is a viewport,
                // and a view can sit on one sheet only.
                var point = new XYZ(x, y, 0);
                var isSchedule = view is ViewSchedule;
                if (!isSchedule && !Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                    return new CommandResultDto(false, $"View '{view.Name}' cannot be placed on sheet {sheet.SheetNumber}: it is already on a sheet, or its type cannot be placed.");
                using var tx = new Transaction(doc, $"LoadingAI for Revit: place {viewId} on {sheetId}");
                tx.Start();
                try
                {
                    var placed = isSchedule
                        ? ScheduleSheetInstance.Create(doc, sheet.Id, view.Id, point).Id
                        : Viewport.Create(doc, sheet.Id, view.Id, point).Id;
                    tx.Commit();
                    return new CommandResultDto(true, $"Placed {(isSchedule ? "schedule" : "view")} {viewId} on sheet {sheetId} at ({xMm:F0},{yMm:F0}) mm: {placed}", 1, ElementIds: new[] { placed.ToString() });
                }
```

- [ ] **Step 6: The adapter — `reload_links`**

`src/LoadingAI/Revit/AdapterRevitBridge.Documents.cs`: replace the whole `ReloadLinksAsync` method with:

```csharp
    public Task<CommandResultDto> ReloadLinksAsync(CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            try
            {
                var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType))
                    .Cast<RevitLinkType>().ToList();
                // Loading and reloading a link is not a model edit and is done outside a transaction.
                int ok = 0;
                var failed = new List<string>();
                foreach (var lt in links)
                {
                    try
                    {
                        var result = lt.Reload();
                        if (result.LoadResult == LinkLoadResultType.LinkLoaded) ok++;
                        else failed.Add($"{lt.Name}: {result.LoadResult}");
                    }
                    catch (Exception ex)
                    {
                        failed.Add($"{lt.Name}: {ex.Message}");
                    }
                }

                var message = $"Reloaded {ok}/{links.Count} RVT links";
                if (failed.Count > 0)
                    message += ". Failed: " + string.Join("; ", failed.Take(5)) + (failed.Count > 5 ? "; ..." : "");
                return new CommandResultDto(links.Count == 0 || ok > 0, message, ok);
            }
            catch (Exception failEx)
            {
                return new CommandResultDto(false, $"Failed: {failEx.Message}");
            }
        }, ct);
    }
```

`Plugins/core-tools/Core/DocumentTools.cs`, `CreateReloadLinks`: replace the description with `"Reload every Revit link from its saved path. Reports how many loaded and which failed. Not an undoable edit."` and add, after `Tier = ToolTier.L2,`:

```csharp
            Describe = _ => Task.FromResult<string?>(
                "Reload every Revit link from its saved path.\nThis is not a model edit, so Ctrl+Z will not reverse it."),
```

- [ ] **Step 7: The tool text**

`Plugins/core-tools/Core/ViewTools.cs`: replace these descriptions.

`reset_temporary_view`: `"Reset (disable) temporary hide/isolate in the active view. Does not touch colour overrides; use reset_view_overrides for those."`

`override_color_in_view`: `"Colour elements or a whole category in the active view with a solid fill. The override is saved in the view and syncs like any edit; it is not a temporary mode. Undo with Ctrl+Z or reset_view_overrides. Use e.g. 'color those doors red'."` and its `reset` property description: `"True to put back what the view had before LoadingAI coloured these elements. Default false."`

`color_by_construction_schedule`: `"Bulk colour elements in the active view by construction schedule, bottom-up (earliest blue → latest red), in ONE transaction, with a solid fill saved in the view. Undo with reset_view_overrides. Provide startDate and intervalDays, optional categories and palette."`

`color_by_parameter`: `"Colour elements in the active view by any instance parameter value (Mark, Comments, Type, Level, …), one colour per distinct value, with a solid fill saved in the view. Undo with reset_view_overrides. Optional category; if omitted uses current selection, else Walls."`

`create_sheet`: `"Create a new sheet. titleBlockName is a title block type name or 'Family: Type'; when omitted the first loaded title block is used."`

`place_view_on_sheet`: `"Place a view or schedule onto a sheet at (x,y) mm from the sheet origin. A view can be on one sheet only; the result says why a view cannot be placed."`

`create_dimension`: `"Dimension the clear distance between two parallel straight walls, face to face, in a plan, section or elevation view (the active view when viewId is omitted)."`

`tag_all`: `"Tag up to 50 elements of a category in the active view, each tag placed on its element. Needs a loaded tag family for the category; the result says how many were tagged and why the rest were not."`

- [ ] **Step 8: Run and commit**

Run: the xUnit command.
Expected: PASS, including `ReloadLinks_ReportsActualSuccessCount` (the string it pins is kept), `FakeBridgeToolTests`, `L3CommandContractTests`, and the manifest test from Task 10 (`reset_view_overrides` is in `plugin.json` as L2, on by default, matching the code).

```bash
git add src Plugins
git commit -m "fix(views): solid colour with an undo ledger, tag_all on the element, dimension faces, title blocks by category, reload outside a transaction"
```

- [ ] **Step 9: Revit smoke (R4 to R7). Required before merge**

Add to `PluginFixesInRevitTests`:

```csharp
    // ---- Task 18 -------------------------------------------------------------------------

    [Test]
    public void ColourOverrides_AreSolid_AndResetViewOverridesPutsTheOldLookBack()
    {
        var doc = _uiApp.ActiveUIDocument.Document;
        var view = doc.ActiveView;
        if (view == null || view.IsTemplate) Assert.Ignore("Activate a normal, non-template view first.");

        using var group = new TransactionGroup(doc, "LoadingAI.RevitTests colour overrides");
        group.Start();
        try
        {
            var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).First();
            var wall = NewWall(doc, level, 0, 0, 4000, 0);
            Assert.That(view!.GetElementOverrides(wall.Id).ProjectionLineColor.IsValid, Is.False, "fixture: a new wall has no override");

            var painted = Run(Adapter.OverrideColorInViewAsync(new[] { wall.Id.ToString() }, null, "red"));
            Assert.That(painted.Ok, Is.True, painted.Message);
            var after = view.GetElementOverrides(wall.Id);
            Assert.That(after.ProjectionLineColor.Red, Is.EqualTo(255));
            Assert.That(after.SurfaceForegroundPatternId, Is.Not.EqualTo(ElementId.InvalidElementId), "a solid fill, so the colour shows when shaded (R7)");

            // A second colouring must not become the thing reset restores.
            Run(Adapter.OverrideColorInViewAsync(new[] { wall.Id.ToString() }, null, "blue"));

            var reset = Run(Adapter.ResetViewOverridesAsync());
            Assert.That(reset.Ok, Is.True, reset.Message);
            Assert.That(view.GetElementOverrides(wall.Id).ProjectionLineColor.IsValid, Is.False, "back to no override");
        }
        finally
        {
            group.RollBack();
        }
    }

    [Test]
    public void TagAll_PutsTheTagOnTheWall_NotOnTheOrigin()
    {
        var doc = _uiApp.ActiveUIDocument.Document;
        if (doc.ActiveView is not ViewPlan view) { Assert.Ignore("Activate a floor plan first."); return; }

        using var group = new TransactionGroup(doc, "LoadingAI.RevitTests tag_all");
        group.Start();
        try
        {
            var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).First();
            NewWall(doc, level, 40000, 0, 44000, 0);

            var result = Run(Adapter.TagAllInActiveViewAsync("Walls"));
            if (!result.Ok) Assert.Ignore("Load a Wall Tag family into the open model. " + result.Message);

            var tag = new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>().First();
            Assert.That(tag.TagHeadPosition.X, Is.EqualTo(Metric.MmToFt(42000)).Within(Metric.MmToFt(5000)), "R6: the tag sits on the wall, not at 0,0");
        }
        finally
        {
            group.RollBack();
        }
    }

    [Test]
    public void CreateDimension_BetweenTwoParallelWalls_MeasuresFaceToFace()
    {
        var doc = _uiApp.ActiveUIDocument.Document;
        if (doc.ActiveView is not ViewPlan) { Assert.Ignore("Activate a floor plan first."); return; }

        using var group = new TransactionGroup(doc, "LoadingAI.RevitTests create_dimension");
        group.Start();
        try
        {
            var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).First();
            var a = NewWall(doc, level, 0, 0, 5000, 0);
            var b = NewWall(doc, level, 0, 3000, 5000, 3000);

            var result = Run(Adapter.CreateDimensionBetweenWallsAsync(a.Id.ToString(), b.Id.ToString()));

            Assert.That(result.Ok, Is.True, result.Message + " (R5)");
            var dimension = (Dimension)doc.GetElement(AllTools.ParseEid(result.ElementIds![0]));
            var expected = Metric.MmToFt(3000) - (a.Width + b.Width) / 2;
            Assert.That(dimension.Value!.Value, Is.EqualTo(expected).Within(1e-3));
        }
        finally
        {
            group.RollBack();
        }
    }

    [Test]
    public void ReloadLinks_WithNoLinks_SucceedsWithZeroOfZero()
    {
        var result = Run(Adapter.ReloadLinksAsync());
        if (new FilteredElementCollector(_uiApp.ActiveUIDocument.Document).OfClass(typeof(RevitLinkType)).GetElementCount() > 0)
            Assert.Ignore("This model has links; the outcome depends on them. Check R4 by hand.");

        Assert.That(result.Ok, Is.True, result.Message);
        Assert.That(result.Message, Does.Contain("Reloaded 0/0 RVT links"));
    }

    [Test]
    public void CreateSheet_WithAnUnknownTitleBlock_SaysWhatIsLoaded()
    {
        var doc = _uiApp.ActiveUIDocument.Document;
        using var group = new TransactionGroup(doc, "LoadingAI.RevitTests create_sheet");
        group.Start();
        try
        {
            var result = Run(Adapter.CreateSheetAsync("LAI-T1", "Test", "No Such Block"));

            Assert.That(result.Ok, Is.False);
            Assert.That(result.Message, Does.Contain("itle block"));
        }
        finally
        {
            group.RollBack();
        }
    }
```

Run the Revit smoke command. Expected: the colour test passes; the tag, dimension and sheet tests pass or are `Ignored` with what to load. Then do R4 by hand: link any `.rvt` (Insert, Link Revit), ask for `reload_links` in chat, expect "Reloaded 1/1 RVT links"; unload the link's file on disk or point it at a missing path and expect "Reloaded 0/1 RVT links. Failed: …" and a failed tool result. Record R4 to R7 in the PR description.

```bash
git add src/LoadingAI.RevitTests
git commit -m "test(revit): colour overrides, tag_all, dimension, sheets and links"
```

---

### Task 19: structure-cuts that page, count failures and roll back cleanly (PL-07)

**Files:**
- Create: `src/LoadingAI.Core/Revit/StructureCuts/SplitCursorStore.cs`, `SplitForecast.cs`
- Modify: `src/LoadingAI.Core/Revit/RevitDtos.cs`
- Modify: `Plugins/structure-cuts/StructureCutsPlugin.cs`
- Modify: `src/LoadingAI/Revit/AdapterRevitBridge.Workflows.cs`
- Modify: `src/LoadingAI/Revit/StructureCuts/SplitScope.cs`, `SplitElements.cs`, `SplitOutcome.cs`, `CurveMemberSplit.cs`, `BeamSplit.cs`, `BeamCut.cs`, `FloorSplit.cs`, `LevelSplit.cs`, `GridSplit.cs`
- Create: `src/LoadingAI.Tests/SplitCursorStoreTests.cs`, `SplitRunTests.cs`
- Modify: `src/LoadingAI.RevitTests/PluginFixesInRevitTests.cs`

What is wrong today:

1. **Paging skips hosts.** `continueFrom` is an offset into a list that `SplitScope.Collect` collects again on every call. A write run deletes and re-creates walls, floors and unjoinable members, so re-created members leave the front of the list (new ids sort last) and unprocessed ones shift up. The report says `nextOffset = 50`, and the next call skips as many hosts as were re-created.
2. **Failures read as skips.** `SplitResultDto` has no failure count. A member that failed and rolled back is counted under `Skipped`, its cause is dropped, and `FromSplit` cannot report `Partial` (its own comment says so).
3. **A failed batch can stall in a dialog.** `WriteTx` deletes warnings only and sets `SetForcedModalHandling(true)`. Every member runs in a `SubTransaction`, but failures are processed when the *outer* transaction commits, so an error-severity failure shows Revit's dialog inside an `ExternalEvent` call and the call waits for a person. The return value of `tx.Commit()` is also never read.
4. **The dry run hides the costly part.** It says "Would split N beams into M pieces" and not how many members Revit cannot split in place, which are deleted and re-created, nor how many hosted elements, tags and dimensions that deletes. That is what the L3 prompt needs to show.

The design keeps every pinned text: `"Dry-run. "`, `"No changes made."`, `SplitElements.WriteTx(` between the dry-run guard and the first write, `SplitElements.Resolve(doc, ids, wanted, out missed)` in `SplitScope.cs`, 14 files in the host folder, `new Transaction(` only in `SplitElements.cs`, and a trailing optional `Failed` on `SplitResultDto` so existing positional constructions compile.

Decision 7 (above): an error-severity failure now **rolls the whole batch back and reports it** instead of showing Revit's dialog. Warnings are still deleted. A person who used to answer the dialog loses that choice; the report names the cause and the next step is a smaller `limit`.

- [ ] **Step 1: Write the failing tests**

Create `src/LoadingAI.Tests/SplitCursorStoreTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using LoadingAI.Revit.StructureCuts;

namespace LoadingAI.Tests;

public class SplitCursorStoreTests
{
    private static List<string> Ids(int n) => Enumerable.Range(1, n).Select(i => i.ToString()).ToList();

    [Fact]
    public void ThePageIsASliceOfTheCollectedList()
    {
        var page = new SplitCursorStore().Page("k", 0, 5, () => Ids(12));

        Assert.Equal(new[] { "1", "2", "3", "4", "5" }, page.Ids);
        Assert.Equal(0, page.Offset);
        Assert.Equal(12, page.Total);
        Assert.False(page.Reused);
    }

    [Fact]
    public void TheNextCallPagesThroughTheFrozenList_EvenWhenTheModelChangedUnderIt()
    {
        var store = new SplitCursorStore();
        store.Page("k", 0, 5, () => Ids(12));

        // After a write run five hosts were re-created. A list collected afresh would now be shorter in
        // front, and an offset of 5 into it would skip five hosts that were never processed.
        var collectedAgain = 0;
        var page = store.Page("k", 5, 5, () => { collectedAgain++; return Ids(7); });

        Assert.Equal(new[] { "6", "7", "8", "9", "10" }, page.Ids);
        Assert.Equal(0, collectedAgain);
        Assert.True(page.Reused);
        Assert.Equal(12, page.Total);
    }

    [Fact]
    public void OffsetZeroAlwaysStartsAFreshList()
    {
        var store = new SplitCursorStore();
        store.Page("k", 0, 5, () => Ids(12));

        var page = store.Page("k", 0, 5, () => Ids(20));

        Assert.Equal(20, page.Total);
        Assert.False(page.Reused);
    }

    [Fact]
    public void AnOffsetWithNoListCollectsOne()
    {
        var page = new SplitCursorStore().Page("k", 5, 5, () => Ids(12));

        Assert.Equal(5, page.Offset);
        Assert.Equal(new[] { "6", "7", "8", "9", "10" }, page.Ids);
        Assert.False(page.Reused);
    }

    [Fact]
    public void AnOffsetPastTheEndGivesAnEmptyPageAtTheEnd()
    {
        var store = new SplitCursorStore();
        store.Page("k", 0, 5, () => Ids(12));

        var page = store.Page("k", 99, 5, () => Ids(12));

        Assert.Empty(page.Ids);
        Assert.Equal(12, page.Offset);
    }

    [Fact]
    public void ALimitOfZeroOrLessGivesAnEmptyPage()
        => Assert.Empty(new SplitCursorStore().Page("k", 0, 0, () => Ids(3)).Ids);

    [Fact]
    public void ADifferentCommandScopeDocumentOrIdListHasItsOwnKey()
    {
        var none = new string[0];
        var baseKey = SplitCursorStore.KeyFor("split_by_levels", "m.rvt", "all", none);
        Assert.NotEqual(baseKey, SplitCursorStore.KeyFor("split_by_grids", "m.rvt", "all", none));
        Assert.NotEqual(baseKey, SplitCursorStore.KeyFor("split_by_levels", "n.rvt", "all", none));
        Assert.NotEqual(baseKey, SplitCursorStore.KeyFor("split_by_levels", "m.rvt", "selection", none));
        Assert.NotEqual(baseKey, SplitCursorStore.KeyFor("split_by_levels", "m.rvt", "all", new[] { "1" }));
        Assert.Equal(baseKey, SplitCursorStore.KeyFor("split_by_levels", "m.rvt", "all", none));
    }

    [Fact]
    public void AListExpiresAfterItsLifetime_AndUsingItKeepsItAlive()
    {
        var now = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
        var store = new SplitCursorStore(() => now, TimeSpan.FromMinutes(30));
        store.Page("k", 0, 5, () => Ids(12));

        now = now.AddMinutes(20);
        Assert.True(store.Page("k", 5, 5, () => Ids(12)).Reused);

        now = now.AddMinutes(20);
        Assert.True(store.Page("k", 10, 5, () => Ids(12)).Reused);

        now = now.AddMinutes(31);
        var collected = 0;
        var page = store.Page("k", 5, 5, () => { collected++; return Ids(9); });
        Assert.Equal(1, collected);
        Assert.False(page.Reused);
        Assert.Equal(9, page.Total);
    }

    [Fact]
    public void TheOldestListsGoFirstWhenTheStoreIsFull()
    {
        var now = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
        var store = new SplitCursorStore(() => now, TimeSpan.FromMinutes(30), capacity: 2);
        store.Page("a", 0, 1, () => Ids(3));
        now = now.AddSeconds(1);
        store.Page("b", 0, 1, () => Ids(3));
        now = now.AddSeconds(1);
        store.Page("c", 0, 1, () => Ids(3));

        Assert.True(store.Page("c", 1, 1, () => Ids(3)).Reused);
        Assert.False(store.Page("a", 1, 1, () => Ids(3)).Reused);
    }
}

public class SplitForecastTests
{
    [Fact]
    public void NothingToSplitSaysNothing()
        => Assert.Equal("", SplitForecast.Note(0, 0, 0));

    [Fact]
    public void InPlaceOnly_DoesNotWarnAboutDependents()
        => Assert.Equal(" In place: 5, re-created: 0.", SplitForecast.Note(5, 0, 0));

    [Fact]
    public void ReCreatedMembers_NameWhatTheyTakeWithThem()
    {
        var note = SplitForecast.Note(3, 2, 7);

        Assert.Contains("In place: 3", note);
        Assert.Contains("re-created: 2", note);
        Assert.Contains("deletes 7 dependent element(s)", note);
    }

    [Fact]
    public void ReCreatedWithNoDependents_AddsNoWarning()
        => Assert.Equal(" In place: 0, re-created: 3.", SplitForecast.Note(0, 3, 0));
}
```

Create `src/LoadingAI.Tests/SplitRunTests.cs`:

```csharp
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using LoadingAI.Plugins.StructureCuts;
using LoadingAI.Revit;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

public class SplitRunTests
{
    private static readonly string[] Algorithms = { "BeamSplit.cs", "BeamCut.cs", "FloorSplit.cs", "LevelSplit.cs", "GridSplit.cs" };

    private static string HostDir() => Path.Combine(AdapterSource.RepoRoot(), "src", "LoadingAI", "Revit", "StructureCuts");

    private static string Host(string file) => File.ReadAllText(Path.Combine(HostDir(), file));

    // ---- the result ----------------------------------------------------------------------

    private static (FakeRevitBridge Bridge, FakePluginContext Context) Setup()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        return (bridge, new FakePluginContext(bridge));
    }

    private static ToolEntry Tool(FakePluginContext context, string name)
        => new StructureCutsPlugin().CreateTools(context).Single(t => t.Definition.Function.Name == name);

    [Fact]
    public async System.Threading.Tasks.Task SomeSplitsAndSomeFailures_IsPartial_AndTheWritesStay()
    {
        var (bridge, context) = Setup();
        bridge.NextSplitBeamsResult = new SplitResultDto(
            "Split 2 beams into 5 pieces. Skipped: 1 split failed.", 2, 0, 1, new[] { "7001", "7002" }, null, 1);

        var result = await Tool(context, "split_beams_by_columns").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Partial, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal(2, result.ChangedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.True(result.IsSafeToAssimilate);
    }

    [Fact]
    public async System.Threading.Tasks.Task EveryAttemptFailing_IsFailedAndWroteNothing()
    {
        var (bridge, context) = Setup();
        bridge.NextSplitByLevelsResult = new SplitResultDto(
            "Split 0 elements into 0 pieces. Skipped: 4 split failed.", 0, 0, 4, new string[0], null, 4);

        var result = await Tool(context, "split_by_levels").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Equal(4, result.FailedCount);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async System.Threading.Tasks.Task SkipsAlone_StillAreNotFailures()
    {
        var (bridge, context) = Setup();
        bridge.NextSplitBeamsResult = new SplitResultDto("Split 2 beams. Skipped 3: pinned.", 2, 0, 3, new[] { "1", "2" });

        var result = await Tool(context, "split_beams_by_columns").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(0, result.FailedCount);
    }

    [Fact]
    public void TheOutcomeCarriesTheFailureCountToTheDto()
    {
        var outcome = AdapterSource.Containing("public Task<SplitResultDto?> SplitBeamsByColumnsAsync");
        Assert.Contains("outcome.NewIds, null, outcome.Failed)", outcome);

        var text = Host("SplitOutcome.cs");
        Assert.Contains("public int Failed { get; set; }", text);
        Assert.Contains("int skipped, int failed = 0)", text);

        foreach (var file in Algorithms)
            Assert.Matches(@"outcome\?\.Record\([^;]*(skippedFailed|skips\.Failed|skips\.DeleteFailed)\);", Host(file));
    }

    // ---- the cursor ----------------------------------------------------------------------

    [Fact]
    public void EveryHostCollectionGoesThroughTheCursor_UnderTheToolsOwnName()
    {
        var scope = Host("SplitScope.cs");
        Assert.Contains("Cursors.Page(", scope);
        Assert.Contains("SplitCursorStore.KeyFor(command,", scope);
        Assert.Contains("SplitElements.Resolve(doc, ids, wanted, out missed)", scope);
        Assert.Contains("public int NextOffset => Offset + Consumed;", scope);

        var plugin = File.ReadAllText(Path.Combine(AdapterSource.RepoRoot(), "Plugins", "structure-cuts", "StructureCutsPlugin.cs"));
        foreach (var (file, command) in new[]
        {
            ("BeamSplit.cs", "split_beams_by_columns"),
            ("BeamCut.cs", "split_secondary_beams_by_main_beams"),
            ("FloorSplit.cs", "split_floors_by_beams"),
            ("LevelSplit.cs", "split_by_levels"),
            ("GridSplit.cs", "split_by_grids"),
        })
        {
            var call = Regex.Match(Host(file), @"SplitScope\.Collect\([^;]*\);");
            Assert.True(call.Success, file);
            Assert.Contains($"\"{command}\"", call.Value);
            Assert.Contains($"Name = \"{command}\"", plugin);
        }
    }

    // ---- the transaction -----------------------------------------------------------------

    [Fact]
    public void EveryRunReadsTheCommit_AndReturnsTheReportWhenRevitRolledBack()
    {
        foreach (var file in Algorithms)
        {
            var text = Host(file);
            var write = text.IndexOf("SplitElements.WriteTx(", System.StringComparison.Ordinal);
            var commit = text.IndexOf("var rolledBack = tx.Commit();", System.StringComparison.Ordinal);
            var back = text.IndexOf("return rolledBack;", System.StringComparison.Ordinal);
            Assert.True(write >= 0 && commit > write && back > commit, file);
            Assert.Equal(1, SourceSlice.Count(text, "tx.Commit();"));
        }
    }

    [Fact]
    public void AnErrorRollsTheBatchBack_WithoutADialog_AndWarningsStillGoQuietly()
    {
        var text = Host("SplitElements.cs");
        Assert.Contains("FailureProcessingResult.ProceedWithRollBack", text);
        Assert.Contains("accessor.DeleteWarning(", text);
        Assert.Contains("TransactionStatus.Committed", text);
        Assert.DoesNotContain("class SkipWarnings", text);
        Assert.Equal(1, SourceSlice.Count(text, "new Transaction("));
    }

    // ---- the dry run ---------------------------------------------------------------------

    [Fact]
    public void TheDryRunKeepsItsPinnedWords_AndAddsTheForecast()
    {
        foreach (var file in new[] { "BeamSplit.cs", "BeamCut.cs", "FloorSplit.cs", "LevelSplit.cs", "GridSplit.cs" })
        {
            var text = Host(file);
            Assert.Contains("\"Dry-run. \"", text);
            Assert.Contains("No changes made.", text);
            Assert.Contains("SplitForecast.Note(", text);
        }

        Assert.Contains("public static (int Native, int Recreated, int Dependents) Forecast(", Host("CurveMemberSplit.cs"));
    }

    [Fact]
    public void TheHostFolderStillHasFourteenFiles()
        => Assert.Equal(14, Directory.GetFiles(HostDir(), "*.cs").Length);

    [Fact]
    public void ContinueFromTellsTheModelWhatItPagesThrough()
    {
        var plugin = File.ReadAllText(Path.Combine(AdapterSource.RepoRoot(), "Plugins", "structure-cuts", "StructureCutsPlugin.cs"));
        Assert.Contains("nextOffset the previous result named", plugin);
        Assert.DoesNotContain("Skip this many matching hosts before applying limit", plugin);
    }
}
```

- [ ] **Step 2: Run to confirm they fail**

Run: the xUnit command.
Expected: compile errors (`SplitCursorStore`, `SplitForecast`, `SplitResultDto` with seven arguments).

- [ ] **Step 3: Core — the cursor, the forecast, the DTO**

Create `src/LoadingAI.Core/Revit/StructureCuts/SplitCursorStore.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace LoadingAI.Revit.StructureCuts;

/// <summary>One slice of a frozen host list.</summary>
public sealed class CursorPage
{
    public CursorPage(IReadOnlyList<string> ids, int offset, int total, bool reused)
    {
        Ids = ids;
        Offset = offset;
        Total = total;
        Reused = reused;
    }

    /// <summary>The ids on this page. Some may no longer exist; the caller skips those and still counts them.</summary>
    public IReadOnlyList<string> Ids { get; }

    public int Offset { get; }

    /// <summary>The length of the whole frozen list.</summary>
    public int Total { get; }

    /// <summary>True when the list was taken by an earlier call and not collected again.</summary>
    public bool Reused { get; }
}

/// <summary>
/// Makes <c>continueFrom</c> mean the same thing on every call. A write run deletes and re-creates
/// walls and floors, so a list collected afresh on each call loses members from the front and every
/// unprocessed one shifts up; an offset into it then skips as many hosts as were re-created. The store
/// keeps the ids collected when a run starts (offset 0) and pages through that list on the following
/// calls. Offset 0 always collects again, so "start over" works. A list lives for
/// <see cref="DefaultLifetime"/> after its last use, and the store keeps at most a handful.
/// </summary>
public sealed class SplitCursorStore
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(30);
    public const int DefaultCapacity = 8;

    private sealed class Entry
    {
        public Entry(List<string> ids, DateTime now)
        {
            Ids = ids;
            LastUsedUtc = now;
        }

        public List<string> Ids { get; }

        public DateTime LastUsedUtc { get; set; }
    }

    private readonly object _gate = new object();
    private readonly Func<DateTime> _clock;
    private readonly TimeSpan _lifetime;
    private readonly int _capacity;
    private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

    public SplitCursorStore(Func<DateTime>? clock = null, TimeSpan? lifetime = null, int capacity = DefaultCapacity)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
        _lifetime = lifetime ?? DefaultLifetime;
        _capacity = Math.Max(1, capacity);
    }

    /// <summary>One list per command, document, scope and explicit id list, so two tools never share a cursor.</summary>
    public static string KeyFor(string command, string document, string scope, IReadOnlyList<string> ids)
        => string.Join("|", command, document, scope ?? "", string.Join(",", ids));

    public CursorPage Page(string key, int offset, int limit, Func<IReadOnlyList<string>> collect)
    {
        lock (_gate)
        {
            var now = _clock();
            foreach (var stale in _entries.Where(e => now - e.Value.LastUsedUtc > _lifetime).Select(e => e.Key).ToList())
                _entries.Remove(stale);

            var start = Math.Max(0, offset);
            Entry? entry = null;
            var reused = start > 0 && _entries.TryGetValue(key, out entry);
            if (!reused)
            {
                entry = new Entry(collect().ToList(), now);
                _entries[key] = entry;
                while (_entries.Count > _capacity)
                {
                    var oldest = _entries.OrderBy(e => e.Value.LastUsedUtc).First().Key;
                    _entries.Remove(oldest);
                }
            }

            entry!.LastUsedUtc = now;
            var list = entry.Ids;
            if (start > list.Count)
                start = list.Count;
            var take = Math.Max(0, Math.Min(limit, list.Count - start));
            return new CursorPage(list.GetRange(start, take), start, list.Count, reused);
        }
    }
}
```

Create `src/LoadingAI.Core/Revit/StructureCuts/SplitForecast.cs`:

```csharp
namespace LoadingAI.Revit.StructureCuts;

/// <summary>
/// The sentence a dry run adds so the person, and the L3 prompt, know what a write run would do to
/// members that cannot be split in place. Revit's own Split keeps the element, its host and everything
/// hanging off it. The fallback deletes the member and builds new ones, and whatever depended on the old
/// element (hosted doors, tags, dimensions) goes with it.
/// </summary>
public static class SplitForecast
{
    public static string Note(int inPlace, int reCreated, int dependents)
    {
        if (inPlace + reCreated == 0)
            return "";

        var text = $" In place: {inPlace}, re-created: {reCreated}.";
        if (reCreated > 0 && dependents > 0)
            text += $" Re-creating deletes {dependents} dependent element(s) (hosted elements, tags, dimensions).";
        return text;
    }
}
```

`src/LoadingAI.Core/Revit/RevitDtos.cs`: replace

```csharp
/// <summary>
/// Outcome of one structure-cuts run. <c>Report</c> is the algorithms' own prose,
/// verbatim; the counters are kept separate from it so a partial run cannot read as
/// a whole one. <c>Created</c> counts hosts replaced by more than one piece and
/// <c>Skipped</c> counts hosts left alone for any reason.
/// </summary>
public sealed record SplitResultDto(
    string Report,
    int Created,
    int Openings,
    int Skipped,
    IReadOnlyList<string> NewIds,
    string? Error = null);
```

with

```csharp
/// <summary>
/// Outcome of one structure-cuts run. <c>Report</c> is the algorithms' own prose,
/// verbatim; the counters are kept separate from it so a partial run cannot read as
/// a whole one. <c>Created</c> counts hosts replaced by more than one piece and
/// <c>Skipped</c> counts hosts left alone for any reason. <c>Failed</c> is the part of
/// <c>Skipped</c> that was attempted and rolled back; it is optional and defaulted so every
/// existing construction keeps compiling.
/// </summary>
public sealed record SplitResultDto(
    string Report,
    int Created,
    int Openings,
    int Skipped,
    IReadOnlyList<string> NewIds,
    string? Error = null,
    int Failed = 0);
```

`Plugins/structure-cuts/StructureCutsPlugin.cs`: replace the `ContinueFromProperty` description with

```csharp
        description = "Optional. Where to continue a capped run: pass the nextOffset the previous result named. Continuing pages through the list of hosts taken when the run started (kept for 30 minutes), so hosts that run re-created are not skipped. Default 0 starts a fresh list."
```

and in `FromSplit` replace

```csharp
        var writes = result.Created + result.Openings;
        return writes > 0
            ? ToolExecutionResult.Succeeded(result.Report, ToolWriteCertainty.Committed, writes)
            : ToolExecutionResult.Succeeded(result.Report, ToolWriteCertainty.NoWrite);
```

with

```csharp
        var writes = result.Created + result.Openings;
        if (result.Failed > 0)
        {
            // Some members were attempted and rolled back. If others were written, those writes stay in
            // the turn (Partial + Committed is safe to assimilate); if none were, nothing changed.
            return writes > 0
                ? ToolExecutionResult.Partial(result.Report, ToolWriteCertainty.Committed, writes, result.Failed)
                : ToolExecutionResult.Failed(result.Report, ToolWriteCertainty.NotCommitted, 0, result.Failed);
        }

        return writes > 0
            ? ToolExecutionResult.Succeeded(result.Report, ToolWriteCertainty.Committed, writes)
            : ToolExecutionResult.Succeeded(result.Report, ToolWriteCertainty.NoWrite);
```

and replace the paragraph in the doc comment above `FromSplit`

```csharp
    /// <c>Skipped</c> is deliberately not folded into the write count: a grouped, pinned or
    /// unsplittable member is left alone by design and named in the report, and the DTO carries
    /// no failure count at all, so treating a skip as a failure would invent one.
```

with

```csharp
    /// <c>Skipped</c> is deliberately not folded into the write count: a grouped, pinned or
    /// unsplittable member is left alone by design and named in the report, so a skip is not a
    /// failure. <c>Failed</c> is the part of Skipped that was attempted and rolled back; with writes
    /// it makes the run Partial, without them it is Failed.
```

- [ ] **Step 4: The host — outcome, run and commit**

`src/LoadingAI/Revit/StructureCuts/SplitOutcome.cs`: add the property and the parameter.

```csharp
    /// <summary>Hosts a write attempted and rolled back. They are part of <see cref="Skipped"/>.</summary>
    public int Failed { get; set; }
```

and replace the `Record` signature and body start

```csharp
    public void Record(IReadOnlyList<ElementId> created, int openings, int skipped)
    {
        Created = created.Count;
        Openings = openings;
        Skipped = skipped;
```

with

```csharp
    public void Record(IReadOnlyList<ElementId> created, int openings, int skipped, int failed = 0)
    {
        Created = created.Count;
        Openings = openings;
        Skipped = skipped;
        Failed = failed;
```

`src/LoadingAI/Revit/AdapterRevitBridge.Workflows.cs`, `RunSplit`: replace

```csharp
                return new SplitResultDto(report, outcome.Created, outcome.Openings, outcome.Skipped, outcome.NewIds);
```

with

```csharp
                return new SplitResultDto(report, outcome.Created, outcome.Openings, outcome.Skipped, outcome.NewIds, null, outcome.Failed);
```

The five `Record` calls (one hunk per file; each file has exactly one):

| File | Replace | With |
|---|---|---|
| `BeamSplit.cs` | `outcome?.Record(created, 0, skippedNoCurve + skippedNoCrossing + skippedUnchanged + skippedFailed);` | `outcome?.Record(created, 0, skippedNoCurve + skippedNoCrossing + skippedUnchanged + skippedFailed, skippedFailed);` |
| `BeamCut.cs` | `outcome?.Record(created, 0, skippedNoCurve + skippedNoPair + skippedFailed);` | `outcome?.Record(created, 0, skippedNoCurve + skippedNoPair + skippedFailed, skippedFailed);` |
| `FloorSplit.cs` | `outcome?.Record(created, openingsMade, skips.Total);` | `outcome?.Record(created, openingsMade, skips.Total, skips.Partial + skips.DeleteFailed);` |
| `LevelSplit.cs` | `outcome?.Record(created, openings, skips.Total);` | `outcome?.Record(created, openings, skips.Total, skips.Failed);` |
| `GridSplit.cs` | `outcome?.Record(created, openings, skips.Total);` | `outcome?.Record(created, openings, skips.Total, skips.Failed);` |

In `FloorSplit`, "piece not built" (`Partial`) and "original not deleted" (`DeleteFailed`) are failures of an attempted write. `SlopedMismatch` stays a skip: the rebuild is taken back on purpose because it would not match.

`src/LoadingAI/Revit/StructureCuts/SplitElements.cs`: replace `WriteTx` and `SkipWarnings`

```csharp
    public static Transaction WriteTx(Document doc, string name)
    {
        var tx = new Transaction(doc, name);
        var failures = tx.GetFailureHandlingOptions();
        failures.SetClearAfterRollback(true);
        failures.SetForcedModalHandling(true);
        failures.SetFailuresPreprocessor(new SkipWarnings());
        tx.SetFailureHandlingOptions(failures);
        return tx;
    }

    private sealed class SkipWarnings : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            var toDelete = new List<FailureMessageAccessor>();
            foreach (var failure in accessor.GetFailureMessages())
            {
                if (failure.GetSeverity() == FailureSeverity.Warning)
                    toDelete.Add(failure);
            }

            foreach (var failure in toDelete)
                accessor.DeleteWarning(failure);

            return FailureProcessingResult.Continue;
        }
    }
```

with

```csharp
    public static SplitTransaction WriteTx(Document doc, string name) => new SplitTransaction(doc, name);

    /// <summary>
    /// The one write transaction a split run opens. It owns the failure handling: warnings are deleted so
    /// they cannot abort a multi-member write, and an error Revit cannot resolve rolls the whole batch
    /// back without a dialog. <see cref="Commit"/> then returns the report to give instead of a success,
    /// so a run can never describe a write that did not land.
    /// </summary>
    internal sealed class SplitTransaction : IDisposable
    {
        private readonly Transaction _tx;
        private readonly FailureCollector _failures = new FailureCollector();

        public SplitTransaction(Document doc, string name)
        {
            _tx = new Transaction(doc, name);
            var options = _tx.GetFailureHandlingOptions();
            options.SetClearAfterRollback(true);
            options.SetForcedModalHandling(true);
            options.SetFailuresPreprocessor(_failures);
            _tx.SetFailureHandlingOptions(options);
        }

        public TransactionStatus Start() => _tx.Start();

        /// <returns>Null when the batch committed; otherwise the "Failed:" report the run must return.</returns>
        public string? Commit()
        {
            var status = _tx.Commit();
            if (status == TransactionStatus.Committed)
                return null;

            return "Failed: Revit rolled back the whole batch (" + status + ")" + _failures.Describe()
                + ". Nothing was changed. Try a smaller limit, or run dryRun first.";
        }

        public void Dispose() => _tx.Dispose();
    }

    private sealed class FailureCollector : IFailuresPreprocessor
    {
        private readonly List<string> _errors = new List<string>();

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            var warnings = new List<FailureMessageAccessor>();
            var hasError = false;
            foreach (var failure in accessor.GetFailureMessages())
            {
                if (failure.GetSeverity() == FailureSeverity.Warning)
                {
                    warnings.Add(failure);
                    continue;
                }

                hasError = true;
                var text = failure.GetDescriptionText();
                if (!string.IsNullOrWhiteSpace(text) && !_errors.Contains(text))
                    _errors.Add(text);
            }

            foreach (var warning in warnings)
                accessor.DeleteWarning(warning);

            // An error Revit cannot resolve would otherwise open its dialog inside this call and stall it
            // until someone answers. Roll the batch back and let Commit say why.
            return hasError ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }

        public string Describe()
            => _errors.Count == 0 ? "" : ": " + string.Join("; ", _errors.Take(3));
    }

    /// <summary>The element behind an id an earlier page of this run collected, or null when it no longer exists.</summary>
    public static Element? ElementOf(Document doc, string token)
    {
        try
        {
            return doc.GetElement(RevitParse.ParseEid(token));
        }
        catch
        {
            return null;
        }
    }
```

In each of the five algorithms replace the commit (one occurrence per file)

```csharp
            tx.Commit();
        }
```

with

```csharp
            var rolledBack = tx.Commit();
            if (rolledBack != null)
                return rolledBack;
        }
```

`return rolledBack;` leaves `outcome` untouched at zero and skips `SplitElements.Select`, and `FromSplit` reads the `Failed:` prefix as a failed run that wrote nothing.

- [ ] **Step 5: The host — the cursor**

`src/LoadingAI/Revit/StructureCuts/SplitScope.cs`: replace the `SplitBatch` constructors and `NextOffset`

```csharp
    public SplitBatch(List<Element> items, int offset, int total, IReadOnlyList<string> missed, int suppliedIds)
    {
        Items = items;
        Offset = offset;
        Total = total;
        Missed = missed;
        SuppliedIds = suppliedIds;
    }
```

with

```csharp
    public SplitBatch(
        List<Element> items, int offset, int total, IReadOnlyList<string> missed, int suppliedIds,
        int? consumed = null, bool reused = false)
    {
        Items = items;
        Offset = offset;
        Total = total;
        Missed = missed;
        SuppliedIds = suppliedIds;
        Consumed = consumed ?? items.Count;
        Reused = reused;
    }
```

and

```csharp
    public int NextOffset => Offset + Items.Count;
```

with

```csharp
    /// <summary>
    /// How many ids of the frozen list this page used, including hosts that no longer exist because an
    /// earlier page re-created them. The next call continues after all of them.
    /// </summary>
    public int Consumed { get; }

    /// <summary>True when this page continued a list taken by an earlier call.</summary>
    public bool Reused { get; }

    public int NextOffset => Offset + Consumed;
```

Replace `SplitScope.Collect` (the whole method, up to and including its closing brace) with

```csharp
    private static readonly SplitCursorStore Cursors = new();

    public static SplitBatch Collect(
        Document doc,
        UIDocument uidoc,
        Func<Element, bool> wanted,
        SplitOptions options,
        IReadOnlyList<string> ids,
        string command)
    {
        // Explicit ids are the work set. An empty Revit selection must not replace them, and a
        // miss is kept so the caller can name it instead of falling through to "select first".
        // The list is frozen when a run starts (offset 0): a write run re-creates hosts, so a list
        // collected afresh would shift under the offset and skip the hosts that moved up.
        var missed = new List<string>();
        var key = SplitCursorStore.KeyFor(command, DocumentKey(doc), options.Scope, ids);
        var page = Cursors.Page(key, options.ContinueFrom, options.Limit, () =>
        {
            List<Element> collected;
            if (ids.Count > 0)
            {
                collected = SplitElements.Resolve(doc, ids, wanted, out missed);
            }
            else
            {
                collected = ByScope(doc, uidoc, wanted, options.Scope);
            }

            return collected.Select(element => SplitElements.Fmt(element.Id)).ToList();
        });

        // A host an earlier page deleted and re-created is gone from the model: skipped here, but still
        // counted in the offset so the next call does not repeat or lose a place.
        var items = new List<Element>(page.Ids.Count);
        foreach (var token in page.Ids)
        {
            var element = SplitElements.ElementOf(doc, token);
            if (element != null && wanted(element))
                items.Add(element);
        }

        return new SplitBatch(
            items, page.Offset, page.Total, page.Reused ? Array.Empty<string>() : missed, ids.Count, page.Ids.Count, page.Reused);
    }

    private static string DocumentKey(Document doc)
        => string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName;
```

Add `using LoadingAI.Revit.StructureCuts;` is not needed: `SplitCursorStore` lives in Core under the same namespace as this file.

The five call sites gain the tool's own name as the last argument (replace the whole line):

| File | New line |
|---|---|
| `BeamSplit.cs` | `var batch = SplitScope.Collect(doc, uidoc, SplitElements.IsBeam, options, beamIds, "split_beams_by_columns");` |
| `BeamCut.cs` | `var batch = SplitScope.Collect(doc, uidoc, SplitElements.IsBeam, options, hostIds, "split_secondary_beams_by_main_beams");` |
| `FloorSplit.cs` | `var batch = SplitScope.Collect(doc, uidoc, SplitElements.IsFloorLike, options, floorIds, "split_floors_by_beams");` |
| `LevelSplit.cs` | `var batch = SplitScope.Collect(doc, uidoc, SplitElements.HostsIn(categories), options, elementIds, "split_by_levels");` |
| `GridSplit.cs` | `var batch = SplitScope.Collect(doc, uidoc, SplitElements.HostsIn(categories), options, elementIds, "split_by_grids");` |

Do not touch `LinkCutters` in `SplitScope.cs`; `StructureCutsLinkCutterTests` reads 400 characters from its signature.

- [ ] **Step 6: The host — the dry-run forecast**

`src/LoadingAI/Revit/StructureCuts/CurveMemberSplit.cs`: add above `SplitAtCuts`

```csharp
    /// <summary>
    /// What a write run would do with these plans, decided the way the run decides it, without touching
    /// the document: members Revit can split in place, members that are deleted and re-created, and
    /// what hangs off the ones that are. Storey stacks always delete the wall or column they replace.
    /// A member that passes <see cref="CanNativeSplit(FamilyInstance, bool)"/> can still fall back to
    /// re-creation at run time, so the re-created count is a floor.
    /// </summary>
    public static (int Native, int Recreated, int Dependents) Forecast(
        IEnumerable<Plan> plans, IEnumerable<Stack>? stacks = null, IEnumerable<ColumnStack>? columns = null)
    {
        int native = 0, recreated = 0, dependents = 0;
        foreach (var plan in plans)
        {
            if (plan.Host is FamilyInstance instance && CanNativeSplit(instance, requireUnjoined: false))
            {
                native++;
                continue;
            }

            recreated++;
            dependents += SplitElements.Dependent(plan.Host);
        }

        foreach (var stack in stacks ?? Enumerable.Empty<Stack>())
        {
            recreated++;
            dependents += SplitElements.Dependent(stack.Wall);
        }

        foreach (var stack in columns ?? Enumerable.Empty<ColumnStack>())
        {
            recreated++;
            dependents += SplitElements.Dependent(stack.Host);
        }

        return (native, recreated, dependents);
    }
```

Then one hunk per algorithm. Each keeps `"Dry-run. "` and the sentence ending `No changes made.` untouched and inserts the forecast after it. The names `forecastNative`, `forecastRebuilt`, `forecastLost` are chosen so they cannot clash with `native`, `recreated`, `dependents` and `lost`, which the same methods declare further down.

`BeamSplit.cs`: replace

```csharp
            var pieces = plans.Sum(plan => plan.Cuts.Count + 1);
            return "Dry-run. " + $"Would split {plans.Count} beams into {pieces} pieces. No changes made."
                + SkipNote(skippedNoCurve, skippedNoCrossing, 0)
```

with

```csharp
            var pieces = plans.Sum(plan => plan.Cuts.Count + 1);
            var (forecastNative, forecastRebuilt, forecastLost) = CurveMemberSplit.Forecast(plans);
            return "Dry-run. " + $"Would split {plans.Count} beams into {pieces} pieces. No changes made."
                + SplitForecast.Note(forecastNative, forecastRebuilt, forecastLost)
                + SkipNote(skippedNoCurve, skippedNoCrossing, 0)
```

`BeamCut.cs` (every secondary beam is re-created): replace

```csharp
            var pieces = plans.Count * 2;
            return "Dry-run. " + $"Would split {plans.Count} secondary beams into {pieces} pieces. No changes made."
                + SkipNote(skippedNoCurve, skippedNoPair, 0)
```

with

```csharp
            var pieces = plans.Count * 2;
            var forecastLost = plans.Sum(plan => SplitElements.Dependent(plan.Host));
            return "Dry-run. " + $"Would split {plans.Count} secondary beams into {pieces} pieces. No changes made."
                + SplitForecast.Note(0, plans.Count, forecastLost)
                + SkipNote(skippedNoCurve, skippedNoPair, 0)
```

`FloorSplit.cs` (every floor is re-created): replace

```csharp
            return "Dry-run. " + $"Would split {plans.Count} floors into {pieces} pieces. Openings: {openings}. No changes made."
                + notes
```

with

```csharp
            var forecastLost = plans.Sum(plan => SplitElements.Dependent(plan.Floor));
            return "Dry-run. " + $"Would split {plans.Count} floors into {pieces} pieces. Openings: {openings}. No changes made."
                + SplitForecast.Note(0, plans.Count, forecastLost)
                + notes
```

`LevelSplit.cs`: replace

```csharp
            var floorPieces = floorPlans.Sum(plan => plan.Regions.Count);
            return "Dry-run. "
                + $"Would split {curvePlans.Count + stackPlans.Count + columnStacks.Count} members into {curvePieces + stackPieces + columnPieces} pieces and {floorPlans.Count} floors into {floorPieces} pieces. No changes made."
                + notes;
```

with

```csharp
            var floorPieces = floorPlans.Sum(plan => plan.Regions.Count);
            var (forecastNative, forecastRebuilt, forecastLost) = CurveMemberSplit.Forecast(curvePlans, stackPlans, columnStacks);
            forecastRebuilt += floorPlans.Count;
            forecastLost += floorPlans.Sum(plan => SplitElements.Dependent(plan.Floor));
            return "Dry-run. "
                + $"Would split {curvePlans.Count + stackPlans.Count + columnStacks.Count} members into {curvePieces + stackPieces + columnPieces} pieces and {floorPlans.Count} floors into {floorPieces} pieces. No changes made."
                + SplitForecast.Note(forecastNative, forecastRebuilt, forecastLost)
                + notes;
```

`GridSplit.cs`: replace

```csharp
            var floorPieces = floorPlans.Sum(plan => plan.Regions.Count);
            return "Dry-run. "
                + $"Would split {curvePlans.Count} members into {curvePieces} pieces and {floorPlans.Count} floors into {floorPieces} pieces. No changes made."
                + notes;
```

with

```csharp
            var floorPieces = floorPlans.Sum(plan => plan.Regions.Count);
            var (forecastNative, forecastRebuilt, forecastLost) = CurveMemberSplit.Forecast(curvePlans);
            forecastRebuilt += floorPlans.Count;
            forecastLost += floorPlans.Sum(plan => SplitElements.Dependent(plan.Floor));
            return "Dry-run. "
                + $"Would split {curvePlans.Count} members into {curvePieces} pieces and {floorPlans.Count} floors into {floorPieces} pieces. No changes made."
                + SplitForecast.Note(forecastNative, forecastRebuilt, forecastLost)
                + notes;
```

- [ ] **Step 7: Run and commit**

Run: the xUnit command.
Expected: PASS, including `StructureCutsContractTests` (14 files; `Dry-run. `; `SplitElements.WriteTx(` before the first write; `new Transaction(` only in `SplitElements.cs`), `StructureCutsLinkCutterTests`, `StructureCutsExecutionResultTests` (`SkipsDoNotTurnARealSplitIntoAPartial` still holds: its `Failed` is 0).

```bash
git add src Plugins
git commit -m "fix(structure-cuts): a cursor that survives re-creation, failure counts, whole-batch rollback, dry-run forecast"
```

- [ ] **Step 8: Revit smoke (R8, plus the dry run). Required before merge**

Add to `PluginFixesInRevitTests`:

```csharp
    // ---- Task 19 -------------------------------------------------------------------------

    private SplitRequestDto LevelRequest(int limit, int continueFrom, bool dryRun)
        => new(null, null, new[] { "walls" }, "all", continueFrom, limit, dryRun, false, true);

    private static IEnumerable<Wall> AllWalls(Document doc)
        => new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>();

    [Test]
    public void SplitByLevels_InPages_LeavesNoWallUnsplit()
    {
        var doc = _uiApp.ActiveUIDocument.Document;
        using var group = new TransactionGroup(doc, "LoadingAI.RevitTests split paging");
        group.Start();
        try
        {
            var baseLevel = NewLevel(doc, "LAI-Split 0", 0);
            NewLevel(doc, "LAI-Split 1", 3000);
            NewLevel(doc, "LAI-Split 2", 6000);
            for (var i = 0; i < 12; i++)
                NewWall(doc, baseLevel, 0, i * 1000, 5000, i * 1000, heightMm: 9000);

            var offset = 0;
            var pages = 0;
            while (true)
            {
                var result = Run(Adapter.SplitByLevelsAsync(LevelRequest(limit: 5, continueFrom: offset, dryRun: false)))!;
                Assert.That(result.Report, Does.Not.StartWith("Failed"), result.Report);
                pages++;
                var next = Regex.Match(result.Report, @"nextOffset = (\d+)");
                if (!next.Success) break;
                offset = int.Parse(next.Groups[1].Value);
                Assert.That(pages, Is.LessThan(10), "paging must terminate");
            }

            Assert.That(pages, Is.EqualTo(3), "12 walls at 5 per page");
            var tall = AllWalls(doc).Where(w => w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() > Metric.MmToFt(3000) + 1e-3).ToList();
            Assert.That(tall, Is.Empty, "R8: every 9 m wall was cut at the storey planes; none was skipped by a shifted offset");
        }
        finally
        {
            group.RollBack();
        }
    }

    [Test]
    public void SplitByLevels_DryRun_ForecastsTheRebuildAndWritesNothing()
    {
        var doc = _uiApp.ActiveUIDocument.Document;
        using var group = new TransactionGroup(doc, "LoadingAI.RevitTests split dry run");
        group.Start();
        try
        {
            var baseLevel = NewLevel(doc, "LAI-Split 0", 0);
            NewLevel(doc, "LAI-Split 1", 3000);
            for (var i = 0; i < 3; i++)
                NewWall(doc, baseLevel, 0, i * 1000, 5000, i * 1000, heightMm: 6000);
            var before = AllWalls(doc).Count();

            var result = Run(Adapter.SplitByLevelsAsync(LevelRequest(limit: 50, continueFrom: 0, dryRun: true)))!;

            Assert.That(result.Report, Does.StartWith("Dry-run. "));
            Assert.That(result.Report, Does.Contain("No changes made."));
            Assert.That(result.Report, Does.Contain("re-created: 3"));
            Assert.That(result.Created, Is.Zero);
            Assert.That(AllWalls(doc).Count(), Is.EqualTo(before));
        }
        finally
        {
            group.RollBack();
        }
    }
```

Run the Revit smoke command. Expected: both pass. Before this task the first one leaves tall walls behind, which is the R8 defect.

R9 (an error-severity failure inside a batch) has no known recipe that reliably raises one, so it has no test. The path is covered by the text assertions and by review; when a field report shows "Revit rolled back the whole batch", attach it to PL-07. Record R8 and the dry-run output in the PR description.

```bash
git add src/LoadingAI.RevitTests
git commit -m "test(revit): split paging leaves no wall unsplit; dry run forecasts the rebuild"
```

---

## Done when

- CI is green on the PR for each task, and the PR description lists which of R2 to R10 ran, with the NUnit output or a note, and which were `Ignored` for want of a family.
- `docs/PROJECT_BUGS_AND_LIMITATIONS.md` marks PL-07 to PL-11 closed or names what remains (per-member failure reasons; the error-severity path not reproduced in a test).
- The main plan's execution handoff still holds: Tasks 1 to 15 do not depend on this file.
