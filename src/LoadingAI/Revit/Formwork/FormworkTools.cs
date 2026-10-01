using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LoadingAI.Tools;

namespace LoadingAI.Revit.Formwork;

/// <summary>
/// Host-side entry point for <c>formwork_setup</c>. The body used to be a lambda in the
/// plugin; it moved here so the plugin needs no Revit types.
/// </summary>
internal static class FormworkSetup
{
    public static FormworkSetupResultDto Run(UIApplication uiApp)
    {
        var doc = uiApp.ActiveUIDocument?.Document;
        if (doc == null) return new FormworkSetupResultDto("Please open a model first.");
        try
        {
            // SchemaBuilder.Finish() is illegal inside an open Transaction, so the schema is created
            // and finished first; the transaction only binds parameters and counts existing panels.
            FormworkCore.GetOrCreateSchema();
            using var tx = new Transaction(doc, "Setup Formwork Parameters");
            tx.Start();
            var (ok, fail, missing) = FormworkParameters.SetupSharedParameters(doc);
            var panels = FormworkCore.CollectPanels(doc).Count;
            var links = FormworkCore.LinkCount(doc);
            tx.Commit();
            var miss = missing.Count == 0 ? "none" : string.Join(", ", missing);
            // The prose stays verbatim for the model; the counts are what let the plugin report
            // status without parsing that prose (ADR-0013).
            return new FormworkSetupResultDto(
                $"Formwork setup complete. Linked models: {links}. Parameters bound: {ok}, failed: {fail} ({miss}). Existing panels: {panels}. Default thickness {FormworkDefaults.SheetThicknessMmDefault:0} mm, coverage skip {FormworkConfig.CoverRatioDelete:P0}.",
                Created: ok,
                Failed: fail);
        }
        catch (Exception ex) { return new FormworkSetupResultDto($"Failed: {ex.Message}"); }
    }
}

/// <summary>
/// Host-side entry point for <c>create_formwork_panels</c>. <see cref="GenerateTimeout"/> is the
/// budget the plugin always used, carried across with the body.
/// </summary>
internal static class FormworkPanels
{
    public static readonly TimeSpan GenerateTimeout = TimeSpan.FromMinutes(10);

    public static FormworkPanelsResultDto Run(
        UIApplication uiApp,
        IReadOnlyList<string> ids,
        double thicknessMm,
        IReadOnlyList<string> faceTypes,
        bool collision,
        bool deleteExisting,
        bool includeUnselectedLinks)
    {
        var doc = uiApp.ActiveUIDocument?.Document;
        if (doc == null) return new FormworkPanelsResultDto("Please open a model first.");
        try
        {
            // Generate() calls GetOrCreateSchema() too; creating it here makes that a Lookup hit, so no
            // SchemaBuilder.Finish() ever runs with this transaction open.
            FormworkCore.GetOrCreateSchema();
            using var tx = new Transaction(doc, "LoadingAI for Revit: create formwork panels");
            tx.Start();
            var result = FormworkGenerator.Generate(doc, ids, new GenerateOptions
            {
                ThicknessMm = thicknessMm,
                FaceTypes = new HashSet<string>(faceTypes, StringComparer.OrdinalIgnoreCase),
                Collision = collision,
                DeleteExisting = deleteExisting,
                IncludeUnselectedLinks = includeUnselectedLinks
            });
            tx.Commit();
            var fails = result.FailReasons.Count == 0
                ? ""
                : " Failures: " + string.Join(", ", result.FailReasons.Select(kv => $"{kv.Key}={kv.Value}"));
            // The sentence is unchanged, so model-readable text does not regress, but the same
            // numbers now cross the Bridge as counts instead of dying inside the prose.
            return new FormworkPanelsResultDto(
                $"Formwork panels: selected {result.SelectedElements}, candidates {result.Candidates}, created {result.Created}, updated {result.Updated}, duplicates {result.Duplicates}, coverage skipped {result.CoverageSkipped}, panel overlap skipped {result.PanelOverlapSkipped}, trimmed {result.Trimmed}, obsolete deleted {result.DeletedObsolete}, failed {result.Failed}.{fails}",
                Created: result.Created,
                Updated: result.Updated,
                Deleted: result.DeletedObsolete,
                Failed: result.Failed);
        }
        catch (Exception ex) { return new FormworkPanelsResultDto($"Failed: {ex.Message}"); }
    }
}

