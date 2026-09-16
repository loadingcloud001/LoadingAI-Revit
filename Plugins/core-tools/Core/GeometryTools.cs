using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LoadingAI.Llm;
using LoadingAI.Revit;
using LoadingAI.Plugins;

namespace LoadingAI.Tools;

public static class GeometryTools
{
    public static IEnumerable<ToolEntry> Create(IPluginContext context)
    {
        yield return CreateMoveElements(context);
        yield return CreateDeleteElements(context);
        yield return CreateCreateWall(context);
        yield return CreateCreateFamilyInstance(context);
        yield return CreateDuplicateElement(context);
    }

    internal static ToolEntry CreateMoveElements(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L3,
            RequiresWriteConfirm = true,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "move_elements",
                    Description = "Move elements by vector (mm).",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            elementIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses current selection." },
                            dx = new { type = "number", description = "Delta X in mm" },
                            dy = new { type = "number", description = "Delta Y in mm" },
                            dz = new { type = "number", description = "Delta Z in mm" }
                        },
                        required = new[] { "dx", "dy", "dz" }
                    }
                }
            },
            Handler = async json =>
            {
                List<string> eids = new(); double dx = 0, dy = 0, dz = 0;
                try
                {
                    using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                    if (d.RootElement.TryGetProperty("elementIds", out var arr) && arr.ValueKind == JsonValueKind.Array)
                        eids = arr.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
                    dx = d.RootElement.GetProperty("dx").GetDouble();
                    dy = d.RootElement.GetProperty("dy").GetDouble();
                    dz = d.RootElement.GetProperty("dz").GetDouble();
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                if (eids.Count == 0)
                {
                    var sel = await context.Bridge.GetSelectionSummaryAsync();
                    eids = sel.ElementIds.ToList();
                }
                if (eids.Count == 0) return "Please select elements first.";
                var result = await context.Bridge.MoveElementsAsync(eids, Metric.MmToFt(dx), Metric.MmToFt(dy), Metric.MmToFt(dz));
                return result.Message;
            }
        };
    }

    internal static ToolEntry CreateDeleteElements(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L3,
            RequiresWriteConfirm = true,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "delete_elements",
                    Description = "Delete selected elements, or pass elementIds. Destructive; host confirms.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new { elementIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses current selection." } },
                        required = Array.Empty<string>()
                    }
                }
            },
            Handler = async json =>
            {
                List<string> eids = new();
                try
                {
                    using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                    if (d.RootElement.TryGetProperty("elementIds", out var arr) && arr.ValueKind == JsonValueKind.Array)
                        eids = arr.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                if (eids.Count == 0)
                {
                    var sel = await context.Bridge.GetSelectionSummaryAsync();
                    eids = sel.ElementIds.ToList();
                }
                if (eids.Count == 0) return "Please select elements first.";
                var result = await context.Bridge.DeleteElementsAsync(eids);
                return result.Message;
            }
        };
    }

    internal static ToolEntry CreateCreateWall(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L3,
            RequiresWriteConfirm = true,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "create_wall",
                    Description = "Create a wall by start/end points (mm) on a level.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            levelName = new { type = "string", description = "Level name, e.g. Level 1" },
                            x1 = new { type = "number" }, y1 = new { type = "number" }, x2 = new { type = "number" }, y2 = new { type = "number" },
                            wallTypeName = new { type = "string", description = "Optional wall type name" }
                        },
                        required = new[] { "levelName", "x1", "y1", "x2", "y2" }
                    }
                }
            },
            Handler = async json =>
            {
                string levelName = ""; double x1 = 0, y1 = 0, x2 = 0, y2 = 0; string? wallTypeName = null;
                try { using var d = JsonDocument.Parse(json); levelName = d.RootElement.GetProperty("levelName").GetString() ?? ""; x1 = d.RootElement.GetProperty("x1").GetDouble(); y1 = d.RootElement.GetProperty("y1").GetDouble(); x2 = d.RootElement.GetProperty("x2").GetDouble(); y2 = d.RootElement.GetProperty("y2").GetDouble(); if (d.RootElement.TryGetProperty("wallTypeName", out var w) && w.ValueKind == JsonValueKind.String) wallTypeName = w.GetString(); } catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                var x1mm = x1; var y1mm = y1; var x2mm = x2; var y2mm = y2;
                x1 = Metric.MmToFt(x1); y1 = Metric.MmToFt(y1); x2 = Metric.MmToFt(x2); y2 = Metric.MmToFt(y2);
                var result = await context.FullAccess!.RunRevitAsync(session =>
                {
                    var uiApp = session.As<UIApplication>();
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Please open a model first.";
                    try
                    {
                        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
                        var names = levels.Select(l => l.Name).ToList();
                        var resolved = LevelNames.Resolve(levelName, names);
                        if (resolved == null) return LevelNames.NotFound(levelName, names);
                        var level = levels.First(l => l.Name == resolved);
                        ElementId wallTypeId = ElementId.InvalidElementId;
                        if (!string.IsNullOrWhiteSpace(wallTypeName))
                        {
                            var wt = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().FirstOrDefault(t => t.Name.Equals(wallTypeName, StringComparison.OrdinalIgnoreCase));
                            if (wt == null) return $"Wall type '{wallTypeName}' not found.";
                            wallTypeId = wt.Id;
                        }
                        else
                        {
                            var wt = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().FirstOrDefault();
                            if (wt == null) return "No wall types found.";
                            wallTypeId = wt.Id;
                        }
                        var p1 = new XYZ(x1, y1, 0); var p2 = new XYZ(x2, y2, 0);
                        var line = Line.CreateBound(p1, p2);
                        using var tx = new Transaction(doc, "LoadingAI for Revit: create wall");
                        tx.Start();
                        var wall = Wall.Create(doc, line, wallTypeId, level.Id, 10, 0, false, false);
                        tx.Commit();
                        return $"Created wall {wall.Id} on {level.Name} from ({x1mm:F0},{y1mm:F0}) to ({x2mm:F0},{y2mm:F0}) mm type {wallTypeId}.";
                    }
                    catch (Exception ex) { return $"Failed: {ex.Message}"; }
                });
                return result;
            }
        };
    }

    internal static ToolEntry CreateCreateFamilyInstance(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L3,
            RequiresWriteConfirm = true,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "create_family_instance",
                    Description = "Place a family instance by symbol name at a point (mm) on a level.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            symbolName = new { type = "string", description = "Family symbol name, e.g. 'Single-Flush'" },
                            familyName = new { type = "string", description = "Family name, e.g. 'Door'" },
                            levelName = new { type = "string", description = "Level name" },
                            x = new { type = "number" }, y = new { type = "number" }, z = new { type = "number", description = "Optional Z, default 0" }
                        },
                        required = new[] { "symbolName", "x", "y" }
                    }
                }
            },
            Handler = async json =>
            {
                string symbolName = "", familyName = "", levelName = ""; double x = 0, y = 0, z = 0;
                try { using var d = JsonDocument.Parse(json); symbolName = d.RootElement.GetProperty("symbolName").GetString() ?? ""; if (d.RootElement.TryGetProperty("familyName", out var fn) && fn.ValueKind == JsonValueKind.String) familyName = fn.GetString() ?? ""; if (d.RootElement.TryGetProperty("levelName", out var ln) && ln.ValueKind == JsonValueKind.String) levelName = ln.GetString() ?? ""; x = d.RootElement.GetProperty("x").GetDouble(); y = d.RootElement.GetProperty("y").GetDouble(); if (d.RootElement.TryGetProperty("z", out var zz) && zz.ValueKind == JsonValueKind.Number) z = zz.GetDouble(); } catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                x = Metric.MmToFt(x); y = Metric.MmToFt(y); z = Metric.MmToFt(z);
                var result = await context.FullAccess!.RunRevitAsync(session =>
                {
                    var uiApp = session.As<UIApplication>();
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Please open a model first.";
                    try
                    {
                        var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToList();
                        FamilySymbol? sym = null;
                        if (!string.IsNullOrWhiteSpace(familyName)) sym = symbols.FirstOrDefault(s => s.FamilyName.Equals(familyName, StringComparison.OrdinalIgnoreCase) && s.Name.Equals(symbolName, StringComparison.OrdinalIgnoreCase));
                        sym ??= symbols.FirstOrDefault(s => s.Name.Equals(symbolName, StringComparison.OrdinalIgnoreCase));
                        if (sym == null) return $"Symbol '{symbolName}' not found. Available: {string.Join(", ", symbols.Take(10).Select(s => s.Name))}";
                        Level? lvl = null;
                        if (!string.IsNullOrWhiteSpace(levelName)) lvl = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault(l => l.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase));
                        if (lvl == null) lvl = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault();
                        if (lvl == null) return "No levels found.";
                        if (!sym.IsActive) sym.Activate();
                        var pt = new XYZ(x, y, z);
                        using var tx = new Transaction(doc, "LoadingAI for Revit: create instance");
                        tx.Start();
                        var inst = doc.Create.NewFamilyInstance(pt, sym, lvl, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                        tx.Commit();
                        return $"Created instance {inst.Id} '{symbolName}' at ({x},{y},{z}) on {lvl.Name}.";
                    }
                    catch (Exception ex) { return $"Failed: {ex.Message}"; }
                });
                return result;
            }
        };
    }

    internal static ToolEntry CreateDuplicateElement(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L3,
            RequiresWriteConfirm = true,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "duplicate_element",
                    Description = "Duplicate a view/sheet/element by Id.",
                    Parameters = new { type = "object", properties = new { elementId = new { type = "string" } }, required = new[] { "elementId" } }
                }
            },
            Handler = async json =>
            {
                string eidStr = "";
                try { using var d = JsonDocument.Parse(json); eidStr = d.RootElement.GetProperty("elementId").GetString() ?? ""; } catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                var result = await context.FullAccess!.RunRevitAsync(session =>
                {
                    var uiApp = session.As<UIApplication>();
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Please open a model first.";
                    try
                    {
                        var eid = AllTools.ParseEid(eidStr);
                        var el = doc.GetElement(eid);
                        if (el == null) return $"Element {eidStr} not found.";
                        using var tx = new Transaction(doc, "LoadingAI for Revit: duplicate");
                        tx.Start();
                        ICollection<ElementId> newIds;
                        if (el is View v) { var nid = v.Duplicate(ViewDuplicateOption.Duplicate); newIds = new[] { nid }; }
                        else { newIds = ElementTransformUtils.CopyElement(doc, eid, XYZ.Zero); }
                        tx.Commit();
                        return $"Duplicated {eidStr} -> {string.Join(", ", newIds.Select(id => id.ToString()))}";
                    }
                    catch (Exception ex) { return $"Failed: {ex.Message}"; }
                });
                return result;
            }
        };
    }

}
