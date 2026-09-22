using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Analysis;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using LoadingAI.Llm;
using LoadingAI.Revit.Formwork;
using LoadingAI.Revit.Openings;
using LoadingAI.Revit.StructureCuts;
using LoadingAI.Tools;

namespace LoadingAI.Revit;

/// <summary>
/// App-side implementation of the revit-free <see cref="IRevitBridge"/> contract,
/// wrapping the low-level <see cref="RevitBridge"/>. All Autodesk.Revit types
/// stay in this adapter; callers see only the Core DTOs.
/// </summary>
public sealed class AdapterRevitBridge : IRevitBridge
{
    private readonly RevitBridge _bridge;

    /// <summary>Exports write whole models to disk, so they get a longer leash than <see cref="RevitBridge.DefaultTimeout"/>.</summary>
    private static readonly TimeSpan ExportTimeout = TimeSpan.FromMinutes(10);

    public AdapterRevitBridge(RevitBridge bridge) => _bridge = bridge;

    public bool HasActiveDocument => _bridge.HasActiveDocument;

    public Task<ModelSummaryDto?> GetModelSummaryAsync(CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return null;
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Count(v => !v.IsTemplate);
            int Count(BuiltInCategory bic) =>
                new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().ToElementIds().Count;
            return new ModelSummaryDto(
                doc.Title,
                string.IsNullOrEmpty(doc.PathName) ? "(unsaved)" : doc.PathName,
                !string.IsNullOrEmpty(doc.PathName),
                views,
                Count(BuiltInCategory.OST_Walls),
                Count(BuiltInCategory.OST_Doors),
                Count(BuiltInCategory.OST_Windows));
        }, ct);
    }

    public Task<SelectionSummaryDto> GetSelectionSummaryAsync(CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (uidoc == null || doc == null)
                return new SelectionSummaryDto(0, Array.Empty<string>(), Array.Empty<string>());
            var ids = uidoc.Selection.GetElementIds();
            var cats = new List<string>();
            var catSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var idStrs = new List<string>();
            foreach (var eid in ids.Take(500))
            {
                var el = doc.GetElement(eid);
                if (el == null) continue;
                var cat = el.Category?.Name ?? "No Category";
                if (catSet.Add(cat)) cats.Add(cat);
                idStrs.Add(eid.ToString());
            }
            return new SelectionSummaryDto(ids.Count, cats, idStrs);
        }, ct);
    }

    public Task<ViewInfoDto?> GetActiveViewInfoAsync(CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc?.Document;
            var view = doc?.ActiveView;
            if (uidoc == null || doc == null || view == null) return null;
            var count = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().ToElementIds().Count;
            var isVisible = uidoc.GetOpenUIViews().Any(v => v.ViewId == view.Id);
            return new ViewInfoDto(view.Name, view.ViewType.ToString(), isVisible, count);
        }, ct);
    }

    public Task<IReadOnlyList<ElementSummaryDto>> FindElementsAsync(
        string? family = null, string? category = null, string? typeName = null,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return (IReadOnlyList<ElementSummaryDto>)Array.Empty<ElementSummaryDto>();

            var bic = !string.IsNullOrWhiteSpace(category) ? AllTools.ParseCategory(category) : null;
            var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
            if (bic.HasValue) collector = collector.OfCategory(bic.Value);

            var list = new List<ElementSummaryDto>();
            foreach (var el in collector.ToElements().Take(200))
            {
                if (!string.IsNullOrWhiteSpace(family) && !(el is FamilyInstance fi && fi.Symbol?.Family?.Name.IndexOf(family, StringComparison.OrdinalIgnoreCase) >= 0))
                    continue;
                if (!string.IsNullOrWhiteSpace(typeName))
                {
                    var t = doc.GetElement(el.GetTypeId()) as ElementType;
                    if (!(t?.Name.IndexOf(typeName, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                }
                list.Add(new ElementSummaryDto(
                    el.Id.ToString(),
                    el.Category?.Name ?? "No Category",
                    (el as FamilyInstance)?.Symbol?.Family?.Name ?? "",
                    (doc.GetElement(el.GetTypeId()) as ElementType)?.Name ?? el.GetType().Name,
                    el.Name));
            }
            return (IReadOnlyList<ElementSummaryDto>)list;
        }, ct);
    }

    public Task<IReadOnlyList<WarningDto>> GetWarningsAsync(CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return (IReadOnlyList<WarningDto>)Array.Empty<WarningDto>();
            var list = doc.GetWarnings()
                .GroupBy(w => w.GetDescriptionText())
                .Select(g => new WarningDto(
                    g.Key,
                    g.Count(),
                    "warning",
                    g.SelectMany(w => w.GetFailingElements().Take(3).Select(id => id.ToString()))
                        .Distinct()
                        .Take(5)
                        .ToList()))
                .ToList();
            return (IReadOnlyList<WarningDto>)list;
        }, ct);
    }

    public Task<IReadOnlyList<WarningItemDto>> GetWarningsRawAsync(int limit, CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return (IReadOnlyList<WarningItemDto>)Array.Empty<WarningItemDto>();
            var lim = Polyfills.Clamp(limit, 1, 50);
            var list = doc.GetWarnings()
                .Take(lim)
                .Select(w => new WarningItemDto(
                    w.GetDescriptionText(),
                    w.GetFailingElements().Take(2).Select(id => id.ToString()).ToList()))
                .ToList();
            return (IReadOnlyList<WarningItemDto>)list;
        }, ct);
    }

    public Task<IReadOnlyList<LevelDto>> GetLevelsAsync(CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return (IReadOnlyList<LevelDto>)Array.Empty<LevelDto>();
            var list = new FilteredElementCollector(doc).OfClass(typeof(Level))
                .Cast<Level>()
                .Select(l => new LevelDto(l.Name, l.Elevation, l.Id.ToString()))
                .ToList();
            return (IReadOnlyList<LevelDto>)list;
        }, ct);
    }

    public Task<IReadOnlyList<RoomDto>> GetRoomsAsync(string? levelName = null, int limit = 200, CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return (IReadOnlyList<RoomDto>)Array.Empty<RoomDto>();
            limit = Polyfills.Clamp(limit, 1, 500);
            var list = new List<RoomDto>();
            foreach (var el in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType())
            {
                if (el is not Room r) continue;
                var lvl = doc.GetElement(r.LevelId) as Level;
                var lvlName = lvl?.Name ?? "";
                if (!string.IsNullOrWhiteSpace(levelName) && !lvlName.Equals(levelName, StringComparison.OrdinalIgnoreCase))
                    continue;
                var number = r.Number ?? "";
                var name = r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? r.Name ?? "";
                var unplaced = r.Location == null;
                list.Add(new RoomDto(r.Id.ToString(), number, name, lvlName, r.Area, unplaced));
                if (list.Count >= limit) break;
            }
            return (IReadOnlyList<RoomDto>)list;
        }, ct);
    }

    public Task<WorksetListDto?> GetWorksetsAsync(CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return null;
            var tbl = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets();
            var list = tbl.Select(w => new WorksetDto(w.Id.IntegerValue.ToString(), w.Name, w.IsOpen, w.IsVisibleByDefault)).ToList();
            return new WorksetListDto(doc.IsWorkshared, list);
        }, ct);
    }

    public Task<IReadOnlyList<PhaseDto>> GetPhasesAsync(CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return (IReadOnlyList<PhaseDto>)Array.Empty<PhaseDto>();
            var list = doc.Phases.Cast<Phase>().Select(p => new PhaseDto(p.Id.ToString(), p.Name)).ToList();
            return (IReadOnlyList<PhaseDto>)list;
        }, ct);
    }

    public Task<IReadOnlyList<DesignOptionDto>> GetDesignOptionsAsync(CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return (IReadOnlyList<DesignOptionDto>)Array.Empty<DesignOptionDto>();
            var list = new FilteredElementCollector(doc).OfClass(typeof(DesignOption)).Cast<DesignOption>()
                .Select(o => new DesignOptionDto(o.Id.ToString(), o.Name, o.IsPrimary))
                .ToList();
            return (IReadOnlyList<DesignOptionDto>)list;
        }, ct);
    }

    public Task<SheetsViewsDto?> GetSheetsViewsAsync(CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return null;
            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
            var sheetSample = sheets.Take(10).Select(s => new SheetSummaryDto(s.Id.ToString(), s.Name, s.SheetNumber)).ToList();
            var byType = views.GroupBy(v => v.ViewType.ToString())
                .Select(g => new CategoryCountDto(g.Key, g.Count()))
                .ToList();
            return new SheetsViewsDto(sheets.Count, views.Count, sheetSample, byType);
        }, ct);
    }

    public Task<FamilyTypeListDto?> GetFamilyTypesAsync(string? category = null, CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return null;
            var syms = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToList();
            if (!string.IsNullOrWhiteSpace(category))
            {
                var cap = category;
                syms = syms.Where(s =>
                    s.Category?.Name.Equals(cap, StringComparison.OrdinalIgnoreCase) == true
                    || s.FamilyName.IndexOf(cap, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            }
            var sample = syms.Take(20).Select(s => new FamilyTypeDto(s.Id.ToString(), s.FamilyName, s.Name, s.Category?.Name ?? "")).ToList();
            return new FamilyTypeListDto(syms.Count, sample);
        }, ct);
    }

    public Task<MaterialListDto?> GetMaterialsAsync(CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return null;
            var mats = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().ToList();
            var sample = mats.Take(20).Select(m => new MaterialDto(m.Id.ToString(), m.Name, m.MaterialClass ?? "")).ToList();
            return new MaterialListDto(mats.Count, sample);
        }, ct);
    }

    public Task<RoomAreaDto?> GetRoomAreaAsync(string? levelName = null, CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return null;
            var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .ToList();
            if (!string.IsNullOrWhiteSpace(levelName))
            {
                var cap = levelName;
                rooms = rooms.Where(r => doc.GetElement(r.LevelId) is Level l && l.Name.Equals(cap, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            double total = rooms.Sum(r => r.get_Parameter(BuiltInParameter.ROOM_AREA)?.AsDouble() ?? 0);
            var sample = rooms.Take(10).Select(r =>
            {
                var lvl = (doc.GetElement(r.LevelId) as Level)?.Name ?? "";
                var area = r.get_Parameter(BuiltInParameter.ROOM_AREA)?.AsDouble() ?? 0;
                return new RoomDto(r.Id.ToString(), r.Number ?? "", r.Name ?? "", lvl, area, r.Location == null);
            }).ToList();
            return new RoomAreaDto(rooms.Count, total, sample);
        }, ct);
    }

    public Task<ClashResultDto?> GetClashByBoundingBoxAsync(
        string category1, string category2, int limit = 20, CancellationToken ct = default)
    {
        limit = Polyfills.Clamp(limit, 1, 100);
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return null;
            var bic1 = AllTools.ParseCategory(category1);
            var bic2 = AllTools.ParseCategory(category2);
            if (bic1 == null || bic2 == null)
                return new ClashResultDto(category1, category2, 0, false, Array.Empty<ClashPairDto>(), $"Unknown categories {category1}/{category2}");
            var a = new FilteredElementCollector(doc).OfCategory(bic1.Value).WhereElementIsNotElementType().ToElements().Take(200).ToList();
            var b = new FilteredElementCollector(doc).OfCategory(bic2.Value).WhereElementIsNotElementType().ToElements().Take(200).ToList();
            var clashes = new List<ClashPairDto>();
            foreach (var elA in a)
            {
                var bbA = elA.get_BoundingBox(null);
                if (bbA == null) continue;
                foreach (var elB in b)
                {
                    var bbB = elB.get_BoundingBox(null);
                    if (bbB == null) continue;
                    if (bbA.Max.X < bbB.Min.X || bbA.Min.X > bbB.Max.X
                        || bbA.Max.Y < bbB.Min.Y || bbA.Min.Y > bbB.Max.Y
                        || bbA.Max.Z < bbB.Min.Z || bbA.Min.Z > bbB.Max.Z)
                        continue;
                    clashes.Add(new ClashPairDto(elA.Id.ToString(), elA.Category?.Name ?? "", elB.Id.ToString(), elB.Category?.Name ?? ""));
                    if (clashes.Count >= limit) break;
                }
                if (clashes.Count >= limit) break;
            }
            return new ClashResultDto(category1, category2, clashes.Count, true, clashes);
        }, ct);
    }

    public Task<ModelHealthDto?> GetModelHealthScoreAsync(CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return null;
            var warns = doc.GetWarnings().Count;
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Count(v => !v.IsTemplate);
            var warnScore = Math.Max(0, 100 - warns * 2);
            var viewScore = Math.Min(20, views / 20);
            var total = warnScore * 0.7 + viewScore * 1.5;
            return new ModelHealthDto(total, warns, warnScore, views, doc.IsWorkshared, doc.Title);
        }, ct);
    }

    public Task<QuantityTakeoffDto?> GetQuantityTakeoffAsync(string category, CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return null;
            var bic = AllTools.ParseCategory(category);
            if (bic == null)
                return new QuantityTakeoffDto(
                    category, 0, 0, 0, 0, Array.Empty<QuantityTakeoffRowDto>(), $"Unknown category {category}");

            var elems = new FilteredElementCollector(doc)
                .OfCategory(bic.Value)
                .WhereElementIsNotElementType()
                .ToElements()
                .ToList();
            double totalArea = 0, totalVol = 0, totalLen = 0;
            var rows = new List<QuantityTakeoffRowDto>(elems.Count);
            foreach (var el in elems)
            {
                double a = el.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0;
                double v = el.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED)?.AsDouble() ?? 0;
                double l = el.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0;
                totalArea += a;
                totalVol += v;
                totalLen += l;
                var lvl = doc.GetElement(el.LevelId) as Level;
                rows.Add(new QuantityTakeoffRowDto(el.Id.ToString(), el.Name, lvl?.Name, a, v, l));
            }
            return new QuantityTakeoffDto(category, elems.Count, totalArea, totalVol, totalLen, rows);
        }, ct);
    }

    public Task<IReadOnlyList<GridDto>> GetGridsAsync(CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return (IReadOnlyList<GridDto>)Array.Empty<GridDto>();
            var list = new FilteredElementCollector(doc).OfClass(typeof(Grid))
                .Cast<Grid>()
                .Select(g => new GridDto(g.Name, g.Id.ToString()))
                .ToList();
            return (IReadOnlyList<GridDto>)list;
        }, ct);
    }

    public Task<IReadOnlyList<QuantityDto>> GetQuantitiesAsync(CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return (IReadOnlyList<QuantityDto>)Array.Empty<QuantityDto>();
            var list = new List<QuantityDto>();
            foreach (var bic in new[]
            {
                BuiltInCategory.OST_Walls, BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows,
                BuiltInCategory.OST_Floors, BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_Roofs,
                BuiltInCategory.OST_Columns
            })
            {
                int count = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().ToElementIds().Count;
                if (count == 0) continue;
                list.Add(new QuantityDto(
                    bic.ToString(),
                    count,
                    0, 0,
                    0));
            }
            return (IReadOnlyList<QuantityDto>)list;
        }, ct);
    }

    public Task<SyncInfoDto?> GetSyncInfoAsync(CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return null;
            string centralPath = "";
            try
            {
                if (doc.IsWorkshared)
                    centralPath = doc.GetWorksharingCentralModelPath()?.ToString() ?? "";
            }
            catch { /* unsupported on this version */ }
            bool isCentral = doc.IsWorkshared
                && !string.IsNullOrEmpty(doc.PathName)
                && centralPath.Equals(doc.PathName, StringComparison.OrdinalIgnoreCase);
            return new SyncInfoDto(
                centralPath,
                isCentral,
                doc.IsWorkshared,
                "",
                0);
        }, ct);
    }

    public Task<WallMetricsDto?> GetWallMetricsAsync(string? levelName = null, int limit = 500, CancellationToken ct = default)
    {
        if (limit < 1) limit = 1;
        if (limit > 500) limit = 500;
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return null;
            var walls = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Walls)
                .WhereElementIsNotElementType()
                .ToElements()
                .OfType<Wall>()
                .ToList();
            if (!string.IsNullOrWhiteSpace(levelName))
            {
                walls = walls.Where(w =>
                    w.LevelId != ElementId.InvalidElementId
                    && doc.GetElement(w.LevelId) is Level lvl
                    && lvl.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            int count = walls.Count;
            double totalLength = 0, totalArea = 0, totalVol = 0;
            int sampled = 0;
            foreach (var w in walls.Take(limit))
            {
                var lenP = w.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
                if (lenP != null && lenP.StorageType == StorageType.Double) totalLength += lenP.AsDouble();
                var areaP = w.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                if (areaP != null && areaP.StorageType == StorageType.Double) totalArea += areaP.AsDouble();
                var volP = w.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED);
                if (volP != null && volP.StorageType == StorageType.Double) totalVol += volP.AsDouble();
                sampled++;
            }
            return new WallMetricsDto(count, sampled, levelName, totalLength, totalArea, totalVol, count > limit);
        }, ct);
    }

    public Task<VisibleElementsDto?> GetVisibleElementsInViewAsync(string? category = null, int limit = 100, CancellationToken ct = default)
    {
        if (limit < 1) limit = 1;
        if (limit > 500) limit = 500;
        return _bridge.RunAsync(uiApp =>
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc?.Document;
            var view = doc?.ActiveView;
            if (uidoc == null || doc == null || view == null) return null;
            FilteredElementCollector col = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType();
            if (!string.IsNullOrWhiteSpace(category))
            {
                var bic = AllTools.ParseCategory(category);
                if (bic == null)
                    return new VisibleElementsDto(view.Name, 0, category, $"Unknown category '{category}'. Try Walls, Doors, Windows, Floors, etc.", Array.Empty<string>());
                col = col.OfCategory(bic.Value);
            }
            var ids = col.ToElementIds();
            return new VisibleElementsDto(
                view.Name,
                ids.Count,
                category,
                null,
                ids.Take(limit).Select(id => id.ToString()).ToList());
        }, ct);
    }

    public Task<ElementDetailsDto?> GetElementDetailsAsync(string elementId, CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return null;
            try
            {
                var el = doc.GetElement(AllTools.ParseEid(elementId));
                if (el == null)
                    return new ElementDetailsDto(elementId, "", "", "", "", "", Array.Empty<ParameterValueDto>(), 0, $"Element {elementId} not found.");
                var cat = el.Category?.Name ?? "NoCat";
                var type = doc.GetElement(el.GetTypeId()) as ElementType;
                var level = doc.GetElement(el.LevelId) as Level;
                var loc = el.Location is LocationPoint lp ? $"Point {lp.Point}" : el.Location is LocationCurve lc ? $"Curve {lc.Curve}" : "No location";
                var parameters = new List<ParameterValueDto>();
                int n = 0;
                foreach (Parameter p in el.Parameters)
                {
                    if (n++ >= 30) break;
                    string vs = p.StorageType switch
                    {
                        StorageType.String => p.AsString() ?? "",
                        StorageType.Integer => p.AsInteger().ToString(),
                        StorageType.Double => p.AsDouble().ToString("F3"),
                        StorageType.ElementId => p.AsElementId().ToString(),
                        _ => "?"
                    };
                    parameters.Add(new ParameterValueDto(p.Definition.Name, vs, p.IsReadOnly, p.StorageType.ToString()));
                }
                return new ElementDetailsDto(
                    el.Id.ToString(), el.Name, cat, type?.Name ?? el.GetType().Name,
                    level?.Name ?? el.LevelId.ToString(), loc, parameters, el.Parameters.Size, null);
            }
            catch (Exception ex)
            {
                return new ElementDetailsDto(elementId, "", "", "", "", "", Array.Empty<ParameterValueDto>(), 0, $"Failed: {ex.Message}");
            }
        }, ct);
    }

    public Task<FindResultDto> FindElementsFilteredAsync(
        string category,
        string? paramName = null, string? op = null, string? value = null,
        int limit = 100, int offset = 0,
        CancellationToken ct = default)
    {
        if (limit < 1) limit = 1;
        if (limit > 500) limit = 500;
        if (offset < 0) offset = 0;
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null)
                return new FindResultDto(0, offset, "Please open a model first.", Array.Empty<ElementSummaryDto>());
            var bic = AllTools.ParseCategory(category);
            if (bic == null)
                return new FindResultDto(0, offset, $"Unknown category '{category}'.", Array.Empty<ElementSummaryDto>());
            var elements = new FilteredElementCollector(doc).OfCategory(bic.Value).WhereElementIsNotElementType().ToElements().ToList();
            if (!string.IsNullOrWhiteSpace(paramName))
            {
                op ??= "=";
                var filtered = new List<Element>();
                foreach (var el in elements)
                {
                    var p = el.LookupParameter(paramName!);
                    if (op == "empty") { if (p == null || string.IsNullOrWhiteSpace(p.AsString())) filtered.Add(el); continue; }
                    if (op == "notempty") { if (p != null && !string.IsNullOrWhiteSpace(p.AsString())) filtered.Add(el); continue; }
                    if (p == null) continue;
                    string pv = (p.StorageType == StorageType.String ? p.AsString() : p.AsValueString() ?? p.AsString()) ?? "";
                    bool match = op switch
                    {
                        "=" => pv.Equals(value ?? "", StringComparison.OrdinalIgnoreCase),
                        "!=" => !pv.Equals(value ?? "", StringComparison.OrdinalIgnoreCase),
                        "contains" => pv.IndexOf(value ?? "", StringComparison.OrdinalIgnoreCase) >= 0,
                        _ => false
                    };
                    if (match) filtered.Add(el);
                }
                elements = filtered;
            }
            int total = elements.Count;
            var page = elements.Skip(offset).Take(limit).Select(e => new ElementSummaryDto(
                e.Id.ToString(),
                e.Category?.Name ?? "No Category",
                (e as FamilyInstance)?.Symbol?.Family?.Name ?? "",
                (doc.GetElement(e.GetTypeId()) as ElementType)?.Name ?? e.GetType().Name,
                e.Name)).ToList();
            return new FindResultDto(total, offset, null, page);
        }, ct);
    }

    public Task<IReadOnlyList<ElementQuantityDto>> GetElementQuantitiesAsync(
        IReadOnlyList<string>? elementIds = null, CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (uidoc == null || doc == null) return (IReadOnlyList<ElementQuantityDto>)Array.Empty<ElementQuantityDto>();
            ICollection<ElementId> ids;
            if (elementIds != null && elementIds.Count > 0)
                ids = elementIds.Select(AllTools.ParseEid).ToList();
            else
                ids = uidoc.Selection.GetElementIds();
            var list = new List<ElementQuantityDto>();
            foreach (var eid in ids.Take(50))
            {
                var el = doc.GetElement(eid);
                if (el == null)
                {
                    list.Add(new ElementQuantityDto(eid.ToString(), "", "not found", null, null, null, null));
                    continue;
                }
                var len = el.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
                var area = el.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                var vol = el.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED);
                string? bbStr = null;
                var bb = el.get_BoundingBox(doc.ActiveView);
                if (bb != null)
                    bbStr = $"({bb.Min.X:F1},{bb.Min.Y:F1},{bb.Min.Z:F1})-({bb.Max.X:F1},{bb.Max.Y:F1},{bb.Max.Z:F1})";
                list.Add(new ElementQuantityDto(
                    eid.ToString(),
                    el.Category?.Name ?? "NoCat",
                    el.Name,
                    len != null && len.StorageType == StorageType.Double ? len.AsDouble() : null,
                    area != null && area.StorageType == StorageType.Double ? area.AsDouble() : null,
                    vol != null && vol.StorageType == StorageType.Double ? vol.AsDouble() : null,
                    bbStr));
            }
            return (IReadOnlyList<ElementQuantityDto>)list;
        }, ct);
    }

    public Task<ActiveViewDetailsDto?> GetActiveViewDetailsAsync(int limit = 200, CancellationToken ct = default)
    {
        if (limit < 1) limit = 1;
        if (limit > 500) limit = 500;
        return _bridge.RunAsync(uiApp =>
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc?.Document;
            var view = doc?.ActiveView;
            if (uidoc == null || doc == null || view == null) return null;
            var all = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().ToElements();
            var byCat = new Dictionary<string, int>();
            foreach (var el in all)
            {
                var cat = el.Category?.Name ?? "NoCat";
                byCat[cat] = byCat.TryGetValue(cat, out var c) ? c + 1 : 1;
            }
            var top = byCat.OrderByDescending(kv => kv.Value).Take(10)
                .Select(kv => new CategoryCountDto(kv.Key, kv.Value)).ToList();
            return new ActiveViewDetailsDto(
                view.Name,
                view.ViewType.ToString(),
                view.Id.ToString(),
                view.Scale,
                view.DetailLevel.ToString(),
                all.Count,
                top,
                all.Take(limit).Select(e => e.Id.ToString()).ToList());
        }, ct);
    }

    public Task<CommandResultDto> IsolateInViewAsync(
        IReadOnlyList<string>? elementIds = null,
        string? category = null,
        bool isolate = true,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc?.Document;
            var view = doc?.ActiveView;
            if (uidoc == null || doc == null) return new CommandResultDto(false, "Please open a model first.");
            if (view == null) return new CommandResultDto(false, "No active view.");
            if (view.IsTemplate) return new CommandResultDto(false, "Cannot isolate in a view template.");
            if ((elementIds == null || elementIds.Count == 0) && string.IsNullOrWhiteSpace(category))
                return new CommandResultDto(false, "Provide elementIds or category.");

            using var tx = new Transaction(doc, isolate
                ? $"LoadingAI for Revit: isolate {category ?? elementIds!.Count.ToString()} in {view.Name}"
                : $"LoadingAI for Revit: reset isolate in {view.Name}");
            tx.Start();
            try
            {
                int affected = 0;
                if (!view.IsTemporaryHideIsolateActive() || isolate)
                {
                    if (!string.IsNullOrWhiteSpace(category) && (elementIds == null || elementIds.Count == 0))
                    {
                        var bic = AllTools.ParseCategory(category);
                        if (bic == null) { tx.RollBack(); return new CommandResultDto(false, $"Unknown category '{category}'."); }
                        var catObj = doc.Settings.Categories.get_Item(bic.Value);
                        if (catObj == null) { tx.RollBack(); return new CommandResultDto(false, $"Category '{category}' not found."); }
                        view.IsolateCategoryTemporary(catObj.Id);
                        affected = 0;
                    }
                    else
                    {
                        var ids = elementIds!.Select(AllTools.ParseEid).ToList();
                        view.IsolateElementsTemporary(ids);
                        affected = ids.Count;
                    }
                }
                if (!isolate)
                    view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                tx.Commit();
                var msg = isolate
                    ? $"Isolated {(category ?? affected + " elements")} in view '{view.Name}' (temporary). Use reset_temporary_view to restore."
                    : $"Reset isolate in '{view.Name}'.";
                return new CommandResultDto(true, msg, affected, view.Name);
            }
            catch (Exception ex)
            {
                try { tx.RollBack(); } catch { }
                return new CommandResultDto(false, $"Failed: {ex.Message}");
            }
        }, ct);
    }

    public Task<CommandResultDto> HideInViewAsync(
        IReadOnlyList<string>? elementIds = null,
        string? category = null,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc?.Document;
            var view = doc?.ActiveView;
            if (uidoc == null || doc == null) return new CommandResultDto(false, "Please open a model first.");
            if (view == null) return new CommandResultDto(false, "No active view.");
            if ((elementIds == null || elementIds.Count == 0) && string.IsNullOrWhiteSpace(category))
                return new CommandResultDto(false, "Provide elementIds or category.");

            using var tx = new Transaction(doc, $"LoadingAI for Revit: hide {category ?? elementIds!.Count.ToString()} in {view.Name}");
            tx.Start();
            try
            {
                ICollection<ElementId> ids;
                if (!string.IsNullOrWhiteSpace(category) && (elementIds == null || elementIds.Count == 0))
                {
                    var bic = AllTools.ParseCategory(category);
                    if (bic == null) { tx.RollBack(); return new CommandResultDto(false, $"Unknown category '{category}'. Try RVT Links, Walls, Doors, etc."); }
                    ids = new FilteredElementCollector(doc, view.Id).OfCategory(bic.Value).WhereElementIsNotElementType().ToElementIds();
                    if (ids.Count == 0) { tx.RollBack(); return new CommandResultDto(false, $"No visible {category} in view '{view.Name}'."); }
                    view.HideElementsTemporary(ids);
                    tx.Commit();
                    return new CommandResultDto(true, $"Hid {ids.Count} {category} in '{view.Name}' (temporary). Use reset_temporary_view to restore.", ids.Count, view.Name);
                }

                ids = elementIds!.Select(AllTools.ParseEid).ToList();
                view.HideElementsTemporary(ids);
                tx.Commit();
                return new CommandResultDto(true, $"Hid {ids.Count} element(s) in '{view.Name}' (temporary).", ids.Count, view.Name);
            }
            catch (Exception ex)
            {
                try { tx.RollBack(); } catch { }
                return new CommandResultDto(false, $"Failed: {ex.Message}");
            }
        }, ct);
    }

    public Task<CommandResultDto> ResetViewVisibilityAsync(CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc?.Document;
            var view = doc?.ActiveView;
            if (uidoc == null || doc == null) return new CommandResultDto(false, "Please open a model first.");
            if (view == null) return new CommandResultDto(false, "No active view.");
            if (!view.IsTemporaryHideIsolateActive())
                return new CommandResultDto(true, $"No temporary hide/isolate active in '{view.Name}'.", 0, view.Name);

            using var tx = new Transaction(doc, $"LoadingAI for Revit: reset view {view.Name}");
            tx.Start();
            try
            {
                view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                tx.Commit();
                return new CommandResultDto(true, $"Reset temporary view in '{view.Name}'.", 0, view.Name);
            }
            catch (Exception ex)
            {
                try { tx.RollBack(); } catch { }
                return new CommandResultDto(false, $"Failed: {ex.Message}");
            }
        }, ct);
    }

    public Task<CommandResultDto> OverrideColorInViewAsync(
        IReadOnlyList<string>? elementIds = null,
        string? category = null,
        string? color = null,
        bool reset = false,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            var view = doc.ActiveView;
            if (view == null) return new CommandResultDto(false, "No active view.");
            if (view.IsTemplate) return new CommandResultDto(false, "Cannot override in view template.");
            if ((elementIds == null || elementIds.Count == 0) && string.IsNullOrWhiteSpace(category))
                return new CommandResultDto(false, "Provide elementIds or category.");

            try
            {
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

                using var tx = new Transaction(doc, reset
                    ? $"LoadingAI for Revit: reset color in {view.Name}"
                    : $"LoadingAI for Revit: color {colorName} in {view.Name}");
                tx.Start();
                try
                {
                    if (!string.IsNullOrWhiteSpace(category) && (elementIds == null || elementIds.Count == 0))
                    {
                        var bic = AllTools.ParseCategory(category);
                        if (bic == null) { tx.RollBack(); return new CommandResultDto(false, $"Unknown category '{category}'."); }
                        var catObj = doc.Settings.Categories.get_Item(bic.Value);
                        if (catObj == null) { tx.RollBack(); return new CommandResultDto(false, $"Category '{category}' not found."); }
                        view.SetCategoryOverrides(catObj.Id, reset ? new OverrideGraphicSettings() : ogs);
                        tx.Commit();
                        var catMsg = reset
                            ? $"Reset color for {category} in '{view.Name}'."
                            : $"Set {colorName} override for {category} in '{view.Name}'.";
                        return new CommandResultDto(true, catMsg, 0, view.Name);
                    }

                    var ids = elementIds!.Select(AllTools.ParseEid).ToList();
                    foreach (var id in ids) view.SetElementOverrides(id, reset ? new OverrideGraphicSettings() : ogs);
                    tx.Commit();
                    var msg = reset
                        ? $"Reset color for {ids.Count} element(s) in '{view.Name}'."
                        : $"Set {colorName} override for {ids.Count} element(s) in '{view.Name}'.";
                    return new CommandResultDto(true, msg, ids.Count, view.Name);
                }
                catch
                {
                    try { tx.RollBack(); } catch { }
                    throw;
                }
            }
            catch (Exception ex)
            {
                return new CommandResultDto(false, $"Failed: {ex.Message}");
            }
        }, ct);
    }

    public Task<CommandResultDto> ColorByConstructionScheduleAsync(
        string startDate,
        int intervalDays = 14,
        IReadOnlyList<string>? categories = null,
        string? palette = null,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            if (!AllTools.TryParseDate(startDate ?? "", out var parsedStart))
                return new CommandResultDto(false, $"Invalid startDate '{startDate}'.");
            var interval = intervalDays < 1 ? 1 : (intervalDays > 365 ? 365 : intervalDays);
            var paletteName = string.IsNullOrWhiteSpace(palette) ? "rainbow" : palette;

            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            var view = doc.ActiveView;
            if (view == null) return new CommandResultDto(false, "No active view.");
            if (view.IsTemplate) return new CommandResultDto(false, "Cannot color in view template.");

            try
            {
                var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
                var levelById = levels.ToDictionary(l => l.Id, l => l.Name);
                var colors = GeneratePalette(levels.Count, paletteName);
                var dateByLevel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < levels.Count; i++)
                    dateByLevel[levels[i].Name] = parsedStart.AddDays(i * interval).ToString("yyyy-MM-dd");

                var bics = new List<BuiltInCategory>();
                if (categories != null && categories.Count > 0)
                {
                    if (categories.Count == 1 && categories[0].Equals("all", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] defaults = { "Walls", "Doors", "Windows", "Floors", "Ceilings", "Roofs", "Structural Framing", "Structural Columns", "Columns", "Generic Models" };
                        foreach (var c in defaults)
                        {
                            var bic = AllTools.ParseCategory(c);
                            if (bic != null) bics.Add(bic.Value);
                        }
                    }
                    else
                    {
                        foreach (var c in categories)
                        {
                            var bic = AllTools.ParseCategory(c);
                            if (bic != null) bics.Add(bic.Value);
                        }
                    }
                }
                else
                {
                    bics.Add(BuiltInCategory.OST_Walls);
                }

                var levelToIds = new Dictionary<string, List<ElementId>>(StringComparer.OrdinalIgnoreCase);
                foreach (var lvl in levels) levelToIds[lvl.Name] = new List<ElementId>();

                foreach (var bic in bics)
                {
                    var elems = new FilteredElementCollector(doc, view.Id).OfCategory(bic).WhereElementIsNotElementType().ToElements();
                    foreach (var el in elems)
                    {
                        string? lvlName = null;
                        if (el.LevelId != ElementId.InvalidElementId && levelById.TryGetValue(el.LevelId, out var ln))
                            lvlName = ln;
                        else
                        {
                            var p = el.LookupParameter("Base Constraint") ?? el.LookupParameter("Level") ?? el.LookupParameter("Reference Level") ?? el.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM) ?? el.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT);
                            if (p != null)
                            {
                                if (p.StorageType == StorageType.ElementId)
                                {
                                    var id = p.AsElementId();
                                    if (levelById.TryGetValue(id, out var n2)) lvlName = n2;
                                }
                                else lvlName = p.AsString() ?? p.AsValueString();
                            }
                        }
                        if (lvlName != null && levelToIds.ContainsKey(lvlName)) levelToIds[lvlName].Add(el.Id);
                    }
                }

                using var tx = new Transaction(doc, $"LoadingAI for Revit: color by schedule {parsedStart:yyyy-MM-dd}");
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
                    var msg = $"Colored {total} element(s) in '{view.Name}' by construction schedule bottom-up {parsedStart:yyyy-MM-dd} (+{interval}d) across {levels.Count} levels, {bics.Count} categories. Palette {paletteName}.";
                    msg += "\nLegend:\n" + string.Join("\n", levels.Select((l, i) => $"{l.Name} ({l.Elevation:F1}ft) = {dateByLevel[l.Name]} -> #{colors[i].Red:X2}{colors[i].Green:X2}{colors[i].Blue:X2}"));
                    msg += "\nTo reset: say 'reset view' (reset_temporary_view).";
                    return new CommandResultDto(true, msg, total, view.Name);
                }
                catch
                {
                    try { tx.RollBack(); } catch { }
                    throw;
                }
            }
            catch (Exception ex)
            {
                return new CommandResultDto(false, $"Failed: {ex.Message}");
            }
        }, ct);
    }

    public Task<CommandResultDto> ColorByParameterAsync(
        string paramName,
        string? category = null,
        IReadOnlyList<string>? elementIds = null,
        string? palette = null,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            if (string.IsNullOrWhiteSpace(paramName))
                return new CommandResultDto(false, "Provide paramName.");
            var paletteName = string.IsNullOrWhiteSpace(palette) ? "rainbow" : palette;

            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            var view = doc.ActiveView;
            if (view == null) return new CommandResultDto(false, "No active view.");
            if (view.IsTemplate) return new CommandResultDto(false, "Cannot color in view template.");

            try
            {
                List<Element> elems;
                if (elementIds != null && elementIds.Count > 0)
                {
                    elems = elementIds.Select(id => doc.GetElement(AllTools.ParseEid(id))).Where(e => e != null).Cast<Element>().ToList();
                }
                else
                {
                    var bic = AllTools.ParseCategory(string.IsNullOrWhiteSpace(category) ? "Walls" : category);
                    if (bic == null) return new CommandResultDto(false, $"Unknown category '{category}'.");
                    elems = new FilteredElementCollector(doc, view.Id).OfCategory(bic.Value).WhereElementIsNotElementType().ToElements().ToList();
                }
                var groups = new SortedDictionary<string, List<ElementId>>(StringComparer.OrdinalIgnoreCase);
                foreach (var el in elems)
                {
                    var key = ReadParam(el, paramName);
                    if (!groups.TryGetValue(key, out var ids)) { ids = new List<ElementId>(); groups[key] = ids; }
                    ids.Add(el.Id);
                }
                if (groups.Count == 0) return new CommandResultDto(false, "No elements to color.");
                var colors = GeneratePalette(groups.Count, paletteName);
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
                    return new CommandResultDto(
                        true,
                        $"Colored {total} element(s) in '{view.Name}' by '{paramName}' ({groups.Count} values). Palette {paletteName}.\nLegend:\n" + string.Join("\n", legend),
                        total,
                        view.Name);
                }
                catch
                {
                    try { tx.RollBack(); } catch { }
                    throw;
                }
            }
            catch (Exception ex)
            {
                return new CommandResultDto(false, $"Failed: {ex.Message}");
            }
        }, ct);
    }

    public Task<CommandResultDto> CreateSheetAsync(
        string sheetNumber, string sheetName, string? titleBlockName = null,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            try
            {
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

                using var tx = new Transaction(doc, $"LoadingAI for Revit: create sheet {sheetNumber}");
                tx.Start();
                try
                {
                    if (!tb.IsActive) tb.Activate();
                    var sheet = ViewSheet.Create(doc, tb.Id);
                    sheet.SheetNumber = sheetNumber;
                    sheet.Name = sheetName;
                    tx.Commit();
                    return new CommandResultDto(true, $"Created sheet {sheet.Id} {sheetNumber} \"{sheetName}\"", 1, ElementIds: new[] { sheet.Id.ToString() });
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

    public Task<CommandResultDto> PlaceViewOnSheetAsync(
        string viewId, string sheetId, double xMm = 0, double yMm = 0,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            var x = Metric.MmToFt(xMm);
            var y = Metric.MmToFt(yMm);
            try
            {
                var view = doc.GetElement(AllTools.ParseEid(viewId)) as View;
                if (view == null) return new CommandResultDto(false, $"View {viewId} not found.");
                var sheet = doc.GetElement(AllTools.ParseEid(sheetId)) as ViewSheet;
                if (sheet == null) return new CommandResultDto(false, $"Sheet {sheetId} not found.");
                using var tx = new Transaction(doc, $"LoadingAI for Revit: place {viewId} on {sheetId}");
                tx.Start();
                try
                {
                    var vp = Viewport.Create(doc, sheet.Id, view.Id, new XYZ(x, y, 0));
                    tx.Commit();
                    return new CommandResultDto(true, $"Placed view {viewId} on sheet {sheetId} at ({x},{y}) viewport {vp.Id}", 1, ElementIds: new[] { vp.Id.ToString() });
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

    public Task<CommandResultDto> DuplicateViewAsync(
        string viewId, bool withDetailing = false,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            try
            {
                var view = doc.GetElement(AllTools.ParseEid(viewId)) as View;
                if (view == null) return new CommandResultDto(false, $"View {viewId} not found.");
                using var tx = new Transaction(doc, "LoadingAI for Revit: duplicate view");
                tx.Start();
                try
                {
                    var nid = view.Duplicate(withDetailing ? ViewDuplicateOption.WithDetailing : ViewDuplicateOption.Duplicate);
                    tx.Commit();
                    return new CommandResultDto(true, $"Duplicated {viewId} -> {nid} {(withDetailing ? "with detailing" : "")}", 1, ElementIds: new[] { nid.ToString() });
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

    public Task<CommandResultDto> SetViewTemplateAsync(
        string viewId, string? templateId = null,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            try
            {
                var view = doc.GetElement(AllTools.ParseEid(viewId)) as View;
                if (view == null) return new CommandResultDto(false, $"View {viewId} not found.");
                using var tx = new Transaction(doc, "LoadingAI for Revit: set view template");
                tx.Start();
                try
                {
                    var tid = string.IsNullOrWhiteSpace(templateId) ? ElementId.InvalidElementId : AllTools.ParseEid(templateId);
                    view.ViewTemplateId = tid;
                    tx.Commit();
                    var msg = string.IsNullOrWhiteSpace(templateId)
                        ? $"Cleared template for {viewId}"
                        : $"Set template {templateId} for {viewId}";
                    return new CommandResultDto(true, msg, 1);
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

    public Task<CommandResultDto> SetCropBoxAsync(
        string viewId, bool enabled,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            try
            {
                var view = doc.GetElement(AllTools.ParseEid(viewId)) as View;
                if (view == null) return new CommandResultDto(false, $"View {viewId} not found.");
                using var tx = new Transaction(doc, "LoadingAI for Revit: set crop");
                tx.Start();
                try
                {
                    view.CropBoxActive = enabled;
                    view.CropBoxVisible = enabled;
                    tx.Commit();
                    return new CommandResultDto(true, $"Set crop {(enabled ? "on" : "off")} for {viewId}", 1);
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
                var w1 = doc.GetElement(AllTools.ParseEid(wallId1)) as Wall;
                var w2 = doc.GetElement(AllTools.ParseEid(wallId2)) as Wall;
                if (w1 == null || w2 == null) return new CommandResultDto(false, "Walls not found.");
                var c1 = (w1.Location as LocationCurve)?.Curve;
                var c2 = (w2.Location as LocationCurve)?.Curve;
                if (c1 == null || c2 == null) return new CommandResultDto(false, "Walls have no location curve.");

                using var tx = new Transaction(doc, "LoadingAI for Revit: create dimension");
                tx.Start();
                try
                {
                    var ra = new ReferenceArray();
                    ra.Append(new Reference(w1));
                    ra.Append(new Reference(w2));
                    var mid1 = c1.Evaluate(0.5, true);
                    var mid2 = c2.Evaluate(0.5, true);
                    var dir = (mid2 - mid1).Normalize();
                    var perp = new XYZ(-dir.Y, dir.X, 0);
                    var line = Line.CreateBound(mid1 + perp * 5, mid2 + perp * 5);
                    var dim = doc.Create.NewDimension(view, line, ra);
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

    public Task<CommandResultDto> TagAllInActiveViewAsync(
        string category,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            var view = uidoc?.ActiveView;
            if (view == null) return new CommandResultDto(false, "No active view.");
            try
            {
                var bic = AllTools.ParseCategory(category);
                if (bic == null) return new CommandResultDto(false, $"Unknown category {category}");
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

    public Task<CommandResultDto> SetParameterAsync(
        string elementId, string paramName, string value,
        CancellationToken ct = default)
    {
        return SetParametersAsync(new[] { elementId }, paramName, value, ct);
    }

    public Task<CommandResultDto> SetParametersAsync(
        IReadOnlyList<string> elementIds, string paramName, string value,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (uidoc == null || doc == null) return new CommandResultDto(false, "Please open a model first.");
            if (elementIds == null || elementIds.Count == 0)
                return new CommandResultDto(false, "Please select elements first or provide elementIds.");

            using var tx = new Transaction(doc, $"LoadingAI for Revit: set {paramName} on {elementIds.Count}");
            tx.Start();
            try
            {
                int updated = 0, skipped = 0;
                var skippedIds = new List<string>();
                string? lastErr = null;
                foreach (var eidStr in elementIds)
                {
                    Element? el;
                    try { el = doc.GetElement(AllTools.ParseEid(eidStr)); }
                    catch { skipped++; skippedIds.Add(eidStr); continue; }
                    if (el == null) { skipped++; skippedIds.Add(eidStr); continue; }
                    if (!AllTools.TrySetParameter(el, paramName, value, out var err))
                    {
                        skipped++;
                        skippedIds.Add(eidStr);
                        lastErr = err;
                        continue;
                    }
                    updated++;
                }
                if (updated == 0)
                {
                    tx.RollBack();
                    return new CommandResultDto(false, lastErr ?? $"Could not set {paramName} on any of {elementIds.Count} element(s).");
                }
                tx.Commit();
                var msg = $"Updated {updated} element(s).";
                if (skipped > 0)
                    msg += $" Skipped {skipped}: {string.Join(", ", skippedIds.Take(10))}" + (skipped > 10 ? "..." : "");
                return new CommandResultDto(true, msg, updated);
            }
            catch (Exception ex)
            {
                try { tx.RollBack(); } catch { }
                return new CommandResultDto(false, $"Failed: {ex.Message}");
            }
        }, ct);
    }

    public Task<CommandResultDto> RenameElementsAsync(
        IReadOnlyList<string> elementIds,
        IReadOnlyList<string>? newNames = null,
        string? newNamePattern = null,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            if (elementIds == null || elementIds.Count == 0)
                return new CommandResultDto(false, "Provide elementIds.");
            if (newNames != null && newNames.Count != elementIds.Count)
                return new CommandResultDto(false, "newNames length must match elementIds.");
            if (newNames == null && string.IsNullOrWhiteSpace(newNamePattern))
                return new CommandResultDto(false, "Provide newNames or newNamePattern.");

            using var tx = new Transaction(doc, $"LoadingAI for Revit: rename {elementIds.Count} elements");
            tx.Start();
            try
            {
                int updated = 0;
                for (int i = 0; i < elementIds.Count; i++)
                {
                    Element? el;
                    try { el = doc.GetElement(AllTools.ParseEid(elementIds[i])); }
                    catch { continue; }
                    if (el == null) continue;
                    var nn = newNames != null ? newNames[i] : newNamePattern!.Replace("{i}", i.ToString());
                    el.Name = nn;
                    updated++;
                }
                tx.Commit();
                return new CommandResultDto(true, $"Renamed {updated}/{elementIds.Count} element(s).", updated);
            }
            catch (Exception failEx)
            {
                try { tx.RollBack(); } catch { }
                return new CommandResultDto(false, $"Failed: {failEx.Message}");
            }
        }, ct);
    }

    public Task<CommandResultDto> ApplyConstructionDatesAsync(
        string startDate,
        int intervalDays = 14,
        string? targetParam = null,
        IReadOnlyList<string>? categories = null,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            if (!AllTools.TryParseDate(startDate ?? "", out var parsedStart))
                return new CommandResultDto(false, $"Invalid startDate '{startDate}'. Use YYYY-MM-DD or YYYYMMDD.");
            var interval = intervalDays < 1 ? 1 : (intervalDays > 365 ? 365 : intervalDays);
            var paramName = string.IsNullOrWhiteSpace(targetParam) ? "Construction Date" : targetParam;

            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");

            try
            {
                var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
                var levelById = levels.ToDictionary(l => l.Id, l => l.Name);
                var levelByName = levels.ToDictionary(l => l.Name, l => l, StringComparer.OrdinalIgnoreCase);
                var dateByLevel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < levels.Count; i++)
                    dateByLevel[levels[i].Name] = parsedStart.AddDays(i * interval).ToString("yyyy-MM-dd");

                var bics = new List<BuiltInCategory>();
                if (categories != null && categories.Count > 0)
                {
                    foreach (var c in categories)
                    {
                        var bic = AllTools.ParseCategory(c);
                        if (bic != null) bics.Add(bic.Value);
                    }
                }
                else
                {
                    string[] defaults =
                    {
                        "Walls", "Doors", "Windows", "Floors", "Ceilings", "Roofs",
                        "Structural Framing", "Structural Columns", "Columns",
                        "Generic Models", "Furniture", "Rooms"
                    };
                    foreach (var c in defaults)
                    {
                        var bic = AllTools.ParseCategory(c);
                        if (bic != null) bics.Add(bic.Value);
                    }
                }

                int updated = 0, skipped = 0, noParam = 0;
                using var tx = new Transaction(doc, $"LoadingAI for Revit: apply {paramName} bottom-up");
                tx.Start();
                try
                {
                    foreach (var bic in bics)
                    {
                        var elems = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().ToElements();
                        foreach (var el in elems)
                        {
                            string? lvlName = null;
                            if (el.LevelId != ElementId.InvalidElementId && levelById.TryGetValue(el.LevelId, out var ln))
                                lvlName = ln;
                            else
                            {
                                var p = el.LookupParameter("Base Constraint")
                                    ?? el.LookupParameter("Level")
                                    ?? el.LookupParameter("Reference Level")
                                    ?? el.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)
                                    ?? el.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)
                                    ?? el.get_Parameter(BuiltInParameter.LEVEL_PARAM);
                                if (p != null)
                                {
                                    if (p.StorageType == StorageType.ElementId)
                                    {
                                        var id = p.AsElementId();
                                        if (levelById.TryGetValue(id, out var n2)) lvlName = n2;
                                    }
                                    else lvlName = p.AsString() ?? p.AsValueString();
                                    if (lvlName != null && !levelByName.ContainsKey(lvlName)) lvlName = null;
                                }
                            }
                            if (lvlName == null || !dateByLevel.TryGetValue(lvlName, out var dateVal)) continue;
                            if (!AllTools.TrySetParameter(el, paramName, dateVal, out var err))
                            {
                                if (paramName.Equals("Construction Date", StringComparison.OrdinalIgnoreCase) && err.Contains("not found"))
                                {
                                    if (!AllTools.TrySetParameter(el, "Comments", dateVal, out _)) noParam++;
                                    else updated++;
                                    continue;
                                }
                                skipped++;
                                continue;
                            }
                            updated++;
                        }
                    }
                    tx.Commit();
                    var msg = $"Applied {paramName} bottom-up {parsedStart:yyyy-MM-dd} (+{interval}d) to {updated} element(s) across {bics.Count} categories, {levels.Count} levels.";
                    if (skipped > 0) msg += $" Skipped {skipped} (read-only/type).";
                    if (noParam > 0) msg += $" No param on {noParam} (used Comments fallback where possible).";
                    msg += "\nSchedule:\n" + string.Join("\n", levels.Select((l, i) => $"{l.Name} = {parsedStart.AddDays(i * interval):yyyy-MM-dd}"));
                    return new CommandResultDto(true, msg, updated);
                }
                catch
                {
                    try { tx.RollBack(); } catch { }
                    throw;
                }
            }
            catch (Exception failEx)
            {
                return new CommandResultDto(false, $"Failed: {failEx.Message}");
            }
        }, ct);
    }

    public Task<ParamExportDto?> ExportParamsAsync(
        string category,
        IReadOnlyList<string> columns,
        int limit = 200,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return null;
            var cat = category ?? "";
            var cols = columns == null || columns.Count == 0
                ? new[] { "Comments", "Mark" }
                : columns.ToArray();
            var lim = limit < 1 ? 1 : (limit > 1000 ? 1000 : limit);
            var bic = AllTools.ParseCategory(cat);
            if (bic == null)
                return new ParamExportDto(cat, cols, Array.Empty<ParamExportRowDto>(), $"Unknown category {cat}");

            var elems = new FilteredElementCollector(doc)
                .OfCategory(bic.Value)
                .WhereElementIsNotElementType()
                .ToElements()
                .Take(lim)
                .ToList();
            var rows = new List<ParamExportRowDto>(elems.Count);
            foreach (var el in elems)
            {
                var vals = new List<string>(cols.Length);
                foreach (var c in cols)
                {
                    var p = el.LookupParameter(c);
                    string v = "";
                    if (p != null)
                        v = p.StorageType == StorageType.String
                            ? (p.AsString() ?? "")
                            : p.AsValueString() ?? p.AsString() ?? "";
                    vals.Add(v);
                }
                rows.Add(new ParamExportRowDto(el.Id.ToString(), vals));
            }
            return new ParamExportDto(cat, cols, rows);
        }, ct);
    }

    public Task<CommandResultDto> ImportParamsAsync(
        string paramName,
        IReadOnlyList<ParamImportRowDto> rows,
        string sourcePath,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            var list = rows ?? Array.Empty<ParamImportRowDto>();
            int upd = 0, skip = 0;
            using var tx = new Transaction(doc, $"LoadingAI for Revit: import {paramName}");
            tx.Start();
            try
            {
                foreach (var row in list)
                {
                    try
                    {
                        var el = doc.GetElement(AllTools.ParseEid(row.Id));
                        if (el == null) { skip++; continue; }
                        if (!AllTools.TrySetParameter(el, paramName, row.Value, out _)) { skip++; continue; }
                        upd++;
                    }
                    catch { skip++; }
                }
                tx.Commit();
                return new CommandResultDto(true, $"Imported {upd}/{list.Count} ({skip} skipped) {paramName} from {sourcePath}", upd);
            }
            catch (Exception failEx)
            {
                try { tx.RollBack(); } catch { }
                return new CommandResultDto(false, $"Failed: {failEx.Message}");
            }
        }, ct);
    }

    public Task<CommandResultDto> CopyParamsAsync(
        string category,
        string sourceParam,
        string targetParam,
        int limit = 200,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            try
            {
                var bic = AllTools.ParseCategory(category);
                if (bic == null) return new CommandResultDto(false, $"Unknown category {category}");
                var lim = limit < 1 ? 1 : (limit > 1000 ? 1000 : limit);
                var elems = new FilteredElementCollector(doc)
                    .OfCategory(bic.Value)
                    .WhereElementIsNotElementType()
                    .ToElements()
                    .Take(lim)
                    .ToList();
                int upd = 0;
                using var tx = new Transaction(doc, $"LoadingAI for Revit: copy {sourceParam}->{targetParam}");
                tx.Start();
                try
                {
                    foreach (var el in elems)
                    {
                        var pSrc = el.LookupParameter(sourceParam);
                        if (pSrc == null) continue;
                        string v = pSrc.StorageType == StorageType.String
                            ? (pSrc.AsString() ?? "")
                            : pSrc.AsValueString() ?? "";
                        if (AllTools.TrySetParameter(el, targetParam, v, out _)) upd++;
                    }
                    tx.Commit();
                    return new CommandResultDto(true, $"Copied {upd}/{elems.Count} {sourceParam}->{targetParam} for {category}", upd);
                }
                catch
                {
                    try { tx.RollBack(); } catch { }
                    throw;
                }
            }
            catch (Exception failEx)
            {
                return new CommandResultDto(false, $"Failed: {failEx.Message}");
            }
        }, ct);
    }

    public Task<CommandResultDto> ReplaceTextInParamAsync(
        string category,
        string paramName,
        string pattern,
        string replacement,
        int limit = 200,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            try
            {
                var bic = AllTools.ParseCategory(category);
                if (bic == null) return new CommandResultDto(false, $"Unknown category {category}");
                var lim = limit < 1 ? 1 : (limit > 1000 ? 1000 : limit);
                var elems = new FilteredElementCollector(doc)
                    .OfCategory(bic.Value)
                    .WhereElementIsNotElementType()
                    .ToElements()
                    .Take(lim)
                    .ToList();
                int upd = 0;
                var re = new System.Text.RegularExpressions.Regex(pattern);
                using var tx = new Transaction(doc, $"LoadingAI for Revit: replace {paramName}");
                tx.Start();
                try
                {
                    foreach (var el in elems)
                    {
                        var p = el.LookupParameter(paramName);
                        if (p == null || p.StorageType != StorageType.String) continue;
                        var cur = p.AsString() ?? "";
                        var nxt = re.Replace(cur, replacement);
                        if (cur == nxt) continue;
                        if (AllTools.TrySetParameter(el, paramName, nxt, out _)) upd++;
                    }
                    tx.Commit();
                    return new CommandResultDto(true, $"Replaced {upd}/{elems.Count} {paramName} /{pattern}/ -> {replacement}", upd);
                }
                catch
                {
                    try { tx.RollBack(); } catch { }
                    throw;
                }
            }
            catch (Exception failEx)
            {
                return new CommandResultDto(false, $"Failed: {failEx.Message}");
            }
        }, ct);
    }

    public Task<CommandResultDto> NumberElementsAsync(
        string? category,
        string prefix,
        int start,
        int pad,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var uidoc = uiApp.ActiveUIDocument;
            if (uidoc == null) return new CommandResultDto(false, "Please open a model first.");
            var doc = uidoc.Document;
            try
            {
                ICollection<ElementId> ids;
                if (!string.IsNullOrWhiteSpace(category))
                {
                    var bic = AllTools.ParseCategory(category);
                    if (bic == null) return new CommandResultDto(false, $"Unknown category {category}");
                    ids = new FilteredElementCollector(doc).OfCategory(bic.Value).WhereElementIsNotElementType().ToElementIds();
                }
                else ids = uidoc.Selection.GetElementIds();
                if (ids.Count == 0) return new CommandResultDto(false, "Please select elements first or provide category.");
                var pref = prefix ?? "";
                int n = start;
                int upd = 0;
                using var tx = new Transaction(doc, "LoadingAI for Revit: auto numbering");
                tx.Start();
                try
                {
                    foreach (var id in ids)
                    {
                        var el = doc.GetElement(id);
                        if (el == null) continue;
                        var mark = pref + n.ToString().PadLeft(pad, '0');
                        if (AllTools.TrySetParameter(el, "Mark", mark, out _)) upd++;
                        n++;
                    }
                    tx.Commit();
                    return new CommandResultDto(true, $"Numbered {upd}/{ids.Count} Mark from {pref}{start.ToString().PadLeft(pad, '0')}", upd);
                }
                catch
                {
                    try { tx.RollBack(); } catch { }
                    throw;
                }
            }
            catch (Exception failEx)
            {
                return new CommandResultDto(false, $"Failed: {failEx.Message}");
            }
        }, ct);
    }

    public Task<CommandResultDto> DeleteElementsAsync(
        IReadOnlyList<string> elementIds, CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            if (elementIds == null || elementIds.Count == 0)
                return new CommandResultDto(false, "Provide elementIds.");

            using var tx = new Transaction(doc, $"LoadingAI for Revit: delete {elementIds.Count} elements");
            tx.Start();
            try
            {
                var ids = elementIds.Select(AllTools.ParseEid).ToList();
                doc.Delete(ids);
                tx.Commit();
                return new CommandResultDto(true, $"Deleted {ids.Count} element(s). Undo with Ctrl+Z.", ids.Count);
            }
            catch (Exception ex)
            {
                try { tx.RollBack(); } catch { }
                return new CommandResultDto(false, $"Failed: {ex.Message}");
            }
        }, ct);
    }

    public Task<CommandResultDto> MoveElementsAsync(
        IReadOnlyList<string> elementIds, double dx, double dy, double dz,
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
                var vec = new XYZ(dx, dy, dz);
                int ok = 0;
                var failed = new List<string>();
                foreach (var eidStr in elementIds)
                {
                    try
                    {
                        ElementTransformUtils.MoveElement(doc, AllTools.ParseEid(eidStr), vec);
                        ok++;
                    }
                    catch (Exception ex) { failed.Add($"{eidStr}:{ex.Message}"); }
                }
                if (ok == 0)
                {
                    tx.RollBack();
                    return new CommandResultDto(false, failed.Count > 0 ? "Failed: " + string.Join("; ", failed.Take(10)) : "Could not move any elements.");
                }
                tx.Commit();
                var msg = $"Moved {ok}/{elementIds.Count} element(s) by ({dx},{dy},{dz}) ft.";
                if (failed.Count > 0) msg += " Failed: " + string.Join("; ", failed.Take(10));
                return new CommandResultDto(true, msg, ok);
            }
            catch (Exception ex)
            {
                try { tx.RollBack(); } catch { }
                return new CommandResultDto(false, $"Failed: {ex.Message}");
            }
        }, ct);
    }

    public Task<CommandResultDto> ChangeElementTypeAsync(
        string elementId, string? typeName = null, string? typeId = null,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            try
            {
                var el = doc.GetElement(AllTools.ParseEid(elementId));
                if (el == null) return new CommandResultDto(false, $"Element {elementId} not found.");
                var valid = Element.GetValidTypes(doc, new List<ElementId> { el.Id });
                ElementType? target = null;
                foreach (var tid in valid)
                {
                    var t = doc.GetElement(tid) as ElementType;
                    if (t == null) continue;
                    if (!string.IsNullOrWhiteSpace(typeId) && t.Id == AllTools.ParseEid(typeId)) { target = t; break; }
                    if (!string.IsNullOrWhiteSpace(typeName) && t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase)) { target = t; break; }
                }
                if (target == null)
                {
                    var names = string.Join(", ", valid.Select(id => (doc.GetElement(id) as ElementType)?.Name).Where(n => n != null));
                    return new CommandResultDto(false, $"No matching type. Valid types: {names}");
                }
                using var tx = new Transaction(doc, "LoadingAI for Revit: change element type");
                tx.Start();
                try
                {
                    el.ChangeTypeId(target.Id);
                    tx.Commit();
                    return new CommandResultDto(true, $"Changed element {el.Id} type to \"{target.Name}\" ({target.Id})", 1);
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
                    foreach (var id in eids)
                        try { ElementTransformUtils.RotateElement(doc, id, axis, rad); } catch { }
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

    public Task<CommandResultDto> SaveDocumentAsync(CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");

            try
            {
                var title = doc.Title;
                var path = string.IsNullOrEmpty(doc.PathName) ? "(unsaved)" : doc.PathName;
                var ws = doc.IsWorkshared ? "Workshared" : "Not workshared";
                var central = "";
                try
                {
                    central = doc.IsWorkshared
                        ? doc.GetWorksharingCentralModelPath()?.ToString() ?? ""
                        : "";
                }
                catch { }
                if (string.IsNullOrEmpty(doc.PathName))
                    return new CommandResultDto(false, "Cannot save: document has no path (unsaved).");
                doc.Save();
                return new CommandResultDto(true, $"Saved {title} to {path} ({ws}) central: {central}");
            }
            catch (Exception ex)
            {
                return new CommandResultDto(false, $"Failed: {ex.Message}");
            }
        }, ct);
    }

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
                using var tx = new Transaction(doc, "LoadingAI for Revit: reload links");
                tx.Start();
                try
                {
                    int ok = 0;
                    foreach (var lt in links)
                    {
                        try { lt.Reload(); ok++; } catch { }
                    }
                    tx.Commit();
                    return new CommandResultDto(true, $"Reloaded {ok}/{links.Count} RVT links", ok);
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

    public Task<CommandResultDto> AssignWorksetAsync(
        IReadOnlyList<string> elementIds,
        string worksetName,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            if (!doc.IsWorkshared) return new CommandResultDto(false, "Document is not workshared.");
            try
            {
                var w = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset)
                    .ToWorksets()
                    .FirstOrDefault(x => x.Name.Equals(worksetName, StringComparison.OrdinalIgnoreCase));
                if (w == null) return new CommandResultDto(false, $"Workset {worksetName} not found.");

                using var tx = new Transaction(doc, $"LoadingAI for Revit: workset {worksetName}");
                tx.Start();
                try
                {
                    int upd = 0;
                    foreach (var idStr in elementIds)
                    {
                        var el = doc.GetElement(AllTools.ParseEid(idStr));
                        if (el == null) continue;
                        var param = el.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                        if (param != null && !param.IsReadOnly)
                        {
                            param.Set(w.Id.IntegerValue);
                            upd++;
                        }
                    }
                    tx.Commit();
                    return new CommandResultDto(true, $"Assigned {upd}/{elementIds.Count} to workset \"{worksetName}\"", upd);
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

    public Task<CommandResultDto> LoadFamilyAsync(string path, CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            try
            {
                using var tx = new Transaction(doc, "LoadingAI for Revit: load family");
                tx.Start();
                try
                {
                    if (!doc.LoadFamily(path, out var family))
                    {
                        tx.RollBack();
                        return new CommandResultDto(false, $"LoadFamily returned false for {path}");
                    }
                    tx.Commit();
                    var symbols = family!.GetFamilySymbolIds()
                        .Select(id => doc.GetElement(id) is FamilySymbol s ? s.Name : id.ToString())
                        .ToList();
                    return new CommandResultDto(
                        true,
                        $"Loaded family \"{family.Name}\" ({family.Id}) with {symbols.Count} symbol(s): {string.Join(", ", symbols)}",
                        1);
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

    public Task<CommandResultDto> ExportPdfAsync(
        string directory,
        IReadOnlyList<string>? sheetIds = null,
        string? fileName = null,
        bool combine = true,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            try
            {
                var views = ResolvePrintable(doc, sheetIds, preferSheets: true);
                if (views.Count == 0)
                    return new CommandResultDto(false, "No printable sheets/views to export.");
                var options = new PDFExportOptions
                {
                    FileName = ExportPaths.SanitizeFileName(fileName, doc.Title),
                    Combine = combine
                };
                if (!doc.Export(directory, views, options))
                    return new CommandResultDto(false, $"PDF export failed ({views.Count} views) to {directory}.");
                var dest = combine
                    ? System.IO.Path.Combine(directory, options.FileName + ".pdf")
                    : directory;
                return new CommandResultDto(
                    true, $"Exported {views.Count} sheet(s)/view(s) to PDF: {dest}", views.Count);
            }
            catch (Exception ex)
            {
                return new CommandResultDto(false, $"Failed: {ex.Message}");
            }
        }, ExportTimeout, ct);
    }

    public Task<CommandResultDto> ExportDwgAsync(
        string directory,
        IReadOnlyList<string>? viewIds = null,
        string? fileName = null,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            try
            {
                var views = ResolvePrintable(doc, viewIds, preferSheets: false);
                if (views.Count == 0)
                    return new CommandResultDto(false, "No printable views to export.");
                var fname = ExportPaths.SanitizeFileName(fileName, doc.Title);
                var options = new DWGExportOptions { FileVersion = ACADVersion.R2018 };
                if (!doc.Export(directory, fname, views, options))
                    return new CommandResultDto(false, $"DWG export failed ({views.Count} views) to {directory}.");
                return new CommandResultDto(
                    true,
                    $"Exported {views.Count} view(s) to DWG in {directory} (prefix \"{fname}\")",
                    views.Count);
            }
            catch (Exception ex)
            {
                return new CommandResultDto(false, $"Failed: {ex.Message}");
            }
        }, ExportTimeout, ct);
    }

    public Task<CommandResultDto> ExportIfcAsync(
        string directory,
        string? fileName = null,
        string? version = null,
        CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return new CommandResultDto(false, "Please open a model first.");
            try
            {
                var fname = ExportPaths.SanitizeFileName(fileName, doc.Title);
                var options = new IFCExportOptions();
                if (!string.IsNullOrWhiteSpace(version)
                    && Enum.TryParse<IFCVersion>(version, true, out var ver))
                    options.FileVersion = ver;
                var path = System.IO.Path.Combine(directory, fname + ".ifc");
                DebugLog.Write("export_ifc.start", new
                {
                    dir = directory,
                    fname,
                    version,
                    title = doc.Title,
                    inTransaction = doc.IsModifiable
                });
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var ok = doc.Export(directory, fname, options);
                sw.Stop();
                DebugLog.Write("export_ifc.end", new { ok, path, elapsedMs = sw.ElapsedMilliseconds });
                if (!ok) return new CommandResultDto(false, $"IFC export failed to {path}.");
                return new CommandResultDto(true, $"Exported IFC: {path}", 1);
            }
            catch (Exception ex)
            {
                return new CommandResultDto(false, $"Failed: {ex.Message}");
            }
        }, ExportTimeout, ct);
    }

    public Task<PurgeUnusedResultDto> PurgeUnusedAsync(bool preview = true, CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null)
                return new PurgeUnusedResultDto(preview, 0, 0, 0, Array.Empty<string>(), "Please open a model first.");
            try
            {
                var unused = doc.GetUnusedElements(new HashSet<ElementId>());
                if (unused == null || unused.Count == 0)
                    return new PurgeUnusedResultDto(preview, 0, 0, 0, Array.Empty<string>());
                var count = unused.Count;

                if (preview)
                {
                    var sample = unused.Take(15).Select(id =>
                    {
                        var el = doc.GetElement(id);
                        if (el == null) return id.ToString();
                        return $"{el.Category?.Name ?? "(no category)"}/{el.Name} ({id})";
                    }).ToList();
                    return new PurgeUnusedResultDto(true, count, 0, 0, sample);
                }

                int deleted = 0, passes = 0;
                using var tx = new Transaction(doc, "LoadingAI for Revit: purge unused");
                tx.Start();
                try
                {
                    for (var i = 0; i < 8; i++)
                    {
                        var batch = doc.GetUnusedElements(new HashSet<ElementId>());
                        if (batch == null || batch.Count == 0) break;
                        doc.Delete(batch);
                        deleted += batch.Count;
                        passes++;
                    }
                    tx.Commit();
                }
                catch (Exception inner)
                {
                    try { tx.RollBack(); } catch { }
                    return new PurgeUnusedResultDto(preview, count, 0, passes, Array.Empty<string>(), $"Failed: {inner.Message}");
                }
                return new PurgeUnusedResultDto(false, count, deleted, passes, Array.Empty<string>());
            }
            catch (Exception ex)
            {
                return new PurgeUnusedResultDto(preview, 0, 0, 0, Array.Empty<string>(), $"Failed: {ex.Message}");
            }
        }, ct);
    }

    public Task<ScheduleDataDto?> GetScheduleDataAsync(
        string? scheduleId, string? scheduleName, int rowLimit, CancellationToken ct = default)
    {
        // The row window is bounded here rather than by the caller so the cap holds for every slice consumer.
        if (rowLimit <= 0) rowLimit = DefaultScheduleRows;
        if (rowLimit > MaxScheduleRows) rowLimit = MaxScheduleRows;
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return null;
            try
            {
                var schedule = FindSchedule(doc, scheduleId, scheduleName);
                if (schedule == null)
                    return new ScheduleDataDto(string.Empty, string.Empty, 0, 0, 0,
                        Array.Empty<IReadOnlyList<string>>(), "Schedule not found. Pass scheduleId or name.");
                var body = schedule.GetTableData().GetSectionData(SectionType.Body);
                var rows = Math.Min(body.NumberOfRows, rowLimit);
                var cols = body.NumberOfColumns;
                var grid = new List<IReadOnlyList<string>>(rows);
                for (var r = 0; r < rows; r++)
                {
                    var cells = new List<string>(cols);
                    for (var c = 0; c < cols; c++)
                        cells.Add(schedule.GetCellText(SectionType.Body, r, c).Replace('\t', ' '));
                    grid.Add(cells);
                }
                return new ScheduleDataDto(schedule.Id.ToString(), schedule.Name, body.NumberOfRows, cols, rows, grid);
            }
            catch (Exception ex)
            {
                return new ScheduleDataDto(string.Empty, string.Empty, 0, 0, 0,
                    Array.Empty<IReadOnlyList<string>>(), $"Failed: {ex.Message}");
            }
        }, ct);
    }

    public Task<ViewRefDto?> CreateScheduleAsync(
        string name, IReadOnlyList<string> fields, CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return null;
            try
            {
                // The one token carries both frozen tool arguments: "<category>" when the caller
                // wants the conventional name, or "<category>:<schedule name>" when an explicit
                // schedule name was asked for.
                var token = name ?? string.Empty;
                var colon = token.IndexOf(':');
                var categoryToken = colon >= 0 ? token.Substring(0, colon) : token;
                var explicitName = colon >= 0 ? token.Substring(colon + 1).Trim() : null;
                var bic = ResolveScheduleCategory(categoryToken);
                if (bic == null)
                    return new ViewRefDto(string.Empty, string.Empty, string.Empty, $"Unknown category {categoryToken.Trim()}");
                var scheduleName = string.IsNullOrWhiteSpace(explicitName)
                    ? $"{categoryToken.Trim()} Schedule"
                    : explicitName!;
                using var tx = new Transaction(doc, "LoadingAI for Revit: create schedule");
                tx.Start();
                try
                {
                    var schedule = ViewSchedule.CreateSchedule(doc, new ElementId(bic.Value), ElementId.InvalidElementId);
                    schedule.Name = scheduleName;
                    var added = AddScheduleFields(schedule, fields);
                    tx.Commit();
                    return new ViewRefDto(schedule.Id.ToString(), schedule.Name,
                        $"Created schedule {schedule.Id} \"{schedule.Name}\" with {added} field(s).");
                }
                catch (Exception inner)
                {
                    try { tx.RollBack(); } catch { }
                    return new ViewRefDto(string.Empty, string.Empty, string.Empty, $"Failed: {inner.Message}");
                }
            }
            catch (Exception ex)
            {
                return new ViewRefDto(string.Empty, string.Empty, string.Empty, $"Failed: {ex.Message}");
            }
        }, ct);
    }

    public Task<ViewRefDto?> CreateFloorPlanAsync(
        string levelIdOrName, string name, CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return null;
            try
            {
                var typeId = FindViewFamily(doc, ViewFamily.FloorPlan);
                if (typeId == null) return new ViewRefDto(string.Empty, string.Empty, string.Empty, "No FloorPlan view type found.");
                var level = ResolveLevelToken(doc, levelIdOrName);
                if (level == null) return new ViewRefDto(string.Empty, string.Empty, string.Empty, "Level not found. Pass levelId or levelName.");
                using var tx = new Transaction(doc, "LoadingAI for Revit: create floor plan");
                tx.Start();
                try
                {
                    var view = ViewPlan.Create(doc, typeId, level.Id);
                    if (!string.IsNullOrWhiteSpace(name)) view.Name = name.Trim();
                    tx.Commit();
                    return new ViewRefDto(view.Id.ToString(), view.Name,
                        $"Created floor plan {view.Id} \"{view.Name}\" on {level.Name}");
                }
                catch (Exception inner)
                {
                    try { tx.RollBack(); } catch { }
                    return new ViewRefDto(string.Empty, string.Empty, string.Empty, $"Failed: {inner.Message}");
                }
            }
            catch (Exception ex)
            {
                return new ViewRefDto(string.Empty, string.Empty, string.Empty, $"Failed: {ex.Message}");
            }
        }, ct);
    }

    public Task<ViewRefDto?> Create3DViewAsync(
        string name, bool isometric, CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return null;
            try
            {
                var typeId = FindViewFamily(doc, ViewFamily.ThreeDimensional);
                if (typeId == null) return new ViewRefDto(string.Empty, string.Empty, string.Empty, "No 3D view type found.");
                using var tx = new Transaction(doc, "LoadingAI for Revit: create 3D view");
                tx.Start();
                try
                {
                    var view = isometric ? View3D.CreateIsometric(doc, typeId) : View3D.CreatePerspective(doc, typeId);
                    if (!string.IsNullOrWhiteSpace(name)) view.Name = name.Trim();
                    tx.Commit();
                    return new ViewRefDto(view.Id.ToString(), view.Name,
                        $"Created 3D view {view.Id} \"{view.Name}\"");
                }
                catch (Exception inner)
                {
                    try { tx.RollBack(); } catch { }
                    return new ViewRefDto(string.Empty, string.Empty, string.Empty, $"Failed: {inner.Message}");
                }
            }
            catch (Exception ex)
            {
                return new ViewRefDto(string.Empty, string.Empty, string.Empty, $"Failed: {ex.Message}");
            }
        }, ct);
    }

    public Task<ViewRefDto?> CreateSectionViewAsync(
        string name, PointMmDto min, PointMmDto max, string? viewDirection, CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return null;
            try
            {
                var a = new XYZ(Metric.MmToFt(min.X), Metric.MmToFt(min.Y), Metric.MmToFt(min.Z));
                var b = new XYZ(Metric.MmToFt(max.X), Metric.MmToFt(max.Y), Metric.MmToFt(max.Z));
                var typeId = FindViewFamily(doc, ViewFamily.Section);
                if (typeId == null) return new ViewRefDto(string.Empty, string.Empty, string.Empty, "No Section view type found.");
                var box = SectionBox(a, b, SectionHeight, SectionDepth, viewDirection);
                using var tx = new Transaction(doc, "LoadingAI for Revit: create section");
                tx.Start();
                try
                {
                    var view = ViewSection.CreateSection(doc, typeId, box);
                    if (!string.IsNullOrWhiteSpace(name)) view.Name = name.Trim();
                    tx.Commit();
                    return new ViewRefDto(view.Id.ToString(), view.Name,
                        $"Created section {view.Id} \"{view.Name}\"");
                }
                catch (Exception inner)
                {
                    try { tx.RollBack(); } catch { }
                    return new ViewRefDto(string.Empty, string.Empty, string.Empty, $"Failed: {inner.Message}");
                }
            }
            catch (Exception ex)
            {
                return new ViewRefDto(string.Empty, string.Empty, string.Empty, $"Failed: {ex.Message}");
            }
        }, ct);
    }

    public Task<PathOfTravelResultDto?> CreatePathOfTravelAsync(
        string viewIdOrName, IReadOnlyList<PathPairDto> pairs, CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return null;
            var wanted = pairs?.Count ?? 0;
            PathOfTravelResultDto Fail(string message) =>
                new PathOfTravelResultDto(string.Empty, wanted, 0, Array.Empty<string>(), Array.Empty<string>(), message);
            try
            {
                var view = ResolveViewPlan(doc, viewIdOrName);
                if (view == null)
                    return Fail("path_of_travel requires a floor plan view. Pass viewId of a ViewPlan.");
                if (pairs == null || pairs.Count == 0)
                    return Fail("Pass startX/startY/endX/endY or starts/ends arrays.");
                // The pair cap is enforced here rather than by the caller so it holds for every slice consumer.
                if (pairs.Count > MaxPathOfTravelPairs)
                    return Fail($"Max {MaxPathOfTravelPairs} path pairs per call.");

                var starts = new List<XYZ>(pairs.Count);
                var ends = new List<XYZ>(pairs.Count);
                foreach (var pair in pairs)
                {
                    starts.Add(new XYZ(Metric.MmToFt(pair.Start.X), Metric.MmToFt(pair.Start.Y), Metric.MmToFt(pair.Start.Z)));
                    ends.Add(new XYZ(Metric.MmToFt(pair.End.X), Metric.MmToFt(pair.End.Y), Metric.MmToFt(pair.End.Z)));
                }

                using var tx = new Transaction(doc, "LoadingAI for Revit: path of travel");
                tx.Start();
                try
                {
                    var paths = PathOfTravel.CreateMultiple(view, starts, ends, out IList<PathOfTravelCalculationStatus> statuses);
                    tx.Commit();
                    // CreateMultiple returns null — and can carry null slots — when no path was found
                    // (points too close, crop box exclusion, no route). Walk the request, not the
                    // result, so every unsuccessful pair is reported instead of throwing.
                    var ids = new List<string>();
                    var failures = new List<string>();
                    for (var i = 0; i < starts.Count; i++)
                    {
                        var path = paths != null && i < paths.Count ? paths[i] : null;
                        if (path != null)
                        {
                            ids.Add(path.Id.ToString());
                            continue;
                        }
                        failures.Add(statuses != null && i < statuses.Count
                            ? $"#{i}:{statuses[i]}"
                            : $"#{i}:No path found");
                    }
                    return new PathOfTravelResultDto(view.Name, starts.Count, ids.Count, ids, failures);
                }
                catch (Exception inner)
                {
                    try { tx.RollBack(); } catch { }
                    return Fail($"Failed: {inner.Message}");
                }
            }
            catch (Exception ex)
            {
                return Fail($"Failed: {ex.Message}");
            }
        }, ct);
    }

    /// <summary>
    /// One page of host elements for the structure-cuts algorithms. The page is capped
    /// Adapter-side so the bound holds for every caller, and <c>Total</c> is the size of
    /// the whole collection so a caller can ask for the next page without re-collecting.
    /// </summary>
    public Task<ElementIdListDto?> CollectHostsAsync(
        string scope, IReadOnlyList<string>? categories, int limit, int offset, CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (uidoc == null || doc == null)
                return new ElementIdListDto(Array.Empty<string>(), offset, 0, "Please open a model first.");

            var cats = categories ?? Array.Empty<string>();
            var cap = Math.Max(1, limit);
            var start = Math.Max(0, offset);
            var wanted = SplitElements.HostsIn(cats);
            var collected = SplitScope.ByScope(doc, uidoc, wanted, scope);

            if (start > collected.Count)
                start = collected.Count;

            var ids = collected
                .Skip(start)
                .Take(cap)
                .Select(element => SplitElements.Fmt(element.Id))
                .ToList();
            return new ElementIdListDto(ids, start, collected.Count);
        }, ct);
    }

    public Task<SplitResultDto?> SplitBeamsByColumnsAsync(SplitRequestDto request, CancellationToken ct = default)
        => RunSplit(request, ct, (uiApp, options, outcome) => BeamSplit.Run(
            uiApp,
            request.HostIds ?? Array.Empty<string>(),
            request.Cutters ?? Array.Empty<string>(),
            options,
            outcome));

    public Task<SplitResultDto?> SplitSecondaryBeamsByMainBeamsAsync(SplitRequestDto request, CancellationToken ct = default)
        => RunSplit(request, ct, (uiApp, options, outcome) => BeamCut.Run(
            uiApp,
            request.HostIds ?? Array.Empty<string>(),
            request.Cutters ?? Array.Empty<string>(),
            options,
            outcome));

    public Task<SplitResultDto?> SplitFloorsByBeamsAsync(SplitRequestDto request, CancellationToken ct = default)
        => RunSplit(request, ct, (uiApp, options, outcome) =>
        {
            // The floor cut takes beams and columns as two lists while the DTO carries one
            // Cutters list. SplitElements.Resolve filters by category, so handing the same
            // list to both parameters separates them without inventing an encoding.
            var cutters = request.Cutters ?? Array.Empty<string>();
            return FloorSplit.Run(
                uiApp,
                request.HostIds ?? Array.Empty<string>(),
                cutters,
                cutters,
                options,
                outcome);
        });

    public Task<SplitResultDto?> SplitByLevelsAsync(SplitRequestDto request, CancellationToken ct = default)
        => RunSplit(request, ct, (uiApp, options, outcome) => LevelSplit.Run(
            uiApp,
            request.HostIds ?? Array.Empty<string>(),
            request.Cutters ?? Array.Empty<string>(),
            request.Categories ?? Array.Empty<string>(),
            options,
            outcome));

    public Task<SplitResultDto?> SplitByGridsAsync(SplitRequestDto request, CancellationToken ct = default)
        => RunSplit(request, ct, (uiApp, options, outcome) => GridSplit.Run(
            uiApp,
            request.HostIds ?? Array.Empty<string>(),
            request.Cutters ?? Array.Empty<string>(),
            request.Categories ?? Array.Empty<string>(),
            options,
            outcome));

    /// <summary>
    /// Shared shape of the four cuts: one transaction per call, the algorithms' own prose in
    /// <c>Report</c>, and the counters captured out of band so a partial run stays legible.
    /// </summary>
    private Task<SplitResultDto?> RunSplit(
        SplitRequestDto request,
        CancellationToken ct,
        Func<UIApplication, SplitOptions, SplitOutcome, string> run)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null)
                return new SplitResultDto("Please open a model first.", 0, 0, 0, Array.Empty<string>());

            var options = new SplitOptions
            {
                Limit = request.Limit,
                DryRun = request.DryRun,
                UniqueMark = request.UniqueMark,
                CopyParams = request.CopyParams,
                Scope = request.Scope,
                ContinueFrom = request.ContinueFrom,
            };
            var outcome = new SplitOutcome();
            try
            {
                var report = run(uiApp, options, outcome);
                return new SplitResultDto(report, outcome.Created, outcome.Openings, outcome.Skipped, outcome.NewIds);
            }
            catch (Exception ex)
            {
                return new SplitResultDto($"Failed: {ex.Message}", 0, 0, 0, Array.Empty<string>());
            }
        }, ct);
    }

    public Task<FormworkSetupResultDto?> FormworkSetupAsync(FormworkSetupRequestDto request, CancellationToken ct = default)
        => RunFormwork(uiApp => FormworkSetup.Run(uiApp), r => new FormworkSetupResultDto(r), null, ct);

    public Task<FormworkPanelsResultDto?> CreateFormworkPanelsAsync(FormworkPanelsRequestDto request, CancellationToken ct = default)
        => RunFormwork(
            uiApp => FormworkPanels.Run(
                uiApp,
                request.Ids ?? Array.Empty<string>(),
                request.ThicknessMm,
                request.FaceTypes ?? Array.Empty<string>(),
                request.Collision,
                request.DeleteExisting,
                request.IncludeUnselectedLinks),
            r => new FormworkPanelsResultDto(r),
            FormworkPanels.GenerateTimeout,
            ct);

    public Task<CleanupResultDto?> CleanupFormworkAsync(CleanupRequestDto request, CancellationToken ct = default)
        => RunFormwork(
            uiApp => FormworkCleanup.Run(uiApp, request.Action, request.FaceType),
            r => new CleanupResultDto(r),
            null,
            ct);

    public Task<CopyParamsResultDto?> CopyFormworkParentParamsAsync(CopyParamsRequestDto request, CancellationToken ct = default)
        => RunFormwork(
            uiApp => FormworkParentParams.Run(
                uiApp,
                request.Ids ?? Array.Empty<string>(),
                request.Names ?? Array.Empty<string>()),
            r => new CopyParamsResultDto(r),
            null,
            ct);

    public Task<OpeningPlanResultDto?> OpeningProbeClashAsync(OpeningClashRequestDto request, CancellationToken ct = default)
        => RunOpening(
            uiApp =>
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                var uidoc = uiApp.ActiveUIDocument;
                if (doc == null || uidoc == null)
                    return new OpeningPlanResultDto("Please open a model first.", 0, 0, 0, Array.Empty<OpeningPlanHitDto>());
                try
                {
                    return PlaceOpenings.Plan(
                        doc, uidoc,
                        request.MepIds, request.HostIds, request.Shape, request.ClearanceMm,
                        request.LevelName, request.Limit);
                }
                catch (Exception ex)
                {
                    return new OpeningPlanResultDto($"Failed: {ex.Message}", 0, 0, 0, Array.Empty<OpeningPlanHitDto>(), ex.Message);
                }
            },
            PlaceOpenings.ClashTimeout,
            ct);

    public Task<OpeningPlaceResultDto?> PlaceOpeningsAsync(OpeningClashRequestDto request, CancellationToken ct = default)
        => RunOpening(
            uiApp =>
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                var uidoc = uiApp.ActiveUIDocument;
                if (doc == null || uidoc == null)
                    return new OpeningPlaceResultDto("Please open a model first.", 0, 0, 0, 0, 0, Array.Empty<string>(), Array.Empty<string>());
                if (request.DryRun)
                {
                    var plan = PlaceOpenings.Plan(
                        doc, uidoc, request.MepIds, request.HostIds, request.Shape,
                        request.ClearanceMm, request.LevelName, request.Limit);
                    return new OpeningPlaceResultDto(
                        plan.Report, 0, 0, 0, 0, 0, Array.Empty<string>(), Array.Empty<string>(), plan.Error);
                }
                try
                {
                    var report = PlaceOpenings.Run(
                        doc, uidoc, request.MepIds, request.HostIds, request.Shape,
                        request.ClearanceMm, request.FamilyName, request.LevelName, request.Limit);
                    return ProjectPlace(report);
                }
                catch (Exception ex)
                {
                    return new OpeningPlaceResultDto($"Failed: {ex.Message}", 0, 0, 0, 0, 0, Array.Empty<string>(), Array.Empty<string>(), ex.Message);
                }
            },
            PlaceOpenings.ClashTimeout,
            ct);

    public Task<OpeningCutResultDto?> CutHostsByOpeningsAsync(OpeningCutRequestDto request, CancellationToken ct = default)
        => RunOpening(
            uiApp =>
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                var uidoc = uiApp.ActiveUIDocument;
                if (doc == null || uidoc == null)
                    return new OpeningCutResultDto("Please open a model first.", 0, 0, 0, 0);
                try
                {
                    var report = CutLocal.Run(doc, uidoc, request.OpeningIds, request.HostIds);
                    return new OpeningCutResultDto(report, 0, 0, 0, 0);
                }
                catch (Exception ex)
                {
                    return new OpeningCutResultDto($"Failed: {ex.Message}", 0, 0, 0, 0, ex.Message);
                }
            },
            null,
            ct);

    static OpeningPlaceResultDto ProjectPlace(string report)
    {
        var ids = new List<string>();
        var points = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
            report, @"(\d+)\(([-\d.]+),\s*([-\d.]+),\s*([-\d.]+) ft\)"))
        {
            ids.Add(m.Groups[1].Value);
            points.Add($"{m.Groups[2].Value},{m.Groups[3].Value},{m.Groups[4].Value}");
        }
        return new OpeningPlaceResultDto(report, ids.Count, ids.Count, 0, 0, 0, ids, points);
    }

    private async Task<TDto?> RunOpening<TDto>(
        Func<UIApplication, TDto> run,
        TimeSpan? timeout,
        CancellationToken ct) where TDto : class
    {
        var dto = timeout is { } budget
            ? await _bridge.RunAsync(run, budget, ct)
            : await _bridge.RunAsync(run, ct);
        return dto;
    }

    /// <summary>
    /// Shared shape of the four formwork tools. The host entry points already return their own
    /// prose and absorb their own exceptions, so this only picks the timeout and lifts the
    /// string into the slice's result DTO. <c>null</c> means the bridge was cancelled before
    /// it ran, which the slices surface as <c>null</c> rather than a made-up report.
    /// </summary>
    private async Task<TDto?> RunFormwork<TDto>(
        Func<UIApplication, string> run,
        Func<string, TDto> project,
        TimeSpan? timeout,
        CancellationToken ct)
    {
        var report = timeout is { } budget
            ? await _bridge.RunAsync(run, budget, ct)
            : await _bridge.RunAsync(run, ct);
        return report == null ? default : project(report);
    }

    /// <summary>Body rows shown when a caller passes no usable <c>rowLimit</c>.</summary>
    private const int DefaultScheduleRows = 25;

    /// <summary>Hard ceiling on schedule body rows returned in one call.</summary>
    private const int MaxScheduleRows = 40;

    /// <summary>Hard ceiling on path-of-travel pairs accepted in one call.</summary>
    private const int MaxPathOfTravelPairs = 5;

    /// <summary>Section box height used when a caller does not describe one (3000 mm).</summary>
    private static readonly double SectionHeight = Metric.MmToFt(3000);

    /// <summary>Section box depth used when a caller does not describe one (2500 mm).</summary>
    private static readonly double SectionDepth = Metric.MmToFt(2500);

    private static int AddScheduleFields(ViewSchedule schedule, IReadOnlyList<string>? wanted)
    {
        var focus = wanted ?? Array.Empty<string>();
        var added = 0;
        foreach (var sf in schedule.Definition.GetSchedulableFields())
        {
            if (sf.FieldType != ScheduleFieldType.Instance && sf.FieldType != ScheduleFieldType.ElementType)
                continue;
            if (sf.ParameterId.Value == (long)BuiltInParameter.ALL_MODEL_MARK)
                continue;
            var fname = sf.GetName(schedule.Document);
            if (focus.Count > 0)
            {
                if (!focus.Any(w => fname.Equals(w, StringComparison.OrdinalIgnoreCase)))
                    continue;
            }
            else if (added >= 8) break;
            schedule.Definition.AddField(sf);
            added++;
        }
        return added;
    }

    private static ViewSchedule? FindSchedule(Document doc, string? scheduleId, string? scheduleName)
    {
        if (!string.IsNullOrWhiteSpace(scheduleId))
            return TryGetElement(doc, scheduleId) as ViewSchedule;
        if (string.IsNullOrWhiteSpace(scheduleName)) return null;
        return new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
            .FirstOrDefault(s => s.Name.Equals(scheduleName!.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Resolves a level from an element id or a level name; blank picks the lowest level.</summary>
    private static Level? ResolveLevelToken(Document doc, string? idOrName)
    {
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>();
        if (string.IsNullOrWhiteSpace(idOrName)) return levels.OrderBy(l => l.Elevation).FirstOrDefault();
        var token = idOrName!.Trim();
        if (TryGetElement(doc, token) is Level byId) return byId;
        return levels.FirstOrDefault(l => l.Name.Equals(token, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Resolves a floor plan from a view id or name; blank uses the active view.</summary>
    private static ViewPlan? ResolveViewPlan(Document doc, string? idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName)) return doc.ActiveView as ViewPlan;
        var token = idOrName!.Trim();
        if (TryGetElement(doc, token) is ViewPlan byId) return byId;
        return new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
            .FirstOrDefault(v => v.Name.Equals(token, StringComparison.OrdinalIgnoreCase));
    }

    private static Element? TryGetElement(Document doc, string? idStr)
    {
        if (string.IsNullOrWhiteSpace(idStr)) return null;
        try { return doc.GetElement(AllTools.ParseEid(idStr)); }
        catch { return null; }
    }

    private static ElementId? FindViewFamily(Document doc, ViewFamily family)
        => new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
            .FirstOrDefault(t => t.ViewFamily == family)?.Id;

    /// <summary>
    /// Reads the category out of a create_schedule token: the whole token first, then its
    /// leading words, so "Door List" still resolves to the Door category.
    /// </summary>
    private static BuiltInCategory? ResolveScheduleCategory(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var words = token!.Trim().Split(' ');
        for (var n = words.Length; n >= 1; n--)
        {
            var candidate = AllTools.ParseCategory(string.Join(" ", words.Take(n)));
            if (candidate != null) return candidate;
        }
        return null;
    }

    /// <summary>Axis token (x / -y / z) for a section view direction; null when absent or unrecognised.</summary>
    private static XYZ? ParseViewDirection(string? direction)
    {
        if (string.IsNullOrWhiteSpace(direction)) return null;
        var token = direction!.Trim().ToLowerInvariant();
        var negative = token.StartsWith("-", StringComparison.Ordinal);
        if (negative) token = token.Substring(1).Trim();
        if (token.StartsWith("+", StringComparison.Ordinal)) token = token.Substring(1).Trim();
        XYZ axis;
        switch (token)
        {
            case "x": case "east": case "right": axis = XYZ.BasisX; break;
            case "y": case "north": case "up": axis = XYZ.BasisY; break;
            case "z": axis = XYZ.BasisZ; break;
            default: return null;
        }
        return negative ? axis.Negate() : axis;
    }

    private static BoundingBoxXYZ SectionBox(XYZ a, XYZ b, double height, double depth, string? viewDirection)
    {
        var along = b - a;
        if (along.GetLength() < 1e-6) throw new InvalidOperationException("Section line is too short.");
        var viewDir = ParseViewDirection(viewDirection) ?? along.Normalize();
        var up = XYZ.BasisZ;
        var right = up.CrossProduct(viewDir);
        if (right.GetLength() < 1e-6)
        {
            up = XYZ.BasisY;
            right = up.CrossProduct(viewDir);
        }
        right = right.Normalize();
        up = viewDir.CrossProduct(right).Normalize();
        var t = Transform.Identity;
        t.Origin = (a + b) / 2;
        t.set_Basis(0, right);
        t.set_Basis(1, up);
        t.set_Basis(2, viewDir);
        var halfW = Math.Max(along.GetLength() / 2, 1);
        var halfH = Math.Max(height / 2, 1);
        var halfD = Math.Max(depth / 2, 0.5);
        return new BoundingBoxXYZ
        {
            Enabled = true,
            Min = new XYZ(-halfW, -halfH, -halfD),
            Max = new XYZ(halfW, halfH, halfD),
            Transform = t
        };
    }

    private static List<ElementId> ResolvePrintable(Document doc, IReadOnlyList<string>? ids, bool preferSheets)
    {
        var result = new List<ElementId>();
        if (ids != null && ids.Count > 0)
        {
            foreach (var s in ids)
            {
                try
                {
                    if (doc.GetElement(AllTools.ParseEid(s)) is View view && IsPrintable(view))
                        result.Add(view.Id);
                }
                catch { }
            }
            return result;
        }

        if (preferSheets)
        {
            result.AddRange(new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder && IsPrintable(s))
                .Select(s => s.Id));
            if (result.Count > 0) return result;
        }

        var active = doc.ActiveView;
        if (active != null && IsPrintable(active)) result.Add(active.Id);
        return result;
    }

    private static bool IsPrintable(View view) => !view.IsTemplate && view.CanBePrinted;

    private static Level? ResolveLevel(Document doc, string? levelId, string? levelName)
    {
        if (!string.IsNullOrWhiteSpace(levelId))
            return doc.GetElement(AllTools.ParseEid(levelId)) as Level;
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>();
        if (!string.IsNullOrWhiteSpace(levelName))
            return levels.FirstOrDefault(l => l.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase));
        return levels.OrderBy(l => l.Elevation).FirstOrDefault();
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
            _ when s.StartsWith("#") && s.Length == 7
                   && int.TryParse(s.Substring(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
                   && int.TryParse(s.Substring(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
                   && int.TryParse(s.Substring(5, 2), System.Globalization.NumberStyles.HexNumber, null, out var b)
                => new Color((byte)r, (byte)g, (byte)b),
            _ => new Color(255, 0, 0)
        };
    }

    private static string ReadParam(Element el, string paramName)
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
        string[] hexRainbow =
        {
            "#1565C0", "#1E88E5", "#039BE5", "#00ACC1", "#00897B", "#43A047", "#7CB342", "#FBC02D", "#FFA000", "#FB8C00",
            "#F4511E", "#E53935", "#D32F2F", "#C2185B", "#7B1FA2", "#512DA8", "#303F9F", "#455A64", "#FF6F00", "#FF4081"
        };
        var list = new List<Color>();
        for (int i = 0; i < n; i++)
        {
            string hex = palette == "heat"
                ? InterpolateHeat((double)i / Math.Max(1, n - 1))
                : hexRainbow[i % hexRainbow.Length];
            list.Add(HexToColor(hex));
        }
        return list;
    }

    private static string InterpolateHeat(double t)
    {
        if (t < 0.5)
        {
            int r = (int)(255 * t * 2);
            int g = (int)(255 * t * 2);
            int b = 255;
            return $"#{r:X2}{g:X2}{b:X2}";
        }
        int rr = 255;
        int gg = (int)(255 * (1 - t) * 2);
        int bb = (int)(255 * (1 - t) * 2);
        return $"#{rr:X2}{gg:X2}{bb:X2}";
    }

    private static Color HexToColor(string hex)
    {
        hex = hex.Trim().TrimStart('#');
        if (hex.Length == 6
            && int.TryParse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
            && int.TryParse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            && int.TryParse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            return new Color((byte)r, (byte)g, (byte)b);
        return new Color(255, 0, 0);
    }
}
