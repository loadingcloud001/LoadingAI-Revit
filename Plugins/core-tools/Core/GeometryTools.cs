using System.Text.Json;
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
        yield return CreateFloorByOutline(context);
        yield return CreateRoof(context);
        yield return CreateGridLevel(context);
        yield return CreateFamilyInstanceArray(context);
        yield return CreateRotateElements(context);
        yield return CreateMirrorElements(context);
        yield return CreateSplitWall(context);
        yield return CreateJoinGeometry(context);
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
                var result = await context.Bridge.CreateWallAsync(levelName, x1, y1, x2, y2, wallTypeName);
                return result.Message;
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
                var result = await context.Bridge.CreateFamilyInstanceAsync(symbolName, x, y, familyName, levelName, z);
                return result.Message;
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
                var result = await context.Bridge.DuplicateElementAsync(eidStr);
                return result.Message;
            }
        };
    }

    internal static ToolEntry CreateFloorByOutline(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L3,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "create_floor_by_outline",
                    Description = "Create floor by rectangle outline (Dynamo: Floor.ByOutline). Params: levelName, x1,y1,x2,y2 in mm, floorTypeName optional.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            levelName = new { type = "string" },
                            x1 = new { type = "number" }, y1 = new { type = "number" }, x2 = new { type = "number" }, y2 = new { type = "number" },
                            floorTypeName = new { type = "string" }
                        },
                        required = new[] { "levelName", "x1", "y1", "x2", "y2" }
                    }
                }
            },
            Handler = async json =>
            {
                string lvl = ""; double x1 = 0, y1 = 0, x2 = 10, y2 = 10; string? ft = null;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    lvl = d.RootElement.GetProperty("levelName").GetString() ?? "";
                    x1 = d.RootElement.GetProperty("x1").GetDouble();
                    y1 = d.RootElement.GetProperty("y1").GetDouble();
                    x2 = d.RootElement.GetProperty("x2").GetDouble();
                    y2 = d.RootElement.GetProperty("y2").GetDouble();
                    if (d.RootElement.TryGetProperty("floorTypeName", out var v) && v.ValueKind == JsonValueKind.String) ft = v.GetString();
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                var result = await context.Bridge.CreateFloorByOutlineAsync(lvl, x1, y1, x2, y2, ft);
                return result.Message;
            }
        };
    }

    internal static ToolEntry CreateRoof(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L3,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "create_roof",
                    Description = "Create flat roof by outline (pyRevit). Params: levelName, x1,y1,x2,y2 in mm.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            levelName = new { type = "string" },
                            x1 = new { type = "number" }, y1 = new { type = "number" }, x2 = new { type = "number" }, y2 = new { type = "number" }
                        },
                        required = new[] { "levelName", "x1", "y1", "x2", "y2" }
                    }
                }
            },
            Handler = async json =>
            {
                string lvl = ""; double x1 = 0, y1 = 0, x2 = 10, y2 = 10;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    lvl = d.RootElement.GetProperty("levelName").GetString() ?? "";
                    x1 = d.RootElement.GetProperty("x1").GetDouble();
                    y1 = d.RootElement.GetProperty("y1").GetDouble();
                    x2 = d.RootElement.GetProperty("x2").GetDouble();
                    y2 = d.RootElement.GetProperty("y2").GetDouble();
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                var result = await context.Bridge.CreateRoofAsync(lvl, x1, y1, x2, y2);
                return result.Message;
            }
        };
    }

    internal static ToolEntry CreateGridLevel(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L3,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "create_grid_level",
                    Description = "Create grid and level (Dynamo: Grid.ByLine/ Level). Params: kind grid|level, name, x1,y1,x2,y2 or elevation in mm.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            kind = new { type = "string", description = "grid or level" },
                            name = new { type = "string" },
                            x1 = new { type = "number" }, y1 = new { type = "number" }, x2 = new { type = "number" }, y2 = new { type = "number" },
                            elevation = new { type = "number" }
                        },
                        required = new[] { "kind", "name" }
                    }
                }
            },
            Handler = async json =>
            {
                string t = "", nm = ""; double x1 = 0, y1 = 0, x2 = 10, y2 = 0, elev = 10;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    t = (d.RootElement.TryGetProperty("kind", out var k) ? k.GetString() : null)
                        ?? (d.RootElement.TryGetProperty("type", out var ty) ? ty.GetString() : null)
                        ?? "";
                    nm = d.RootElement.GetProperty("name").GetString() ?? "";
                    if (d.RootElement.TryGetProperty("x1", out var a) && a.ValueKind == JsonValueKind.Number) x1 = a.GetDouble();
                    if (d.RootElement.TryGetProperty("y1", out var b) && b.ValueKind == JsonValueKind.Number) y1 = b.GetDouble();
                    if (d.RootElement.TryGetProperty("x2", out var c) && c.ValueKind == JsonValueKind.Number) x2 = c.GetDouble();
                    if (d.RootElement.TryGetProperty("y2", out var d2) && d2.ValueKind == JsonValueKind.Number) y2 = d2.GetDouble();
                    if (d.RootElement.TryGetProperty("elevation", out var e) && e.ValueKind == JsonValueKind.Number) elev = e.GetDouble();
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                var result = await context.Bridge.CreateGridLevelAsync(t, nm, x1, y1, x2, y2, elev);
                return result.Message;
            }
        };
    }

    internal static ToolEntry CreateFamilyInstanceArray(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L3,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "create_family_instance_array",
                    Description = "Array family instances along line (Dynamo: FamilyInstance.ByPoint array). Params: symbolName, startX,startY, endX,endY in mm, count, levelName optional.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            symbolName = new { type = "string" },
                            startX = new { type = "number" }, startY = new { type = "number" },
                            endX = new { type = "number" }, endY = new { type = "number" },
                            count = new { type = "integer" },
                            levelName = new { type = "string" }
                        },
                        required = new[] { "symbolName", "startX", "startY", "endX", "endY", "count" }
                    }
                }
            },
            Handler = async json =>
            {
                string sym = ""; double sx = 0, sy = 0, ex = 10, ey = 0; int cnt = 5; string? lvl = null;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    sym = d.RootElement.GetProperty("symbolName").GetString() ?? "";
                    sx = d.RootElement.GetProperty("startX").GetDouble();
                    sy = d.RootElement.GetProperty("startY").GetDouble();
                    ex = d.RootElement.GetProperty("endX").GetDouble();
                    ey = d.RootElement.GetProperty("endY").GetDouble();
                    cnt = d.RootElement.GetProperty("count").GetInt32();
                    if (d.RootElement.TryGetProperty("levelName", out var l) && l.ValueKind == JsonValueKind.String) lvl = l.GetString();
                }
                catch (Exception ex2) { return $"Invalid args: {ex2.Message}"; }
                var result = await context.Bridge.CreateFamilyInstanceArrayAsync(sym, sx, sy, ex, ey, cnt, lvl);
                return result.Message;
            }
        };
    }

    internal static ToolEntry CreateRotateElements(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L3,
            RequiresWriteConfirm = true,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "rotate_elements",
                    Description = "Rotate elements around Z (pyRevit: Rotate). Params: elementIds, angleDegrees, originX,originY optional (else centroid).",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            elementIds = new { type = "array", items = new { type = "string" } },
                            angleDegrees = new { type = "number" },
                            originX = new { type = "number" }, originY = new { type = "number" }
                        },
                        required = new[] { "elementIds", "angleDegrees" }
                    }
                }
            },
            Handler = async json =>
            {
                List<string> ids = new(); double ang = 0; double? ox = null, oy = null;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    ids = d.RootElement.GetProperty("elementIds").EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
                    ang = d.RootElement.GetProperty("angleDegrees").GetDouble();
                    if (d.RootElement.TryGetProperty("originX", out var xv) && xv.ValueKind == JsonValueKind.Number) ox = xv.GetDouble();
                    if (d.RootElement.TryGetProperty("originY", out var yv) && yv.ValueKind == JsonValueKind.Number) oy = yv.GetDouble();
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                var result = await context.Bridge.RotateElementsAsync(ids, ang, ox, oy);
                return result.Message;
            }
        };
    }

    internal static ToolEntry CreateMirrorElements(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L3,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "mirror_elements",
                    Description = "Mirror elements across line (pyRevit). Params: elementIds, x1,y1,x2,y2 in mm.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            elementIds = new { type = "array", items = new { type = "string" } },
                            x1 = new { type = "number" }, y1 = new { type = "number" }, x2 = new { type = "number" }, y2 = new { type = "number" }
                        },
                        required = new[] { "elementIds", "x1", "y1", "x2", "y2" }
                    }
                }
            },
            Handler = async json =>
            {
                List<string> ids = new(); double x1 = 0, y1 = 0, x2 = 10, y2 = 0;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    ids = d.RootElement.GetProperty("elementIds").EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
                    x1 = d.RootElement.GetProperty("x1").GetDouble();
                    y1 = d.RootElement.GetProperty("y1").GetDouble();
                    x2 = d.RootElement.GetProperty("x2").GetDouble();
                    y2 = d.RootElement.GetProperty("y2").GetDouble();
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                var result = await context.Bridge.MirrorElementsAsync(ids, x1, y1, x2, y2);
                return result.Message;
            }
        };
    }

    internal static ToolEntry CreateSplitWall(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L3,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "split_wall",
                    Description = "Split wall at point (pyRevit). Params: wallId, x,y in mm.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            wallId = new { type = "string" },
                            x = new { type = "number" }, y = new { type = "number" }
                        },
                        required = new[] { "wallId", "x", "y" }
                    }
                }
            },
            Handler = async json =>
            {
                string wid = ""; double x = 0, y = 0;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    wid = d.RootElement.GetProperty("wallId").GetString() ?? "";
                    x = d.RootElement.GetProperty("x").GetDouble();
                    y = d.RootElement.GetProperty("y").GetDouble();
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                var result = await context.Bridge.SplitWallAsync(wid, x, y);
                return result.Message;
            }
        };
    }

    internal static ToolEntry CreateJoinGeometry(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L2,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "join_geometry",
                    Description = "Join two elements geometry (pyRevit). Params: elementId1, elementId2, join bool.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            elementId1 = new { type = "string" },
                            elementId2 = new { type = "string" },
                            join = new { type = "boolean" }
                        },
                        required = new[] { "elementId1", "elementId2" }
                    }
                }
            },
            Handler = async json =>
            {
                string id1 = "", id2 = ""; bool j = true;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    id1 = d.RootElement.GetProperty("elementId1").GetString() ?? "";
                    id2 = d.RootElement.GetProperty("elementId2").GetString() ?? "";
                    if (d.RootElement.TryGetProperty("join", out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False) j = v.GetBoolean();
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                var result = await context.Bridge.JoinGeometryAsync(id1, id2, j);
                return result.Message;
            }
        };
    }

}
