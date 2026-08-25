using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
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

    public AdapterRevitBridge(RevitBridge bridge) => _bridge = bridge;

    public bool HasActiveDocument => _bridge.RunAsync(uiApp =>
        uiApp.ActiveUIDocument?.Document != null).GetAwaiter().GetResult();

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
            var doc = uiApp.ActiveUIDocument?.Document;
            var view = doc?.ActiveView;
            if (doc == null || view == null) return null;
            var count = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().ToElementIds().Count;
            return new ViewInfoDto(view.Name, view.ViewType.ToString(), view.GetType() != null, count);
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
                .Select(g => new WarningDto(g.Key, g.Count(), "warning"))
                .ToList();
            return (IReadOnlyList<WarningDto>)list;
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

    public Task<string> RunBatchAsync(IReadOnlyList<BatchOperation> operations, CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return "{\"error\":\"No open model.\"}";
            var results = new Dictionary<string, object>();
            foreach (var op in operations)
            {
                try
                {
                    results[op.Operation] = ExecuteOperation(uiApp, doc, op);
                }
                catch (Exception ex)
                {
                    results[op.Operation] = new { error = ex.Message };
                }
            }
            return JsonSerializer.Serialize(results);
        }, ct);
    }

    private static object ExecuteOperation(UIApplication uiApp, Document doc, BatchOperation op)
    {
        switch (op.Operation)
        {
            case "get_model_summary":
                return new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Count(v => !v.IsTemplate);
            case "get_warning_count":
                return doc.GetWarnings().Count;
            case "get_level_count":
                return new FilteredElementCollector(doc).OfClass(typeof(Level)).GetElementCount();
            default:
                throw new InvalidOperationException($"Unknown batch operation '{op.Operation}'");
        }
    }
}
