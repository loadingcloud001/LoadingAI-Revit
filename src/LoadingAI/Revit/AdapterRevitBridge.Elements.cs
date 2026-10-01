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
/// Element slice: delete, move, change type.
/// Implements <c>IRevitElementCommand</c>. Part of <see cref="AdapterRevitBridge"/>; see that type for the contract.
/// </summary>
public sealed partial class AdapterRevitBridge
{
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
}