/// <summary>Host-side entry point for <c>cleanup_formwork</c>.</summary>
internal static class FormworkCleanup
{
    public static CleanupResultDto Run(UIApplication uiApp, string action, string? faceType)
    {
        var doc = uiApp.ActiveUIDocument?.Document;
        if (doc == null) return new CleanupResultDto("Please open a model first.");
        try
        {
            var panels = FormworkCore.CollectPanels(doc);
            // stats returns before tx.Start, so it never writes. Every count stays at zero, which
            // lets the plugin report it as a read from its own knowledge of the action argument
            // instead of from the wording of the prose.
            if (action is "stats" or "")
                return new CleanupResultDto(FormworkPanelQueries.FormatStats(panels));

            using var tx = new Transaction(doc, "LoadingAI for Revit: cleanup formwork");
            tx.Start();
            int n = 0;
            string msg;
            switch (action)
            {
                case "delete_all":
                    n = FormworkCore.DeletePanels(doc, panels);
                    msg = $"Deleted {n} formwork panel(s).";
                    break;
                case "delete_by_type":
                    if (string.IsNullOrWhiteSpace(faceType))
                    {
                        tx.RollBack();
                        return new CleanupResultDto("Failed: faceType is required for delete_by_type.");
                    }
                    var match = panels.Where(p => string.Equals(FormworkCore.ParamText(p, "FW_PanelType"), faceType, StringComparison.OrdinalIgnoreCase)).ToList();
                    n = FormworkCore.DeletePanels(doc, match);
                    msg = $"Deleted {n} formwork panel(s) of type {faceType}.";
                    break;
                case "cleanup_orphans":
                    var orphans = panels.Where(p => FormworkPanelQueries.IsOrphan(doc, p)).ToList();
                    n = FormworkCore.DeletePanels(doc, orphans);
                    msg = $"Deleted {n} orphan formwork panel(s).";
                    break;
                default:
                    tx.RollBack();
                    // Prefixed Failed: for the same reason as the faceType branch above.
                    return new CleanupResultDto("Failed: Unknown action. Use stats, delete_all, delete_by_type, or cleanup_orphans.");
            }
            tx.Commit();
            return new CleanupResultDto(msg, Deleted: n);
        }
        catch (Exception ex) { return new CleanupResultDto($"Failed: {ex.Message}"); }
    }
}

/// <summary>Host-side entry point for <c>copy_formwork_parent_params</c>.</summary>
internal static class FormworkParentParams
{
    public static CopyParamsResultDto Run(UIApplication uiApp, IReadOnlyList<string> ids, IReadOnlyList<string> names)
    {
        var doc = uiApp.ActiveUIDocument?.Document;
        if (doc == null) return new CopyParamsResultDto("Please open a model first.");
        try
        {
            var all = FormworkCore.CollectPanels(doc);
            var panels = ids.Count == 0
                ? all
                : ids.Select(id =>
                {
                    try { return doc.GetElement(RevitParse.ParseEid(id)) as DirectShape; }
                    catch { return null; }
                }).Where(ds => ds != null && string.Equals(ds.ApplicationId, FormworkConfig.AppId, StringComparison.Ordinal)).Cast<DirectShape>().ToList();
            if (panels.Count == 0) return new CopyParamsResultDto("No formwork panels found. Select panels or run create_formwork_panels first.");

            // The catalogue path returns before tx.Start, so it never writes. Zero counts say so.
            if (names.Count == 0)
                return new CopyParamsResultDto(FormworkPanelQueries.Catalog(doc, panels));

            using var tx = new Transaction(doc, "Copy Parent Params to Formwork");
            tx.Start();
            var destNames = names.Select(n => n.StartsWith("FW_", StringComparison.OrdinalIgnoreCase) ? n : "FW_" + n).ToList();
            FormworkParameters.EnsureNamedParameters(doc, destNames);
            var copied = 0;
            var skippedEmpty = 0;
            var skippedMismatch = 0;
            var unresolved = 0;
            var unloaded = 0;
            foreach (var panel in panels)
            {
                var parent = FormworkPanelQueries.FindParent(doc, panel, out var status);
                if (status == "link_unloaded") { unloaded++; continue; }
                if (parent == null) { unresolved++; continue; }
                foreach (var name in names)
                {
                    Parameter? src = null;
                    foreach (var p in FormworkParameters.IterCopyable(parent))
                    {
                        if (string.Equals(p.Definition?.Name, name, StringComparison.OrdinalIgnoreCase))
                        { src = p; break; }
                    }
                    if (src == null) continue;
                    if (FormworkParameters.IsEmpty(src)) { skippedEmpty++; continue; }
                    var destName = name.StartsWith("FW_", StringComparison.OrdinalIgnoreCase) ? name : "FW_" + name;
                    var dest = panel.LookupParameter(destName);
                    if (dest == null) continue;
                    if (src.StorageType != dest.StorageType) { skippedMismatch++; continue; }
                    if (FormworkParameters.CopyValue(src, dest)) copied++;
                }
            }
            tx.Commit();
            return new CopyParamsResultDto(
                $"Copied {copied} parent parameter value(s) onto {panels.Count} panel(s). Empty skipped {skippedEmpty}, type mismatch {skippedMismatch}, unresolved parent {unresolved}, unloaded link {unloaded}.",
                Updated: copied);
        }
        catch (Exception ex) { return new CopyParamsResultDto($"Failed: {ex.Message}"); }
    }
}

