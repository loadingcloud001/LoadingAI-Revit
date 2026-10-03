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
        yield return CreateWall(context);
        yield return CreateCreateFamilyInstance(context);
        yield return CreateDuplicateElement(context);
        yield return CreateFloorByOutline(context);
        yield return CreateRoof(context);
        yield return CreateGridLevel(context);
        yield return CreateFamilyInstanceArray(context);
        yield return CreateRotateElements(context);
        yield return CreateMirrorElements(context);
        yield return CreateJoinGeometry(context);
    }

    internal static ToolEntry CreateMoveElements(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        RequiresWriteConfirm = true,
        Describe = json => DescribeMove(context, json),
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "move_elements",
                Description = "Move elements by a vector (mm). The set moves together, so a wall and the doors hosted on it move once.",
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
        Handler = async json => (await MoveElementsAsync(context, json)).Text,
        StructuredHandler = json => MoveElementsAsync(context, json)
    };

    private static async Task<ToolExecutionResult> MoveElementsAsync(IPluginContext context, string json)
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
        catch (Exception ex)
        {
            return Refused($"Invalid args: {ex.Message}");
        }
        if (eids.Count == 0)
        {
            var sel = await context.Bridge.GetSelectionSummaryAsync();
            var truncated = SelectionGuards.TruncationRefusal(sel);
            if (truncated != null) return Refused(truncated);
            eids = sel.ElementIds.ToList();
        }
        if (eids.Count == 0) return Refused("Please select elements first.");
        return WriteResult(await context.Bridge.MoveElementsAsync(eids, dx, dy, dz));
    }

    internal static ToolEntry CreateDeleteElements(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        RequiresWriteConfirm = true,
        Describe = json => DescribeDelete(context, json),
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
        Handler = async json => (await DeleteElementsAsync(context, json)).Text,
        StructuredHandler = json => DeleteElementsAsync(context, json)
    };

    private static async Task<ToolExecutionResult> DeleteElementsAsync(IPluginContext context, string json)
    {
        List<string> eids = new();
        try
        {
            using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (d.RootElement.TryGetProperty("elementIds", out var arr) && arr.ValueKind == JsonValueKind.Array)
                eids = arr.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
        }
        catch (Exception ex)
        {
            return Refused($"Invalid args: {ex.Message}");
        }
        if (eids.Count == 0)
        {
            var sel = await context.Bridge.GetSelectionSummaryAsync();
            var truncated = SelectionGuards.TruncationRefusal(sel);
            if (truncated != null) return Refused(truncated);
            eids = sel.ElementIds.ToList();
        }
        if (eids.Count == 0) return Refused("Please select elements first.");
        return WriteResult(await context.Bridge.DeleteElementsAsync(eids));
    }

    internal static ToolEntry CreateWall(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        RequiresWriteConfirm = true,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "create_wall",
                Description = "Create a straight wall by start and end points (mm) on a level. heightMm defaults to 3000; wallTypeName defaults to the first basic wall type.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        levelName = new { type = "string", description = "Level name, e.g. Level 1" },
                        x1 = new { type = "number" }, y1 = new { type = "number" }, x2 = new { type = "number" }, y2 = new { type = "number" },
                        wallTypeName = new { type = "string", description = "Optional wall type name" },
                        heightMm = new { type = "number", description = "Optional wall height in mm, default 3000" }
                    },
                    required = new[] { "levelName", "x1", "y1", "x2", "y2" }
                }
            }
        },
        Handler = async json => (await CreateWallAsync(context, json)).Text,
        StructuredHandler = json => CreateWallAsync(context, json)
    };

    private static async Task<ToolExecutionResult> CreateWallAsync(IPluginContext context, string json)
    {
        string levelName = ""; double x1 = 0, y1 = 0, x2 = 0, y2 = 0; string? wallTypeName = null; double? heightMm = null;
        try
        {
            using var d = JsonDocument.Parse(json);
            levelName = d.RootElement.GetProperty("levelName").GetString() ?? "";
            x1 = d.RootElement.GetProperty("x1").GetDouble();
            y1 = d.RootElement.GetProperty("y1").GetDouble();
            x2 = d.RootElement.GetProperty("x2").GetDouble();
            y2 = d.RootElement.GetProperty("y2").GetDouble();
            if (d.RootElement.TryGetProperty("wallTypeName", out var w) && w.ValueKind == JsonValueKind.String) wallTypeName = w.GetString();
            if (d.RootElement.TryGetProperty("heightMm", out var h) && h.ValueKind == JsonValueKind.Number) heightMm = h.GetDouble();
        }
        catch (Exception ex)
        {
            return Refused($"Invalid args: {ex.Message}");
        }
        return WriteResult(await context.Bridge.CreateWallAsync(levelName, x1, y1, x2, y2, wallTypeName, heightMm));
    }

    internal static ToolEntry CreateCreateFamilyInstance(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        RequiresWriteConfirm = true,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "create_family_instance",
                Description = "Place a level-based family instance (column, furniture, equipment) by type name at a point (mm). z is the height above the level in mm, default 0. Hosted families such as doors and windows are refused. If several families share the type name, pass familyName.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        symbolName = new { type = "string", description = "Family type name, e.g. '600 x 600'" },
                        familyName = new { type = "string", description = "Family name, e.g. 'Concrete-Rectangular-Column'. Needed when several families have the same type name." },
                        levelName = new { type = "string", description = "Level name. May be omitted only when the model has a single level." },
                        x = new { type = "number" }, y = new { type = "number" }, z = new { type = "number", description = "Optional height above the level in mm, default 0" }
                    },
                    required = new[] { "symbolName", "x", "y" }
                }
            }
        },
        Handler = async json => (await CreateFamilyInstanceAsync(context, json)).Text,
        StructuredHandler = json => CreateFamilyInstanceAsync(context, json)
    };

    private static async Task<ToolExecutionResult> CreateFamilyInstanceAsync(IPluginContext context, string json)
    {
        string symbolName = "", familyName = "", levelName = ""; double x = 0, y = 0, z = 0;
        try
        {
            using var d = JsonDocument.Parse(json);
            symbolName = d.RootElement.GetProperty("symbolName").GetString() ?? "";
            if (d.RootElement.TryGetProperty("familyName", out var fn) && fn.ValueKind == JsonValueKind.String) familyName = fn.GetString() ?? "";
            if (d.RootElement.TryGetProperty("levelName", out var ln) && ln.ValueKind == JsonValueKind.String) levelName = ln.GetString() ?? "";
            x = d.RootElement.GetProperty("x").GetDouble();
            y = d.RootElement.GetProperty("y").GetDouble();
            if (d.RootElement.TryGetProperty("z", out var zz) && zz.ValueKind == JsonValueKind.Number) z = zz.GetDouble();
        }
        catch (Exception ex)
        {
            return Refused($"Invalid args: {ex.Message}");
        }
        return WriteResult(await context.Bridge.CreateFamilyInstanceAsync(symbolName, x, y, familyName, levelName, z));
    }

    internal static ToolEntry CreateDuplicateElement(IPluginContext context) => new()
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
        Handler = async json => (await DuplicateElementAsync(context, json)).Text,
        StructuredHandler = json => DuplicateElementAsync(context, json)
    };

    private static async Task<ToolExecutionResult> DuplicateElementAsync(IPluginContext context, string json)
    {
        string eidStr = "";
        try
        {
            using var d = JsonDocument.Parse(json);
            eidStr = d.RootElement.GetProperty("elementId").GetString() ?? "";
        }
        catch (Exception ex)
        {
            return Refused($"Invalid args: {ex.Message}");
        }
        return WriteResult(await context.Bridge.DuplicateElementAsync(eidStr));
    }

    internal static ToolEntry CreateFloorByOutline(IPluginContext context) => new()
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
        Handler = async json => (await CreateFloorByOutlineAsync(context, json)).Text,
        StructuredHandler = json => CreateFloorByOutlineAsync(context, json)
    };

    private static async Task<ToolExecutionResult> CreateFloorByOutlineAsync(IPluginContext context, string json)
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
        catch (Exception ex)
        {
            return Refused($"Invalid args: {ex.Message}");
        }
        return WriteResult(await context.Bridge.CreateFloorByOutlineAsync(lvl, x1, y1, x2, y2, ft));
    }

    internal static ToolEntry CreateRoof(IPluginContext context) => new()
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
        Handler = async json => (await CreateRoofAsync(context, json)).Text,
        StructuredHandler = json => CreateRoofAsync(context, json)
    };

    private static async Task<ToolExecutionResult> CreateRoofAsync(IPluginContext context, string json)
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
        catch (Exception ex)
        {
            return Refused($"Invalid args: {ex.Message}");
        }
        return WriteResult(await context.Bridge.CreateRoofAsync(lvl, x1, y1, x2, y2));
    }

    internal static ToolEntry CreateGridLevel(IPluginContext context) => new()
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
        Handler = async json => (await CreateGridLevelAsync(context, json)).Text,
        StructuredHandler = json => CreateGridLevelAsync(context, json)
    };

    private static async Task<ToolExecutionResult> CreateGridLevelAsync(IPluginContext context, string json)
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
        catch (Exception ex)
        {
            return Refused($"Invalid args: {ex.Message}");
        }
        return WriteResult(await context.Bridge.CreateGridLevelAsync(t, nm, x1, y1, x2, y2, elev));
    }

    internal static ToolEntry CreateFamilyInstanceArray(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "create_family_instance_array",
                Description = "Array level-based family instances along a line, count 1 to 50. Params: symbolName, familyName (needed when several families share the type name), startX,startY, endX,endY in mm, count, levelName (optional only when the model has one level). Hosted families are refused.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        symbolName = new { type = "string" },
                        startX = new { type = "number" }, startY = new { type = "number" },
                        endX = new { type = "number" }, endY = new { type = "number" },
                        count = new { type = "integer" },
                        familyName = new { type = "string" },
                        levelName = new { type = "string" }
                    },
                    required = new[] { "symbolName", "startX", "startY", "endX", "endY", "count" }
                }
            }
        },
        Handler = async json => (await CreateFamilyInstanceArrayAsync(context, json)).Text,
        StructuredHandler = json => CreateFamilyInstanceArrayAsync(context, json)
    };

    private static async Task<ToolExecutionResult> CreateFamilyInstanceArrayAsync(IPluginContext context, string json)
    {
        string sym = ""; double sx = 0, sy = 0, ex = 10, ey = 0; int cnt = 5; string? lvl = null; string? fam = null;
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
            if (d.RootElement.TryGetProperty("familyName", out var f) && f.ValueKind == JsonValueKind.String) fam = f.GetString();
        }
        catch (Exception ex2)
        {
            return Refused($"Invalid args: {ex2.Message}");
        }
        return WriteResult(await context.Bridge.CreateFamilyInstanceArrayAsync(sym, sx, sy, ex, ey, cnt, lvl, fam));
    }

    internal static ToolEntry CreateRotateElements(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        RequiresWriteConfirm = true,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "rotate_elements",
                Description = "Rotate elements around a vertical axis. Params: elementIds, angleDegrees, originX,originY in mm (optional; default the centre of the elements' bounding boxes). The set rotates together.",
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
        Handler = async json => (await RotateElementsAsync(context, json)).Text,
        StructuredHandler = json => RotateElementsAsync(context, json)
    };

    private static async Task<ToolExecutionResult> RotateElementsAsync(IPluginContext context, string json)
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
        catch (Exception ex)
        {
            return Refused($"Invalid args: {ex.Message}");
        }
        return WriteResult(await context.Bridge.RotateElementsAsync(ids, ang, ox, oy));
    }

    internal static ToolEntry CreateMirrorElements(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "mirror_elements",
                Description = "Mirror elements in place across the vertical plane through the line (x1,y1)-(x2,y2), all in mm. Makes no copies.",
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
        Handler = async json => (await MirrorElementsAsync(context, json)).Text,
        StructuredHandler = json => MirrorElementsAsync(context, json)
    };

    private static async Task<ToolExecutionResult> MirrorElementsAsync(IPluginContext context, string json)
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
        catch (Exception ex)
        {
            return Refused($"Invalid args: {ex.Message}");
        }
        return WriteResult(await context.Bridge.MirrorElementsAsync(ids, x1, y1, x2, y2));
    }

    internal static ToolEntry CreateJoinGeometry(IPluginContext context) => new()
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
        Handler = async json => (await JoinGeometryAsync(context, json)).Text,
        StructuredHandler = json => JoinGeometryAsync(context, json)
    };

    private static async Task<ToolExecutionResult> JoinGeometryAsync(IPluginContext context, string json)
    {
        string id1 = "", id2 = ""; bool j = true;
        try
        {
            using var d = JsonDocument.Parse(json);
            id1 = d.RootElement.GetProperty("elementId1").GetString() ?? "";
            id2 = d.RootElement.GetProperty("elementId2").GetString() ?? "";
            if (d.RootElement.TryGetProperty("join", out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False) j = v.GetBoolean();
        }
        catch (Exception ex)
        {
            return Refused($"Invalid args: {ex.Message}");
        }
        return WriteResult(await context.Bridge.JoinGeometryAsync(id1, id2, j));
    }

    private static JsonDocument? ParseArgs(string json)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json); }
        catch { return null; }
    }

    private static List<string> ReadIds(JsonElement? root)
    {
        var ids = new List<string>();
        if (root is { } r && r.TryGetProperty("elementIds", out var arr) && arr.ValueKind == JsonValueKind.Array)
            ids.AddRange(arr.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!));
        return ids;
    }

    private static async Task<string?> DescribeDelete(IPluginContext context, string json)
    {
        using var doc = ParseArgs(json);
        var ids = ReadIds(doc?.RootElement);
        const string cascade = "Hosted elements, tags and dimensions on them go too. Undo with Ctrl+Z.";
        if (ids.Count == 0)
        {
            var sel = await context.Bridge.GetSelectionSummaryAsync();
            if (sel.ElementCount == 0) return null;
            return $"Delete the {sel.ElementCount} selected element(s) ({string.Join(", ", sel.CategoryNames.Take(5))}).\n{cascade}";
        }

        var lines = new List<string>();
        foreach (var id in ids.Take(5))
        {
            var d = await context.Bridge.GetElementDetailsAsync(id);
            lines.Add(d == null || !string.IsNullOrEmpty(d.Error) ? $"{id} (not found)" : $"{d.Category} \"{d.Name}\" ({d.Id})");
        }
        var more = ids.Count > 5 ? $"\n  … and {ids.Count - 5} more." : "";
        return $"Delete {ids.Count} element(s):\n  " + string.Join("\n  ", lines) + more + "\n" + cascade;
    }

    private static async Task<string?> DescribeMove(IPluginContext context, string json)
    {
        using var doc = ParseArgs(json);
        if (doc == null) return null;
        double Num(string name) => doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        var ids = ReadIds(doc.RootElement);
        var count = ids.Count;
        var what = $"{count} element(s)";
        if (count == 0)
        {
            var sel = await context.Bridge.GetSelectionSummaryAsync();
            if (sel.ElementCount == 0) return null;
            count = sel.ElementCount;
            what = $"the {count} selected element(s) ({string.Join(", ", sel.CategoryNames.Take(4))})";
        }
        return $"Move {what} by dx {Num("dx")}, dy {Num("dy")}, dz {Num("dz")} mm.\nUndo with Ctrl+Z.";
    }

    /// <summary>
    /// Every geometry tool is a write returning CommandResultDto, so Ok decides commit versus
    /// not-committed and AffectedCount becomes the changed count. Ok=false wrote nothing and
    /// forces a turn rollback.
    /// </summary>
    private static ToolExecutionResult WriteResult(CommandResultDto result)
        => result.Ok
            ? ToolExecutionResult.Succeeded(result.Message, ToolWriteCertainty.Committed, result.AffectedCount)
            : ToolExecutionResult.Failed(result.Message, ToolWriteCertainty.NotCommitted);

    /// <summary>
    /// A parse guard or a precondition refusal. Neither carries the Failed prefix the legacy path
    /// looked for, so each used to read as a successful committed write. They reach no
    /// transaction, so they are Failed + NotCommitted.
    /// </summary>
    private static ToolExecutionResult Refused(string text)
        => ToolExecutionResult.Failed(text, ToolWriteCertainty.NotCommitted);
}
