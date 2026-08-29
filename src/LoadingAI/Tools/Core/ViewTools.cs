using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LoadingAI.Llm;
using LoadingAI.Revit;
using LoadingAI.Plugins;

namespace LoadingAI.Tools;

public static class ViewTools
{
    public static IEnumerable<ToolEntry> Create(IPluginContext context)
    {
        yield return CreateIsolateInView(context);
        yield return CreateHideInView(context);
        yield return CreateResetTemporaryView(context);
        yield return CreateOverrideColorInView(context);
        yield return CreateColorByConstructionSchedule(context);
        yield return CreateColorByParameter(context);
    }

    internal static ToolEntry CreateIsolateInView(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L2,
            RequiresWriteConfirm = true,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "isolate_in_view",
                    Description = "Temporarily isolate elements or a whole category in the active view. Use for 'isolate the walls' or 'isolate selection'.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            elementIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses current selection." },
                            category = new { type = "string", description = "Category to isolate, e.g. Walls. Alternative to elementIds." },
                            isolate = new { type = "boolean", description = "True to isolate, false to undo isolate. Default true." }
                        },
                        required = Array.Empty<string>()
                    }
                }
            },
            Handler = async json =>
            {
                List<string>? eids = null;
                string? cat = null;
                bool isolate = true;
                try
                {
                    using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                    if (d.RootElement.TryGetProperty("elementIds", out var arr) && arr.ValueKind == JsonValueKind.Array) eids = arr.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
                    if (d.RootElement.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String) cat = c.GetString();
                    if (d.RootElement.TryGetProperty("isolate", out var iso) && iso.ValueKind is JsonValueKind.True or JsonValueKind.False) isolate = iso.GetBoolean();
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                if ((eids == null || eids.Count == 0) && string.IsNullOrWhiteSpace(cat))
                {
                    var sel = await context.Bridge.GetSelectionSummaryAsync();
                    eids = sel.ElementIds.ToList();
                }
                if ((eids == null || eids.Count == 0) && string.IsNullOrWhiteSpace(cat)) return "Please select elements first.";
                var result = await context.Bridge.IsolateInViewAsync(eids, cat, isolate);
                return result.Message;
            }
        };
    }

    internal static ToolEntry CreateHideInView(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L2,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "hide_in_view",
                    Description = "Temporarily hide elements or a category in the active view. For 'hide all link model' use category='RVT Links' (aliases: link, links, revit links).",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            elementIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses current selection." },
                            category = new { type = "string", description = "Category to hide" }
                        },
                        required = Array.Empty<string>()
                    }
                }
            },
            Handler = async json =>
            {
                List<string>? eids = null;
                string? cat = null;
                try
                {
                    using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                    if (d.RootElement.TryGetProperty("elementIds", out var a) && a.ValueKind == JsonValueKind.Array) eids = a.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
                    if (d.RootElement.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String) cat = c.GetString();
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                if ((eids == null || eids.Count == 0) && string.IsNullOrWhiteSpace(cat))
                {
                    var sel = await context.Bridge.GetSelectionSummaryAsync();
                    eids = sel.ElementIds.ToList();
                }
                if ((eids == null || eids.Count == 0) && string.IsNullOrWhiteSpace(cat)) return "Please select elements first.";
                var result = await context.Bridge.HideInViewAsync(eids, cat);
                return result.Message;
            }
        };
    }

    internal static ToolEntry CreateResetTemporaryView(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L2,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "reset_temporary_view",
                    Description = "Reset (disable) temporary hide/isolate in the active view.",
                    Parameters = new { type = "object", properties = new { }, required = Array.Empty<string>() }
                }
            },
            Handler = async _ =>
            {
                var result = await context.Bridge.ResetViewVisibilityAsync();
                return result.Message;
            }
        };
    }

    internal static ToolEntry CreateOverrideColorInView(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L2,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "override_color_in_view",
                    Description = "Set temporary color override for elements or a whole category in the active view. Use e.g. 'color those doors red'.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            elementIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses current selection." },
                            category = new { type = "string", description = "Category to color. Alternative to elementIds." },
                            color = new { type = "string", description = "Color name (red, blue, green, yellow, orange) or hex #RRGGBB" },
                            reset = new { type = "boolean", description = "True to reset override. Default false." }
                        },
                        required = Array.Empty<string>()
                    }
                }
            },
            Handler = async json =>
            {
                List<string>? eids = null;
                string? cat = null, color = "red";
                bool reset = false;
                try
                {
                    using var d = JsonDocument.Parse(json);
                    if (d.RootElement.TryGetProperty("elementIds", out var a) && a.ValueKind == JsonValueKind.Array) eids = a.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
                    if (d.RootElement.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String) cat = c.GetString();
                    if (d.RootElement.TryGetProperty("color", out var col) && col.ValueKind == JsonValueKind.String) color = col.GetString();
                    if (d.RootElement.TryGetProperty("reset", out var r) && r.ValueKind is JsonValueKind.True or JsonValueKind.False) reset = r.GetBoolean();
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                if ((eids == null || eids.Count == 0) && string.IsNullOrWhiteSpace(cat))
                {
                    var sel = await context.Bridge.GetSelectionSummaryAsync();
                    eids = sel.ElementIds.ToList();
                }
                if ((eids == null || eids.Count == 0) && string.IsNullOrWhiteSpace(cat)) return "Please select elements first.";
                var result = await context.FullAccess!.RunRevitAsync(session =>
                {
                    var uiApp = session.As<UIApplication>();
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Please open a model first.";
                    var view = doc.ActiveView;
                    if (view == null) return "No active view.";
                    if (view.IsTemplate) return "Cannot override in view template.";
                    try
                    {
                        Color clr = new Color(255, 0, 0);
                        if (!reset && !string.IsNullOrWhiteSpace(color))
                        {
                            clr = ParseColor(color!);
                        }
                        var ogs = new OverrideGraphicSettings();
                        if (reset) { /* empty resets */ }
                        else
                        {
                            ogs.SetProjectionLineColor(clr);
                            ogs.SetSurfaceForegroundPatternColor(clr);
                            ogs.SetCutForegroundPatternColor(clr);
                        }

                        using var tx = new Transaction(doc, reset ? $"LoadingAI for Revit: reset color in {view.Name}" : $"LoadingAI for Revit: color {color} in {view.Name}");
                        tx.Start();
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(cat) && (eids == null || eids.Count == 0))
                            {
                                var bic = AllTools.ParseCategory(cat);
                                if (bic == null) { tx.RollBack(); return $"Unknown category '{cat}'."; }
                                var catObj = doc.Settings.Categories.get_Item(bic.Value);
                                if (catObj == null) { tx.RollBack(); return $"Category '{cat}' not found."; }
                                view.SetCategoryOverrides(catObj.Id, reset ? new OverrideGraphicSettings() : ogs);
                                tx.Commit();
                                return reset ? $"Reset color for {cat} in '{view.Name}'." : $"Set {color} override for {cat} in '{view.Name}'.";
                            }
                            else
                            {
                                var ids = eids!.Select(AllTools.ParseEid).ToList();
                                foreach (var id in ids) view.SetElementOverrides(id, reset ? new OverrideGraphicSettings() : ogs);
                                tx.Commit();
                                return reset ? $"Reset color for {ids.Count} element(s) in '{view.Name}'." : $"Set {color} override for {ids.Count} element(s) in '{view.Name}'.";
                            }
                        }
                        catch
                        {
                            try { tx.RollBack(); } catch { }
                            throw;
                        }
                    }
                    catch (Exception ex) { return $"Failed: {ex.Message}"; }
                });
                return result;
            }
        };
    }

    private static Color ParseColor(string s)
    {
        s = s.Trim().ToLowerInvariant();
        return s switch
        {
            "red" => new Color(255, 0, 0),
            "green" => new Color(0, 200, 0),
            "blue" => new Color(0, 0, 255),
            "yellow" => new Color(255, 255, 0),
            "orange" => new Color(255, 165, 0),
            "purple" => new Color(128, 0, 128),
            _ when s.StartsWith("#") && s.Length == 7 && int.TryParse(s.Substring(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var r) && int.TryParse(s.Substring(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var g) && int.TryParse(s.Substring(5, 2), System.Globalization.NumberStyles.HexNumber, null, out var b) => new Color((byte)r, (byte)g, (byte)b),
            _ => new Color(255, 0, 0)
        };
    }

    // ---------- param ----------


    internal static ToolEntry CreateColorByConstructionSchedule(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L2,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "color_by_construction_schedule",
                    Description = "Bulk color elements in active view by construction schedule bottom-up (earliest blue ??latest red) in ONE transaction. Avoids per-level find+override loops. Provide startDate and intervalDays, optional categories and palette.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            startDate = new { type = "string", description = "Start date for lowest level, e.g. 2024-01-08" },
                            intervalDays = new { type = "integer", description = "Days between levels, default 14" },
                            categories = new { type = "array", items = new { type = "string" }, description = "Categories to color, default [\"Walls\"] or all if you say all" },
                            palette = new { type = "string", description = "Palette: rainbow, blue_red, heat. Default rainbow." }
                        },
                        required = new[] { "startDate" }
                    }
                }
            },
            Handler = async json =>
            {
                string startDateStr = ""; int interval = 14; List<string>? cats = null; string palette = "rainbow";
                try
                {
                    using var d = JsonDocument.Parse(json);
                    startDateStr = d.RootElement.GetProperty("startDate").GetString() ?? "";
                    if (d.RootElement.TryGetProperty("intervalDays", out var iv) && iv.ValueKind == JsonValueKind.Number) interval = iv.GetInt32();
                    if (d.RootElement.TryGetProperty("categories", out var ca) && ca.ValueKind == JsonValueKind.Array) cats = ca.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
                    if (d.RootElement.TryGetProperty("palette", out var pal) && pal.ValueKind == JsonValueKind.String) palette = pal.GetString() ?? palette;
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                if (!AllTools.TryParseDate(startDateStr, out var startDate)) return $"Invalid startDate '{startDateStr}'.";
                interval = Polyfills.Clamp(interval, 1, 365);

                var preview = await context.FullAccess!.RunRevitAsync(session =>
                {
                    var uiApp = session.As<UIApplication>();
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return (err: "Please open a model first.", n: 0);
                    try
                    {
                        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
                        return (err: "", n: levels.Count);
                    }
                    catch (Exception ex) { return (err: ex.Message, n: 0); }
                });
                if (!string.IsNullOrEmpty(preview.err)) return preview.err;

                var result = await context.FullAccess!.RunRevitAsync(session =>
                {
                    var uiApp = session.As<UIApplication>();
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Please open a model first.";
                    var view = doc.ActiveView;
                    if (view == null) return "No active view.";
                    if (view.IsTemplate) return "Cannot color in view template.";
                    try
                    {
                        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
                        var levelById = levels.ToDictionary(l => l.Id, l => l.Name);
                        // date map not needed for color, just index
                        var colors = GeneratePalette(levels.Count, palette);
                        var dateByLevel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        for (int i = 0; i < levels.Count; i++) dateByLevel[levels[i].Name] = startDate.AddDays(i * interval).ToString("yyyy-MM-dd");

                        // categories
                        List<BuiltInCategory> bics = new();
                        if (cats != null && cats.Count > 0)
                        {
                            // handle "all" keyword
                            if (cats.Count == 1 && cats[0].Equals("all", StringComparison.OrdinalIgnoreCase))
                            {
                                string[] defaults = { "Walls", "Doors", "Windows", "Floors", "Ceilings", "Roofs", "Structural Framing", "Structural Columns", "Columns", "Generic Models" };
                                foreach (var c in defaults) { var bic = AllTools.ParseCategory(c); if (bic != null) bics.Add(bic.Value); }
                            }
                            else foreach (var c in cats) { var bic = AllTools.ParseCategory(c); if (bic != null) bics.Add(bic.Value); }
                        }
                        else
                        {
                            bics.Add(BuiltInCategory.OST_Walls); // default walls if not specified, to keep it light
                        }

                        // Build level -> ids map
                        var levelToIds = new Dictionary<string, List<ElementId>>(StringComparer.OrdinalIgnoreCase);
                        foreach (var lvl in levels) levelToIds[lvl.Name] = new List<ElementId>();

                        foreach (var bic in bics)
                        {
                            var elems = new FilteredElementCollector(doc, view.Id).OfCategory(bic).WhereElementIsNotElementType().ToElements();
                            // also include not visible? Use view collector to only color visible elements to avoid off-view clutter
                            // For each element, resolve level name
                            foreach (var el in elems)
                            {
                                string? lvlName = null;
                                if (el.LevelId != ElementId.InvalidElementId && levelById.TryGetValue(el.LevelId, out var ln)) lvlName = ln;
                                else
                                {
                                    var p = el.LookupParameter("Base Constraint") ?? el.LookupParameter("Level") ?? el.LookupParameter("Reference Level") ?? el.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM) ?? el.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT);
                                    if (p != null)
                                    {
                                        if (p.StorageType == StorageType.ElementId) { var id = p.AsElementId(); if (levelById.TryGetValue(id, out var n2)) lvlName = n2; }
                                        else lvlName = p.AsString() ?? p.AsValueString();
                                    }
                                }
                                if (lvlName != null && levelToIds.ContainsKey(lvlName)) levelToIds[lvlName].Add(el.Id);
                            }
                        }

                        // Single transaction for all colors
                        using var tx = new Transaction(doc, $"LoadingAI for Revit: color by schedule {startDate:yyyy-MM-dd}");
                        tx.Start();
                        try
                        {
                            int total = 0;
                            for (int i = 0; i < levels.Count; i++)
                            {
                                var lvl = levels[i];
                                var ids = levelToIds[lvl.Name];
                                if (ids.Count == 0) continue;
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
                            }
                            tx.Commit();
                            var msg = $"Colored {total} element(s) in '{view.Name}' by construction schedule bottom-up {startDate:yyyy-MM-dd} (+{interval}d) across {levels.Count} levels, {bics.Count} categories. Palette {palette}.";
                            msg += "\nLegend:\n" + string.Join("\n", levels.Select((l, i) => $"{l.Name} ({l.Elevation:F1}ft) = {dateByLevel[l.Name]} -> #{colors[i].Red:X2}{colors[i].Green:X2}{colors[i].Blue:X2}"));
                            msg += "\nTo reset: say 'reset view' (reset_temporary_view).";
                            return msg;
                        }
                        catch
                        {
                            try { tx.RollBack(); } catch { }
                            throw;
                        }
                    }
                    catch (Exception ex) { return $"Failed: {ex.Message}"; }
                });
                return result;
            }
        };
    }

    internal static ToolEntry CreateColorByParameter(IPluginContext context)
    {
        return new ToolEntry
        {
            Tier = ToolTier.L2,
            Definition = new ToolDefinition
            {
                Function = new ToolFunction
                {
                    Name = "color_by_parameter",
                    Description = "Color elements in the active view by any instance parameter value (Mark, Comments, Type, Level, …). One color per distinct value. Optional category; if omitted uses current selection, else Walls.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            paramName = new { type = "string", description = "Parameter name, e.g. Mark, Comments, Level" },
                            category = new { type = "string", description = "Optional category. If omitted: selection, else Walls." },
                            palette = new { type = "string", description = "Palette: rainbow, blue_red, heat. Default rainbow." }
                        },
                        required = new[] { "paramName" }
                    }
                }
            },
            Handler = async json =>
            {
                string paramName = "";
                string? cat = null;
                string palette = "rainbow";
                try
                {
                    using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                    if (d.RootElement.TryGetProperty("paramName", out var pn) && pn.ValueKind == JsonValueKind.String) paramName = pn.GetString() ?? "";
                    if (d.RootElement.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String) cat = c.GetString();
                    if (d.RootElement.TryGetProperty("palette", out var pal) && pal.ValueKind == JsonValueKind.String) palette = pal.GetString() ?? palette;
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                if (string.IsNullOrWhiteSpace(paramName)) return "Provide paramName.";
                List<string>? selIds = null;
                if (string.IsNullOrWhiteSpace(cat))
                {
                    var sel = await context.Bridge.GetSelectionSummaryAsync();
                    if (sel.ElementCount > 0) selIds = sel.ElementIds.ToList();
                }
                return await context.FullAccess!.RunRevitAsync(session =>
                {
                    var uiApp = session.As<UIApplication>();
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Please open a model first.";
                    var view = doc.ActiveView;
                    if (view == null) return "No active view.";
                    if (view.IsTemplate) return "Cannot color in view template.";
                    try
                    {
                        List<Element> elems;
                        if (selIds != null)
                        {
                            elems = selIds.Select(id => doc.GetElement(AllTools.ParseEid(id))).Where(e => e != null).Cast<Element>().ToList();
                        }
                        else
                        {
                            var bic = AllTools.ParseCategory(string.IsNullOrWhiteSpace(cat) ? "Walls" : cat);
                            if (bic == null) return $"Unknown category '{cat}'.";
                            elems = new FilteredElementCollector(doc, view.Id).OfCategory(bic.Value).WhereElementIsNotElementType().ToElements().ToList();
                        }
                        var groups = new SortedDictionary<string, List<ElementId>>(StringComparer.OrdinalIgnoreCase);
                        foreach (var el in elems)
                        {
                            var key = ReadParam(el, paramName);
                            if (!groups.TryGetValue(key, out var ids)) { ids = new List<ElementId>(); groups[key] = ids; }
                            ids.Add(el.Id);
                        }
                        if (groups.Count == 0) return "No elements to color.";
                        var colors = GeneratePalette(groups.Count, palette);
                        using var tx = new Transaction(doc, $"LoadingAI for Revit: color by {paramName}");
                        tx.Start();
                        try
                        {
                            int i = 0, total = 0;
                            var legend = new List<string>();
                            foreach (var kv in groups)
                            {
                                var clr = colors[i];
                                var ogs = new OverrideGraphicSettings();
                                ogs.SetProjectionLineColor(clr);
                                ogs.SetSurfaceForegroundPatternColor(clr);
                                ogs.SetCutForegroundPatternColor(clr);
                                foreach (var id in kv.Value) { view.SetElementOverrides(id, ogs); total++; }
                                legend.Add($"{kv.Key} ({kv.Value.Count}) -> #{clr.Red:X2}{clr.Green:X2}{clr.Blue:X2}");
                                i++;
                            }
                            tx.Commit();
                            return $"Colored {total} element(s) in '{view.Name}' by '{paramName}' ({groups.Count} values). Palette {palette}.\nLegend:\n" + string.Join("\n", legend);
                        }
                        catch
                        {
                            try { tx.RollBack(); } catch { }
                            throw;
                        }
                    }
                    catch (Exception ex) { return $"Failed: {ex.Message}"; }
                });
            }
        };
    }

    static string ReadParam(Element el, string paramName)
    {
        var p = el.LookupParameter(paramName);
        if (p == null)
        {
            foreach (Parameter q in el.Parameters)
            {
                if (q?.Definition?.Name != null && q.Definition.Name.Equals(paramName, StringComparison.OrdinalIgnoreCase))
                { p = q; break; }
            }
        }
        if (p == null) return "(none)";
        var vs = p.AsValueString();
        if (!string.IsNullOrWhiteSpace(vs)) return vs.Trim();
        if (p.StorageType == StorageType.String) return string.IsNullOrWhiteSpace(p.AsString()) ? "(empty)" : p.AsString()!.Trim();
        if (p.StorageType == StorageType.Integer) return p.AsInteger().ToString();
        if (p.StorageType == StorageType.Double) return p.AsDouble().ToString("G4");
        if (p.StorageType == StorageType.ElementId)
        {
            var id = p.AsElementId();
            if (id == ElementId.InvalidElementId) return "(none)";
            return el.Document.GetElement(id)?.Name ?? id.ToString();
        }
        return "(none)";
    }

    private static List<Color> GeneratePalette(int n, string palette)
    {
        palette = palette?.ToLowerInvariant() ?? "rainbow";
        // Predefined gradient blue->red
        string[] hexRainbow = { "#1565C0", "#1E88E5", "#039BE5", "#00ACC1", "#00897B", "#43A047", "#7CB342", "#FBC02D", "#FFA000", "#FB8C00", "#F4511E", "#E53935", "#D32F2F", "#C2185B", "#7B1FA2", "#512DA8", "#303F9F", "#455A64", "#FF6F00", "#FF4081" };
        var list = new List<Color>();
        for (int i = 0; i < n; i++)
        {
            string hex;
            if (palette == "heat") hex = InterpolateHeat((double)i / Math.Max(1, n - 1));
            else hex = hexRainbow[i % hexRainbow.Length];
            list.Add(HexToColor(hex));
        }
        return list;
    }

    private static string InterpolateHeat(double t)
    {
        // blue -> yellow -> red
        if (t < 0.5) { int r = (int)(255 * t * 2); int g = (int)(255 * t * 2); int b = 255; return $"#{r:X2}{g:X2}{b:X2}"; }
        else { int r = 255; int g = (int)(255 * (1 - t) * 2); int b = (int)(255 * (1 - t) * 2); return $"#{r:X2}{g:X2}{b:X2}"; }
    }

    private static Color HexToColor(string hex)
    {
        hex = hex.Trim().TrimStart('#');
        if (hex.Length == 6 && int.TryParse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r) && int.TryParse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g) && int.TryParse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b)) return new Color((byte)r, (byte)g, (byte)b);
        return new Color(255, 0, 0);
    }
}
