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
/// Document slice: save, reload links, assign workset, load family, PDF/DWG/IFC export, purge unused.
/// Implements <c>IRevitDocumentCommand</c>. Part of <see cref="AdapterRevitBridge"/>; see that type for the contract.
/// </summary>
public sealed partial class AdapterRevitBridge
{
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
}
