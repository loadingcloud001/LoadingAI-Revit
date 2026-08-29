using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using LoadingAI.Llm;
using LoadingAI.Revit;
using LoadingAI.Plugins;

namespace LoadingAI.Tools.Compat;

/// <summary>
/// pyRevit OpeningTools: cut walls by Generic Model openings, and MagiCAD/FSE voids → opening family.
/// L3 FullAccess. No TaskDialog. Omit ids → current selection.
/// </summary>
public static class OpeningTools
{
    public static IEnumerable<ToolEntry> Create(IPluginContext context)
    {
        yield return CreateCutWallsByOpenings(context);
        yield return CreateFseVoidsToOpenings(context);
    }

    internal static ToolEntry CreateCutWallsByOpenings(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        RequiresWriteConfirm = true,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "cut_walls_by_openings",
                Description = "Cut intersecting walls with NewOpening from Generic Model opening instances (pyRevit: OpeningCutWall). Optional. If omitted, uses current selection.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        elementIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses current selection." }
                    },
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

            return await context.FullAccess!.RunRevitAsync(session =>
            {
                var uiApp = session.As<UIApplication>();
                var uidoc = uiApp.ActiveUIDocument;
                var doc = uidoc?.Document;
                if (doc == null) return "Please open a model first.";
                try
                {
                    var openings = ResolveHostGenericModels(doc, uidoc!, eids);
                    if (openings.Count == 0) return "Please select Generic Model openings first, or pass elementIds.";
                    var walls = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_Walls)
                        .WhereElementIsNotElementType()
                        .OfType<Wall>()
                        .ToList();
                    if (walls.Count == 0) return "No walls found.";

                    int created = 0, skipped = 0, errors = 0;
                    using var tx = new Transaction(doc, "LoadingAI for Revit: cut walls by openings");
                    tx.Start();
                    foreach (var opening in openings)
                    {
                        var openingSolid = GetElementSolid(opening);
                        if (openingSolid == null) { skipped++; continue; }
                        foreach (var wall in walls)
                        {
                            if (!BboxOverlap(opening, wall)) continue;
                            var wallSolid = GetElementSolid(wall);
                            if (wallSolid == null) continue;
                            if (!SolidsIntersect(openingSolid, wallSolid)) continue;
                            if (TryCreateWallOpening(doc, wall, opening)) created++;
                            else errors++;
                        }
                    }
                    tx.Commit();
                    return $"Processed {openings.Count} openings against {walls.Count} walls. Openings created: {created}. Skipped: {skipped}. Errors: {errors}.";
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            });
        }
    };

    internal static ToolEntry CreateFseVoidsToOpenings(IPluginContext context) => new()
    {
        Tier = ToolTier.L3,
        RequiresWriteConfirm = true,
        Definition = new ToolDefinition
        {
            Function = new ToolFunction
            {
                Name = "fse_voids_to_openings",
                Description = "Place an opening family at MagiCAD/FSE void locations (host or linked). Params: familyName, levelName, optional symbolName and elementIds. If omitted, uses current selection.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        familyName = new { type = "string", description = "Opening family name (host)." },
                        symbolName = new { type = "string", description = "Optional type name." },
                        levelName = new { type = "string", description = "Level name (exact). Snowdon: L1 - Block 35/37/43." },
                        elementIds = new { type = "array", items = new { type = "string" }, description = "Optional. If omitted, uses current selection." }
                    },
                    required = new[] { "familyName", "levelName" }
                }
            }
        },
        Handler = async json =>
        {
            string familyName = "", symbolName = "", levelName = "";
            List<string> eids = new();
            try
            {
                using var d = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                familyName = d.RootElement.GetProperty("familyName").GetString() ?? "";
                levelName = d.RootElement.GetProperty("levelName").GetString() ?? "";
                if (d.RootElement.TryGetProperty("symbolName", out var sn) && sn.ValueKind == JsonValueKind.String)
                    symbolName = sn.GetString() ?? "";
                if (d.RootElement.TryGetProperty("elementIds", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    eids = arr.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList();
            }
            catch (Exception ex) { return $"Invalid args: {ex.Message}"; }
            if (string.IsNullOrWhiteSpace(familyName) || string.IsNullOrWhiteSpace(levelName))
                return "familyName and levelName are required.";

            return await context.FullAccess!.RunRevitAsync(session =>
            {
                var uiApp = session.As<UIApplication>();
                var uidoc = uiApp.ActiveUIDocument;
                var doc = uidoc?.Document;
                if (doc == null) return "Please open a model first.";
                try
                {
                    var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
                    var names = levels.Select(l => l.Name).ToList();
                    var resolved = LevelNames.Resolve(levelName, names);
                    if (resolved == null) return LevelNames.NotFound(levelName, names);
                    var level = levels.First(l => l.Name == resolved);

                    var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToList();
                    var sym = ResolveSymbol(symbols, familyName, symbolName);
                    if (sym == null)
                    {
                        var sample = string.Join(", ", symbols.Take(8).Select(s => $"{s.FamilyName} : {s.Name}"));
                        return $"Family '{familyName}' not found. Did you mean: {sample}?";
                    }

                    var voids = CollectMagiCadVoids(doc, uidoc!, eids);
                    if (voids.Count == 0)
                        return "No MagiCAD/MC Void family instances in selection or elementIds. Select FSE voids (host or pass linked ids).";

                    if (!sym.IsActive) { using var act = new Transaction(doc, "LoadingAI for Revit: activate opening family"); act.Start(); sym.Activate(); doc.Regenerate(); act.Commit(); }

                    int created = 0, skipped = 0;
                    using var tx = new Transaction(doc, "LoadingAI for Revit: FSE voids to openings");
                    tx.Start();
                    foreach (var (src, xf) in voids)
                    {
                        if (src.Location is not LocationPoint lp) { skipped++; continue; }
                        var pt = xf.OfPoint(lp.Point);
                        try
                        {
                            var inst = doc.Create.NewFamilyInstance(pt, sym, level, StructuralType.NonStructural);
                            CopyVoidDimensions(src, inst);
                            TrySetString(inst, "Comments", src.Name);
                            TrySetString(inst, "Modified by", "LoadingAI");
                            var depth = inst.LookupParameter("Depth");
                            if (depth != null && !depth.IsReadOnly && depth.StorageType == StorageType.Double)
                                depth.Set(Metric.MmToFt(500));
                            RotateToFacing(doc, inst, src, xf);
                            created++;
                        }
                        catch { skipped++; }
                    }
                    tx.Commit();
                    return $"Created {created} openings on {level.Name} from {voids.Count} MagiCAD/FSE voids. Skipped: {skipped}.";
                }
                catch (Exception ex) { return $"Failed: {ex.Message}"; }
            });
        }
    };

    static List<FamilyInstance> ResolveHostGenericModels(Document doc, UIDocument uidoc, List<string> eids)
    {
        IEnumerable<ElementId> ids;
        if (eids.Count > 0)
            ids = eids.Select(AllTools.ParseEid);
        else
            ids = uidoc.Selection.GetElementIds();
        var gm = new ElementId(BuiltInCategory.OST_GenericModel);
        var list = new List<FamilyInstance>();
        foreach (var id in ids)
        {
            if (doc.GetElement(id) is FamilyInstance fi && fi.Category != null && fi.Category.Id == gm)
                list.Add(fi);
        }
        return list;
    }

    static List<(FamilyInstance Src, Transform Xf)> CollectMagiCadVoids(Document doc, UIDocument uidoc, List<string> eids)
    {
        List<ElementId> ids;
        if (eids.Count > 0)
            ids = eids.Select(AllTools.ParseEid).ToList();
        else
            ids = uidoc.Selection.GetElementIds().ToList();

        var found = new List<(FamilyInstance, Transform)>();
        foreach (var id in ids)
        {
            if (doc.GetElement(id) is FamilyInstance host && IsMagiCadVoid(host))
                found.Add((host, Transform.Identity));
        }
        foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
        {
            var ld = link.GetLinkDocument();
            if (ld == null) continue;
            var xf = link.GetTotalTransform();
            foreach (var id in ids)
            {
                if (ld.GetElement(id) is FamilyInstance fi && IsMagiCadVoid(fi))
                    found.Add((fi, xf));
            }
        }
        return found;
    }

    static bool IsMagiCadVoid(FamilyInstance fi)
    {
        var n = fi.Symbol?.FamilyName ?? "";
        return n.IndexOf("MagiCAD", StringComparison.OrdinalIgnoreCase) >= 0
            || n.IndexOf("MC Void", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static FamilySymbol? ResolveSymbol(List<FamilySymbol> symbols, string familyName, string symbolName)
    {
        var fam = familyName.Trim();
        var typ = (symbolName ?? "").Trim();
        if (fam.Contains(":") && string.IsNullOrEmpty(typ))
        {
            var parts = fam.Split(new[] { ':' }, 2);
            fam = parts[0].Trim();
            typ = parts[1].Trim();
        }
        FamilySymbol? sym = null;
        if (!string.IsNullOrEmpty(typ))
            sym = symbols.FirstOrDefault(s => s.FamilyName.Equals(fam, StringComparison.OrdinalIgnoreCase) && s.Name.Equals(typ, StringComparison.OrdinalIgnoreCase));
        sym ??= symbols.FirstOrDefault(s => s.FamilyName.Equals(fam, StringComparison.OrdinalIgnoreCase));
        sym ??= symbols.FirstOrDefault(s => s.Name.Equals(fam, StringComparison.OrdinalIgnoreCase));
        return sym;
    }

    static void CopyVoidDimensions(FamilyInstance src, FamilyInstance dest)
    {
        var width = GetDouble(src, "MC Width Instance", "Width_", "magi_width_instance", "Width");
        var height = GetDouble(src, "MC Height Instance", "Height_", "magi_height_instance", "Height");
        var radius = GetDouble(src, "Radius_", "magi_radius_instance", "Radius");
        if (width.HasValue) TrySetDouble(dest, "Width", width.Value);
        if (height.HasValue) TrySetDouble(dest, "Height", height.Value);
        if (radius.HasValue) TrySetDouble(dest, "Radius", radius.Value);
    }

    static double? GetDouble(Element el, params string[] names)
    {
        foreach (var name in names)
        {
            var p = el.LookupParameter(name);
            if (p != null && p.HasValue && p.StorageType == StorageType.Double) return p.AsDouble();
            if (el is FamilyInstance fi)
            {
                p = fi.Symbol?.LookupParameter(name);
                if (p != null && p.HasValue && p.StorageType == StorageType.Double) return p.AsDouble();
            }
        }
        return null;
    }

    static void TrySetDouble(Element el, string name, double value)
    {
        var p = el.LookupParameter(name);
        if (p != null && !p.IsReadOnly && p.StorageType == StorageType.Double) p.Set(value);
    }

    static void TrySetString(Element el, string name, string value)
    {
        var p = el.LookupParameter(name);
        if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String) p.Set(value);
    }

    static void RotateToFacing(Document doc, FamilyInstance inst, FamilyInstance src, Transform xf)
    {
        XYZ? facing = null;
        try { facing = src.FacingOrientation; } catch { /* some families have none */ }
        if (facing == null || facing.GetLength() < 1e-9) return;
        var dir = xf.OfVector(new XYZ(facing.X, facing.Y, 0));
        var xy = new XYZ(dir.X, dir.Y, 0);
        if (xy.GetLength() < 1e-9) return;
        xy = xy.Normalize();
        var angle = Math.Atan2(xy.Y, xy.X);
        if (Math.Abs(angle) < 1e-6) return;
        var loc = (inst.Location as LocationPoint)?.Point;
        if (loc == null) return;
        var axis = Line.CreateBound(loc, loc + XYZ.BasisZ);
        ElementTransformUtils.RotateElement(doc, inst.Id, axis, -angle);
    }

    static Solid? GetElementSolid(Element element)
    {
        var options = new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = true };
        var geo = element.get_Geometry(options);
        if (geo == null) return null;
        foreach (var obj in geo)
        {
            if (obj is Solid s && s.Volume > 0) return s;
            if (obj is GeometryInstance gi)
            {
                foreach (var inner in gi.GetInstanceGeometry())
                    if (inner is Solid inst && inst.Volume > 0) return inst;
            }
        }
        return null;
    }

    static bool BboxOverlap(Element a, Element b, double tol = 0.05)
    {
        var bb1 = a.get_BoundingBox(null);
        var bb2 = b.get_BoundingBox(null);
        if (bb1 == null || bb2 == null) return true;
        return !(bb1.Max.X < bb2.Min.X - tol || bb2.Max.X < bb1.Min.X - tol
              || bb1.Max.Y < bb2.Min.Y - tol || bb2.Max.Y < bb1.Min.Y - tol
              || bb1.Max.Z < bb2.Min.Z - tol || bb2.Max.Z < bb1.Min.Z - tol);
    }

    static bool SolidsIntersect(Solid a, Solid b)
    {
        try
        {
            var hit = BooleanOperationsUtils.ExecuteBooleanOperation(a, b, BooleanOperationsType.Intersect);
            return hit != null && hit.Volume > 0.0001;
        }
        catch { return false; }
    }

    static bool TryCreateWallOpening(Document doc, Wall wall, FamilyInstance opening)
    {
        var bbox = opening.get_BoundingBox(null);
        if (bbox == null) return false;
        var center = (bbox.Min + bbox.Max) / 2.0;
        if (wall.Location is not LocationCurve loc) return false;
        var wallCurve = loc.Curve;
        var wallDir = wallCurve.GetEndPoint(1) - wallCurve.GetEndPoint(0);
        wallDir = new XYZ(wallDir.X, wallDir.Y, 0);
        if (wallDir.GetLength() < 1e-9) return false;
        wallDir = wallDir.Normalize();

        var openingAxis = wallDir;
        try
        {
            var hand = opening.HandOrientation;
            var instDir = new XYZ(hand.X, hand.Y, 0);
            if (instDir.GetLength() > 1e-9)
            {
                instDir = instDir.Normalize();
                var proj = instDir.X * wallDir.X + instDir.Y * wallDir.Y;
                openingAxis = proj >= 0 ? wallDir : new XYZ(-wallDir.X, -wallDir.Y, 0);
            }
        }
        catch { /* keep wallDir */ }

        double minProj = double.PositiveInfinity, maxProj = double.NegativeInfinity;
        foreach (var c in new[]
        {
            new XYZ(bbox.Min.X, bbox.Min.Y, bbox.Min.Z), new XYZ(bbox.Max.X, bbox.Min.Y, bbox.Min.Z),
            new XYZ(bbox.Min.X, bbox.Max.Y, bbox.Min.Z), new XYZ(bbox.Max.X, bbox.Max.Y, bbox.Min.Z),
            new XYZ(bbox.Min.X, bbox.Min.Y, bbox.Max.Z), new XYZ(bbox.Max.X, bbox.Min.Y, bbox.Max.Z),
            new XYZ(bbox.Min.X, bbox.Max.Y, bbox.Max.Z), new XYZ(bbox.Max.X, bbox.Max.Y, bbox.Max.Z)
        })
        {
            var v = new XYZ(c.X - center.X, c.Y - center.Y, 0);
            var d = v.X * openingAxis.X + v.Y * openingAxis.Y;
            if (d < minProj) minProj = d;
            if (d > maxProj) maxProj = d;
        }
        var result = wallCurve.Project(center);
        if (result == null) return false;
        var projected = result.XYZPoint;
        var half = (maxProj - minProj) / 2.0;
        var pt1 = new XYZ(projected.X + half * openingAxis.X, projected.Y + half * openingAxis.Y, bbox.Min.Z);
        var pt2 = new XYZ(projected.X - half * openingAxis.X, projected.Y - half * openingAxis.Y, bbox.Max.Z);
        var created = doc.Create.NewOpening(wall, pt2, pt1);
        return created != null;
    }
}
