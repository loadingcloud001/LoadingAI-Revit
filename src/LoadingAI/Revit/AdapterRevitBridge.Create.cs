using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Analysis;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using LoadingAI.Core.Openings;
using LoadingAI.Llm;
using LoadingAI.Revit.Formwork;
using LoadingAI.Revit.Openings;
using LoadingAI.Revit.StructureCuts;
using LoadingAI.Tools;

namespace LoadingAI.Revit;

/// <summary>
/// Creation slice: walls, family instances, floors, roofs, grids, arrays, rotate, mirror, join geometry, topography.
/// Implements <c>IRevitCreateCommand</c>. Part of <see cref="AdapterRevitBridge"/>; see that type for the contract.
/// </summary>
public sealed partial class AdapterRevitBridge
{
    public Task<CommandResultDto> CreateWallAsync(
        string levelName,
        double x1, double y1, double x2, double y2,
        string? wallTypeName = null,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");

            try
            {
                var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
                var names = levels.Select(l => l.Name).ToList();
                var resolved = LevelNames.Resolve(levelName, names);
                if (resolved == null) return new CommandResultDto(false, LevelNames.NotFound(levelName, names));
                var level = levels.First(l => l.Name == resolved);

                ElementId wallTypeId;
                if (!string.IsNullOrWhiteSpace(wallTypeName))
                {
                    var wt = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
                        .FirstOrDefault(t => t.Name.Equals(wallTypeName, StringComparison.OrdinalIgnoreCase));
                    if (wt == null) return new CommandResultDto(false, $"Wall type '{wallTypeName}' not found.");
                    wallTypeId = wt.Id;
                }
                else
                {
                    var wt = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().FirstOrDefault();
                    if (wt == null) return new CommandResultDto(false, "No wall types found.");
                    wallTypeId = wt.Id;
                }

                var p1 = new XYZ(Metric.MmToFt(x1), Metric.MmToFt(y1), 0);
                var p2 = new XYZ(Metric.MmToFt(x2), Metric.MmToFt(y2), 0);
                var line = Line.CreateBound(p1, p2);
                using var tx = new Transaction(doc, "LoadingAI for Revit: create wall");
                tx.Start();
                try
                {
                    var wall = Wall.Create(doc, line, wallTypeId, level.Id, 10, 0, false, false);
                    tx.Commit();
                    var id = wall.Id.ToString();
                    return new CommandResultDto(
                        true,
                        $"Created wall {id} on {level.Name} from ({x1:F0},{y1:F0}) to ({x2:F0},{y2:F0}) mm type {wallTypeId}.",
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
                var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToList();
                FamilySymbol? sym = null;
                if (!string.IsNullOrWhiteSpace(familyName))
                    sym = symbols.FirstOrDefault(s =>
                        s.FamilyName.Equals(familyName, StringComparison.OrdinalIgnoreCase)
                        && s.Name.Equals(symbolName, StringComparison.OrdinalIgnoreCase));
                sym ??= symbols.FirstOrDefault(s => s.Name.Equals(symbolName, StringComparison.OrdinalIgnoreCase));
                if (sym == null)
                    return new CommandResultDto(false, $"Symbol '{symbolName}' not found. Available: {string.Join(", ", symbols.Take(10).Select(s => s.Name))}");

                Level? lvl = null;
                if (!string.IsNullOrWhiteSpace(levelName))
                    lvl = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                        .FirstOrDefault(l => l.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase));
                lvl ??= new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault();
                if (lvl == null) return new CommandResultDto(false, "No levels found.");

                if (!sym.IsActive) sym.Activate();
                var pt = new XYZ(Metric.MmToFt(x), Metric.MmToFt(y), Metric.MmToFt(z));
                using var tx = new Transaction(doc, "LoadingAI for Revit: create instance");
                tx.Start();
                try
                {
                    var inst = doc.Create.NewFamilyInstance(pt, sym, lvl, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                    tx.Commit();
                    var id = inst.Id.ToString();
                    return new CommandResultDto(
                        true,
                        $"Created instance {id} '{symbolName}' at ({x:F0},{y:F0},{z:F0}) mm on {lvl.Name}.",
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

    public Task<CommandResultDto> DuplicateElementAsync(
        string elementId,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            if (string.IsNullOrWhiteSpace(elementId))
                return new CommandResultDto(false, "Provide elementId.");

            try
            {
                var eid = AllTools.ParseEid(elementId);
                var el = doc.GetElement(eid);
                if (el == null) return new CommandResultDto(false, $"Element {elementId} not found.");

                using var tx = new Transaction(doc, "LoadingAI for Revit: duplicate");
                tx.Start();
                try
                {
                    ICollection<ElementId> newIds;
                    if (el is View v)
                        newIds = new[] { v.Duplicate(ViewDuplicateOption.Duplicate) };
                    else
                        newIds = ElementTransformUtils.CopyElement(doc, eid, XYZ.Zero);
                    tx.Commit();
                    var idStrs = newIds.Select(id => id.ToString()).ToList();
                    return new CommandResultDto(
                        true,
                        $"Duplicated {elementId} -> {string.Join(", ", idStrs)}",
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

    public Task<CommandResultDto> CreateFloorByOutlineAsync(
        string levelName,
        double x1, double y1, double x2, double y2,
        string? floorTypeName = null,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");

            try
            {
                var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
                var names = levels.Select(l => l.Name).ToList();
                var resolved = LevelNames.Resolve(levelName, names);
                if (resolved == null) return new CommandResultDto(false, LevelNames.NotFound(levelName, names));
                var level = levels.First(l => l.Name == resolved);

                FloorType? ftype = null;
                if (!string.IsNullOrWhiteSpace(floorTypeName))
                    ftype = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>()
                        .FirstOrDefault(f => f.Name.Equals(floorTypeName, StringComparison.OrdinalIgnoreCase));
                ftype ??= new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().FirstOrDefault();
                if (ftype == null) return new CommandResultDto(false, "No floor types.");

                var fx1 = Metric.MmToFt(x1); var fy1 = Metric.MmToFt(y1);
                var fx2 = Metric.MmToFt(x2); var fy2 = Metric.MmToFt(y2);
                var z = level.Elevation;
                var pts = new[] { new XYZ(fx1, fy1, z), new XYZ(fx2, fy1, z), new XYZ(fx2, fy2, z), new XYZ(fx1, fy2, z) };
                var curveLoop = new CurveLoop();
                for (int i = 0; i < 4; i++) curveLoop.Append(Line.CreateBound(pts[i], pts[(i + 1) % 4]));

                using var tx = new Transaction(doc, $"LoadingAI for Revit: floor {resolved}");
                tx.Start();
                try
                {
                    var floor = Floor.Create(doc, new[] { curveLoop }, ftype.Id, level.Id);
                    tx.Commit();
                    var id = floor.Id.ToString();
                    return new CommandResultDto(
                        true,
                        $"Created floor {id} on {resolved} rect ({x1},{y1})-({x2},{y2})",
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

    public Task<CommandResultDto> CreateRoofAsync(
        string levelName,
        double x1, double y1, double x2, double y2,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");

            try
            {
                var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
                var names = levels.Select(l => l.Name).ToList();
                var resolved = LevelNames.Resolve(levelName, names);
                if (resolved == null) return new CommandResultDto(false, LevelNames.NotFound(levelName, names));
                var level = levels.First(l => l.Name == resolved);

                var rtype = new FilteredElementCollector(doc).OfClass(typeof(RoofType)).Cast<RoofType>().FirstOrDefault();
                if (rtype == null) return new CommandResultDto(false, "No roof type.");

                var fx1 = Metric.MmToFt(x1); var fy1 = Metric.MmToFt(y1);
                var fx2 = Metric.MmToFt(x2); var fy2 = Metric.MmToFt(y2);
                var pts = new[] { new XYZ(fx1, fy1, 0), new XYZ(fx2, fy1, 0), new XYZ(fx2, fy2, 0), new XYZ(fx1, fy2, 0) };
                var loop = new CurveArray();
                for (int i = 0; i < 4; i++) loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % 4]));

                using var tx = new Transaction(doc, "LoadingAI for Revit: roof");
                tx.Start();
                try
                {
                    ModelCurveArray mca;
                    var roof = doc.Create.NewFootPrintRoof(loop, level, rtype, out mca);
                    tx.Commit();
                    var id = roof.Id.ToString();
                    return new CommandResultDto(true, $"Created roof {id} on {resolved}", 1, ElementIds: new[] { id });
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

    public Task<CommandResultDto> CreateGridLevelAsync(
        string kind,
        string name,
        double x1, double y1, double x2, double y2,
        double elevation,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");

            try
            {
                if (kind.Equals("level", StringComparison.OrdinalIgnoreCase))
                {
                    var elevFt = Metric.MmToFt(elevation);
                    using var tx = new Transaction(doc, $"LoadingAI for Revit: level {name}");
                    tx.Start();
                    try
                    {
                        var lvl = Level.Create(doc, elevFt);
                        lvl.Name = name;
                        tx.Commit();
                        var id = lvl.Id.ToString();
                        return new CommandResultDto(true, $"Created level {id} \"{name}\" elev {elevation}", 1, ElementIds: new[] { id });
                    }
                    catch (Exception ex)
                    {
                        try { tx.RollBack(); } catch { }
                        return new CommandResultDto(false, $"Failed: {ex.Message}");
                    }
                }

                var fx1 = Metric.MmToFt(x1); var fy1 = Metric.MmToFt(y1);
                var fx2 = Metric.MmToFt(x2); var fy2 = Metric.MmToFt(y2);
                using var gtx = new Transaction(doc, $"LoadingAI for Revit: grid {name}");
                gtx.Start();
                try
                {
                    var g = Grid.Create(doc, Line.CreateBound(new XYZ(fx1, fy1, 0), new XYZ(fx2, fy2, 0)));
                    g.Name = name;
                    gtx.Commit();
                    var gid = g.Id.ToString();
                    return new CommandResultDto(true, $"Created grid {gid} \"{name}\"", 1, ElementIds: new[] { gid });
                }
                catch (Exception ex)
                {
                    try { gtx.RollBack(); } catch { }
                    return new CommandResultDto(false, $"Failed: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                return new CommandResultDto(false, $"Failed: {ex.Message}");
            }
        }, ct);
    }

    public Task<CommandResultDto> CreateFamilyInstanceArrayAsync(
        string symbolName,
        double startX, double startY, double endX, double endY,
        int count,
        string? levelName = null,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");

            try
            {
                var cnt = Polyfills.Clamp(count, 1, 50);
                var symbol = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                    .FirstOrDefault(s => s.Name.Equals(symbolName, StringComparison.OrdinalIgnoreCase));
                if (symbol == null) return new CommandResultDto(false, $"Symbol {symbolName} not found.");
                if (!symbol.IsActive) symbol.Activate();

                Level? level = null;
                if (!string.IsNullOrWhiteSpace(levelName))
                {
                    var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
                    var names = levels.Select(l => l.Name).ToList();
                    var resolved = LevelNames.Resolve(levelName, names);
                    if (resolved != null) level = levels.First(l => l.Name == resolved);
                }
                level ??= new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault();
                if (level == null) return new CommandResultDto(false, "No level.");

                var sx = Metric.MmToFt(startX); var sy = Metric.MmToFt(startY);
                var exFt = Metric.MmToFt(endX); var eyFt = Metric.MmToFt(endY);
                using var tx = new Transaction(doc, $"LoadingAI for Revit: array {symbolName} x{cnt}");
                tx.Start();
                try
                {
                    var ids = new List<ElementId>();
                    for (int i = 0; i < cnt; i++)
                    {
                        double t = (double)i / (cnt - 1 == 0 ? 1 : cnt - 1);
                        var pt = new XYZ(sx + (exFt - sx) * t, sy + (eyFt - sy) * t, 0);
                        var inst = doc.Create.NewFamilyInstance(pt, symbol, level, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                        ids.Add(inst.Id);
                    }
                    tx.Commit();
                    var idStrs = ids.Select(id => id.ToString()).ToList();
                    return new CommandResultDto(
                        true,
                        $"Created {idStrs.Count} {symbolName} array {string.Join(",", idStrs.Take(5))}",
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

    public Task<CommandResultDto> RotateElementsAsync(
        IReadOnlyList<string> elementIds,
        double angleDegrees,
        double? originX = null,
        double? originY = null,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");

            try
            {
                var eids = elementIds.Select(AllTools.ParseEid).ToList();
                XYZ origin;
                if (originX.HasValue && originY.HasValue)
                    origin = new XYZ(Metric.MmToFt(originX.Value), Metric.MmToFt(originY.Value), 0);
                else
                {
                    var bb = eids.Select(id => doc.GetElement(id)?.get_BoundingBox(null)).Where(b => b != null).ToList();
                    if (bb.Count == 0) return new CommandResultDto(false, "No bounding box.");
                    var minX = bb.Min(b => b!.Min.X); var minY = bb.Min(b => b!.Min.Y);
                    var maxX = bb.Max(b => b!.Max.X); var maxY = bb.Max(b => b!.Max.Y);
                    origin = new XYZ((minX + maxX) / 2, (minY + maxY) / 2, 0);
                }
                var axis = Line.CreateBound(origin, origin + XYZ.BasisZ);
                double rad = angleDegrees * Math.PI / 180;
                using var tx = new Transaction(doc, $"LoadingAI for Revit: rotate {angleDegrees}°");
                tx.Start();
                try
                {
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
            }
            catch (Exception ex)
            {
                return new CommandResultDto(false, $"Failed: {ex.Message}");
            }
        }, ct);
    }

    public Task<CommandResultDto> MirrorElementsAsync(
        IReadOnlyList<string> elementIds,
        double x1, double y1, double x2, double y2,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");

            try
            {
                var eids = elementIds.Select(AllTools.ParseEid).ToList();
                var fx1 = Metric.MmToFt(x1); var fy1 = Metric.MmToFt(y1);
                var fx2 = Metric.MmToFt(x2); var fy2 = Metric.MmToFt(y2);
                var plane = Plane.CreateByThreePoints(new XYZ(fx1, fy1, 0), new XYZ(fx2, fy2, 0), new XYZ(fx1, fy1, 1));
                using var tx = new Transaction(doc, "LoadingAI for Revit: mirror");
                tx.Start();
                try
                {
                    var newIds = ElementTransformUtils.MirrorElements(doc, eids, plane, false);
                    tx.Commit();
                    return new CommandResultDto(true, $"Mirrored {eids.Count} -> {newIds.Count} new elements", newIds.Count);
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

    public Task<CommandResultDto> SplitWallAsync(
        string wallId,
        double x, double y,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");

            try
            {
                var wall = doc.GetElement(AllTools.ParseEid(wallId)) as Wall;
                if (wall == null) return new CommandResultDto(false, $"Wall {wallId} not found.");
                var loc = wall.Location as LocationCurve;
                if (loc == null) return new CommandResultDto(false, "Wall has no curve.");
                return new CommandResultDto(false, $"Split not yet implemented for {wallId} at ({x},{y}) — use split via Dynamo or manual. Wall curve: {loc.Curve}");
            }
            catch (Exception ex)
            {
                return new CommandResultDto(false, $"Failed: {ex.Message}");
            }
        }, ct);
    }

    public Task<CommandResultDto> JoinGeometryAsync(
        string elementId1,
        string elementId2,
        bool join = true,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");

            try
            {
                var e1 = doc.GetElement(AllTools.ParseEid(elementId1));
                var e2 = doc.GetElement(AllTools.ParseEid(elementId2));
                if (e1 == null || e2 == null) return new CommandResultDto(false, "Elements not found.");
                using var tx = new Transaction(doc, "LoadingAI for Revit: join");
                tx.Start();
                try
                {
                    if (join) JoinGeometryUtils.JoinGeometry(doc, e1, e2);
                    else JoinGeometryUtils.UnjoinGeometry(doc, e1, e2);
                    tx.Commit();
                    return new CommandResultDto(true, join ? $"Joined {elementId1} and {elementId2}" : $"Unjoined {elementId1} and {elementId2}");
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

    public Task<CommandResultDto> CreateTopographyAsync(
        IReadOnlyList<PointMmDto> points,
        string? levelName = null, string? levelId = null,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            if (points == null || points.Count < 3)
                return new CommandResultDto(false, "Provide at least 3 points [x,y,z] in mm.");
            try
            {
                var level = ResolveLevel(doc, levelId, levelName);
                if (level == null) return new CommandResultDto(false, "No level found. Pass levelName or levelId.");
                var typeId = new FilteredElementCollector(doc).OfClass(typeof(ToposolidType)).FirstOrDefault()?.Id;
                if (typeId == null) return new CommandResultDto(false, "No ToposolidType found in the model.");
                var pts = points.Select(p => new XYZ(Metric.MmToFt(p.X), Metric.MmToFt(p.Y), Metric.MmToFt(p.Z))).ToList();
                using var tx = new Transaction(doc, "LoadingAI for Revit: create toposolid");
                tx.Start();
                try
                {
                    // Official order is (document, points, topoTypeId, levelId) — both trailing args are ElementId.
                    var surface = Toposolid.Create(doc, pts, typeId, level.Id);
                    tx.Commit();
                    return new CommandResultDto(true, $"Created toposolid {surface.Id} from {pts.Count} points on {level.Name}.", 1);
                }
                catch (Exception inner)
                {
                    try { tx.RollBack(); } catch { }
                    return new CommandResultDto(false, $"Failed: {inner.Message}");
                }
            }
            catch (Exception ex)
            {
                return new CommandResultDto(false, $"Failed: {ex.Message}");
            }
        }, ct);
    }
}
