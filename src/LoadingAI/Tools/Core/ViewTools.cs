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
                    Description = "Temporarily isolate elements or a whole category in the active view. Use for 'isolate the walls'. Requires confirm if many.",
                    Parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            elementIds = new { type = "array", items = new { type = "string" }, description = "ElementIds to isolate. If omitted and category given, isolates category." },
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
                    using var d = JsonDocument.Parse(json);
                    if (d.RootElement.TryGetProperty("elementIds", out var arr) && arr.ValueKind == JsonValueKind.Array) eids = arr.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
                    if (d.RootElement.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String) cat = c.GetString();
                    if (d.RootElement.TryGetProperty("isolate", out var iso) && iso.ValueKind is JsonValueKind.True or JsonValueKind.False) isolate = iso.GetBoolean();
                }
                catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                if ((eids == null || eids.Count == 0) && string.IsNullOrWhiteSpace(cat)) return "Provide elementIds or category.";
                var result = await context.FullAccess!.RunRevitAsync(session =>
                {
                    var uiApp = session.As<UIApplication>();
                    var uidoc = uiApp.ActiveUIDocument;
                    if (uidoc == null) return "Please open a model first.";
                    var doc = uidoc.Document;
                    var view = doc.ActiveView;
                    if (view == null) return "No active view.";
                    if (view.IsTemplate) return "Cannot isolate in a view template.";
                    try
                    {
                        // Confirm
                        int count = eids?.Count ?? 0;
                        string preview = !string.IsNullOrWhiteSpace(cat) ? $"Category: {cat}\nView: {view.Name}\nIsolate: {isolate}" : $"Ids: {string.Join(", ", eids!.Take(5))}{(eids.Count > 5 ? "..." : "")}\nView: {view.Name}";
                        // Skip confirm for L2? But we use L2 tier => bridge will confirm via HostPage's audit? Actually we need explicit dialog here for view ops as well.
                        // Use same Confirm helper
                        if (count > 10 || !string.IsNullOrWhiteSpace(cat))
                        {
                            var td = new TaskDialog("LoadingAI for Revit") { MainInstruction = isolate ? $"Isolate {(cat ?? count + " elements")} in '{view.Name}'?" : $"Reset isolate in '{view.Name}'?", MainContent = preview, CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No, DefaultButton = TaskDialogResult.No };
                            if (td.Show() != TaskDialogResult.Yes) return "Cancelled by user.";
                        }
                        // All temporary view modifications require a Transaction in Revit 2027
                        using (var tx = new Transaction(doc, isolate ? $"LoadingAI for Revit: isolate {cat ?? eids!.Count.ToString()} in {view.Name}" : $"LoadingAI for Revit: reset isolate in {view.Name}"))
                        {
                            tx.Start();
                            try
                            {
                                if (!view.IsTemporaryHideIsolateActive() || isolate)
                                {
                                    if (!string.IsNullOrWhiteSpace(cat) && (eids == null || eids.Count == 0))
                                    {
                                        var bic = AllTools.ParseCategory(cat);
                                        if (bic == null) { tx.RollBack(); return $"Unknown category '{cat}'."; }
                                        var catObj = doc.Settings.Categories.get_Item(bic.Value);
                                        if (catObj == null) { tx.RollBack(); return $"Category '{cat}' not found."; }
                                        view.IsolateCategoryTemporary(catObj.Id);
                                    }
                                    else
                                    {
                                        var ids = eids!.Select(AllTools.ParseEid).ToList();
                                        view.IsolateElementsTemporary(ids);
                                    }
                                }
                                if (!isolate)
                                {
                                    view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                                }
                                tx.Commit();
                            }
                            catch
                            {
                                try { tx.RollBack(); } catch { }
                                throw;
                            }
                        }
                        return isolate ? $"Isolated {(cat ?? eids!.Count + " elements")} in view '{view.Name}' (temporary). Use reset_temporary_view to restore." : $"Reset isolate in '{view.Name}'.";
                    }
                    catch (Exception ex) { return $"Failed: {ex.Message}"; }
                });
                return result;
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
                            elementIds = new { type = "array", items = new { type = "string" }, description = "Ids to hide" },
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
                try { using var d = JsonDocument.Parse(json); if (d.RootElement.TryGetProperty("elementIds", out var a) && a.ValueKind == JsonValueKind.Array) eids = a.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList(); if (d.RootElement.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String) cat = c.GetString(); } catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
                if ((eids == null || eids.Count == 0) && string.IsNullOrWhiteSpace(cat)) return "Provide elementIds or category.";
                var result = await context.FullAccess!.RunRevitAsync(session =>
                {
                    var uiApp = session.As<UIApplication>();
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Please open a model first.";
                    var view = doc.ActiveView;
                    if (view == null) return "No active view.";
                    try
                    {
                        using var tx = new Transaction(doc, $"LoadingAI for Revit: hide {cat ?? eids!.Count.ToString()} in {view.Name}");
                        tx.Start();
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(cat) && (eids == null || eids.Count == 0))
                            {
                                var lowerCat = cat.Trim().ToLowerInvariant();
                                ICollection<ElementId> ids;
                                if (lowerCat is "link" or "links" or "rvt links" or "revit links" or "rvtlinks")
                                {
                                    ids = new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_RvtLinks).WhereElementIsNotElementType().ToElementIds();
                                }
                                else
                                {
                                    var bic = AllTools.ParseCategory(cat);
                                    if (bic == null) { tx.RollBack(); return $"Unknown category '{cat}'. Try RVT Links, Walls, Doors, etc."; }
                                    ids = new FilteredElementCollector(doc, view.Id).OfCategory(bic.Value).WhereElementIsNotElementType().ToElementIds();
                                }
                                if (ids.Count == 0) { tx.RollBack(); return $"No visible {cat} in view '{view.Name}'."; }
                                view.HideElementsTemporary(ids);
                                tx.Commit();
                                return $"Hid {ids.Count} {cat} in '{view.Name}' (temporary). Use reset_temporary_view to restore.";
                            }
                            else
                            {
                                var ids = eids!.Select(AllTools.ParseEid).ToList();
                                view.HideElementsTemporary(ids);
                                tx.Commit();
                                return $"Hid {ids.Count} element(s) in '{view.Name}' (temporary).";
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
                var result = await context.FullAccess!.RunRevitAsync(session =>
                {
                    var uiApp = session.As<UIApplication>();
                    var doc = uiApp.ActiveUIDocument?.Document;
                    if (doc == null) return "Please open a model first.";
                    var view = doc.ActiveView;
                    if (view == null) return "No active view.";
                    try
                    {
                        if (view.IsTemporaryHideIsolateActive())
                        {
                            using var tx = new Transaction(doc, $"LoadingAI for Revit: reset view {view.Name}");
                            tx.Start();
                            try
                            {
                                view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                                tx.Commit();
                            }
                            catch
                            {
                                try { tx.RollBack(); } catch { }
                                throw;
                            }
                            return $"Reset temporary view in '{view.Name}'.";
                        }
                        return $"No temporary hide/isolate active in '{view.Name}'.";
                    }
                    catch (Exception ex) { return $"Failed: {ex.Message}"; }
                });
                return result;
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
                            elementIds = new { type = "array", items = new { type = "string" }, description = "Ids to color" },
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
                if ((eids == null || eids.Count == 0) && string.IsNullOrWhiteSpace(cat)) return "Provide elementIds or category.";
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
