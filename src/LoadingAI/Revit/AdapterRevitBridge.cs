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
/// App-side implementation of the revit-free <see cref="IRevitBridge"/> contract,
/// wrapping the low-level <see cref="RevitBridge"/>. All Autodesk.Revit types
/// stay in this adapter; callers see only the Core DTOs.
/// </summary>
// Implementation is split along the IRevitBridge seams so no single file owns
// every concern. This file holds the query slice and the shared state; the command
// slices live in AdapterRevitBridge.{Views,Params,Elements,Create,Documents}.cs and
// the cross-slice workflows plus private helpers in AdapterRevitBridge.Workflows.cs.
public sealed partial class AdapterRevitBridge : IRevitBridge
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
}
