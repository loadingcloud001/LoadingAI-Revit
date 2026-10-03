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
using StructuralType = Autodesk.Revit.DB.Structure.StructuralType;

namespace LoadingAI.Revit;

/// <summary>
/// Creation slice: walls, family instances, floors, roofs, grids, arrays, rotate, mirror, join geometry, topography.
/// Implements <c>IRevitCreateCommand</c>. Part of <see cref="AdapterRevitBridge"/>; see that type for the contract.
/// </summary>
public sealed partial class AdapterRevitBridge
{
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

    public Task<CommandResultDto> CreateWallAsync(
        string levelName,
        double x1, double y1, double x2, double y2,
        string? wallTypeName = null,
        double? heightMm = null,
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
                    // A plain wall, never a curtain or stacked type that happens to sort first.
                    var wt = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
                        .FirstOrDefault(t => t.Kind == WallKind.Basic);
                    if (wt == null) return new CommandResultDto(false, "No basic wall type found. Pass wallTypeName.");
                    wallTypeId = wt.Id;
                }

                var height = heightMm ?? DefaultWallHeightMm;
                if (height < 100 || height > 300000)
                    return new CommandResultDto(false, $"heightMm must be between 100 and 300000 (got {height:F0}).");
                var p1 = new XYZ(Metric.MmToFt(x1), Metric.MmToFt(y1), 0);
                var p2 = new XYZ(Metric.MmToFt(x2), Metric.MmToFt(y2), 0);
                var line = Line.CreateBound(p1, p2);
                using var tx = new Transaction(doc, "LoadingAI for Revit: create wall");
                tx.Start();
                try
                {
                    var wall = Wall.Create(doc, line, wallTypeId, level.Id, Metric.MmToFt(height), 0, false, false);
                    tx.Commit();
                    var id = wall.Id.ToString();
                    return new CommandResultDto(
                        true,
                        $"Created wall {id} on {level.Name} from ({x1:F0},{y1:F0}) to ({x2:F0},{y2:F0}) mm, height {height:F0} mm, type {wallTypeId}.",
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
                    // mirrorCopies is false: the elements are mirrored where they stand, nothing is copied.
                    ElementTransformUtils.MirrorElements(doc, eids, plane, false);
                    tx.Commit();
                    return new CommandResultDto(
                        true,
                        $"Mirrored {eids.Count} element(s) in place across the line ({x1:F0},{y1:F0})-({x2:F0},{y2:F0}) mm.",
                        eids.Count);
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