/// <summary>
/// The panel-walking helpers the plugin used to declare. They take <see cref="Document"/> and
/// <see cref="DirectShape"/>, so they belong host-side with the entry points above.
/// </summary>
internal static class FormworkPanelQueries
{
    public static string Catalog(Document doc, List<DirectShape> panels)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var n = 0;
        foreach (var panel in panels)
        {
            var parent = FindParent(doc, panel, out _);
            if (parent == null) continue;
            foreach (var p in FormworkParameters.IterCopyable(parent))
            {
                var nm = p.Definition?.Name;
                if (nm is { Length: > 0 }) names.Add(nm);
            }
            n++;
            if (n >= FormworkConfig.MaxParentsForCatalog) break;
        }
        if (names.Count == 0) return "No copyable parent parameters found. Pass paramNames to copy.";
        return "Copyable parent parameters (pass paramNames to copy onto FW_*): " + string.Join(", ", names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
    }

    public static Element? FindParent(Document doc, DirectShape panel, out string status)
    {
        status = "";
        var uid = FormworkCore.ParamText(panel, "FW_ParentUniqueId");
        var linkName = FormworkCore.ParamText(panel, "FW_LinkName");
        var idText = FormworkCore.ParamText(panel, "FW_ParentElementId");
        if (string.IsNullOrWhiteSpace(idText))
        {
            var p = panel.LookupParameter("FW_ParentElementId");
            try { idText = p?.AsInteger().ToString(); }
            catch { idText = ""; }
        }

        Document target = doc;
        if (!string.IsNullOrWhiteSpace(linkName) && !string.Equals(linkName, doc.Title, StringComparison.OrdinalIgnoreCase))
        {
            RevitLinkInstance? found = null;
            var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).WhereElementIsNotElementType();
            foreach (RevitLinkInstance link in links)
            {
                var ld = link.GetLinkDocument();
                var title = ld?.Title ?? link.Name;
                if (string.Equals(title, linkName, StringComparison.OrdinalIgnoreCase))
                { found = link; break; }
            }
            if (found == null) { status = "unresolved_parent"; return null; }
            var linked = found.GetLinkDocument();
            if (linked == null) { status = "link_unloaded"; return null; }
            target = linked;
        }

        if (!string.IsNullOrWhiteSpace(uid))
        {
            try
            {
                var byUid = target.GetElement(uid);
                if (byUid != null) return byUid;
            }
            catch { /* */ }
        }
        if (!string.IsNullOrWhiteSpace(idText))
        {
            try
            {
                var byId = target.GetElement(RevitParse.ParseEid(idText!));
                if (byId != null) return byId;
            }
            catch { /* */ }
        }
        status = "unresolved_parent";
        return null;
    }

    public static bool IsOrphan(Document doc, DirectShape panel)
    {
        var parentId = panel.LookupParameter("FW_ParentElementId");
        try
        {
            if (parentId != null && parentId.AsInteger() == 0) return true;
        }
        catch { /* */ }
        var uid = FormworkCore.ParamText(panel, "FW_ParentUniqueId");
        var linkName = FormworkCore.ParamText(panel, "FW_LinkName");
        if (!string.IsNullOrWhiteSpace(linkName) && !string.Equals(linkName, doc.Title, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(uid))
        {
            try { return doc.GetElement(uid) == null; }
            catch { return true; }
        }
        var idText = "";
        try { idText = parentId?.AsInteger().ToString() ?? ""; }
        catch { /* */ }
        if (string.IsNullOrWhiteSpace(idText) || idText == "0") return true;
        try { return doc.GetElement(RevitParse.ParseEid(idText)) == null; }
        catch { return true; }
    }

    public static string FormatStats(List<DirectShape> panels)
    {
        if (panels.Count == 0) return "No formwork panels in the model.";
        var byType = panels
            .GroupBy(p => FormworkCore.ParamText(p, "FW_PanelType") is { Length: > 0 } t ? t : "Unknown")
            .OrderBy(g => g.Key);
        var total = panels.Sum(p => FormworkCore.ParamArea(p, "FW_Area"));
        var net = panels.Sum(p =>
        {
            var n = FormworkCore.ParamArea(p, "FW_AreaNet");
            return n > 0 ? n : FormworkCore.ParamArea(p, "FW_Area");
        });
        var lines = new List<string>
        {
            $"Formwork panels: {panels.Count}. Gross {Metric.Area(total)}, net {Metric.Area(net)}."
        };
        foreach (var g in byType)
        {
            var a = g.Sum(p => FormworkCore.ParamArea(p, "FW_Area"));
            lines.Add($"  {g.Key}: {g.Count()} ({Metric.Area(a)})");
        }
        return string.Join(" ", lines);
    }
}
