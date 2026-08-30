using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using LoadingAI.Revit;
using LoadingAI.Tools;

namespace LoadingAI.Plugins.OpeningTools;

internal static class PlaceOpenings
{
    public static string Run(
        Document doc,
        UIDocument uidoc,
        IReadOnlyList<string> mepIds,
        IReadOnlyList<string> hostIds,
        string shape,
        double clearanceMm,
        string? familyName,
        string? levelName,
        int limit)
    {
        var symbol = FamilyContract.Find(doc, familyName, shape);
        var fail = FamilyContract.FailReason(symbol);
        if (fail != null) return fail;

        Level? forced = null;
        if (!string.IsNullOrWhiteSpace(levelName))
        {
            var names = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().Select(l => l.Name).ToList();
            var resolved = LevelNames.Resolve(levelName, names);
            if (resolved == null) return LevelNames.NotFound(levelName, names);
            forced = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .FirstOrDefault(l => l.Name.Equals(resolved, StringComparison.OrdinalIgnoreCase));
            if (forced == null) return LevelNames.NotFound(levelName, names);
        }

        if (!ClashProbe.AnyMep(doc, uidoc, mepIds))
            return "Please select MEP elements first.";

        var hits = ClashProbe.Find(doc, uidoc, mepIds, hostIds, Metric.MmToFt(clearanceMm), shape, limit);
        if (hits.Count == 0)
            return "No MEP-host clash found (bbox then solid intersect).";

        var placed = 0;
        var failed = 0;
        var tx = new Transaction(doc, "LoadingAI for Revit: create openings from clash");
        tx.Start();
        try
        {
            if (!symbol!.IsActive) symbol.Activate();
            foreach (var hit in hits)
            {
                try
                {
                    var level = forced ?? doc.GetElement(hit.LevelId) as Level;
                    if (level == null)
                    {
                        failed++;
                        continue;
                    }
                    var inst = doc.Create.NewFamilyInstance(hit.Centroid, symbol, level, StructuralType.NonStructural);
                    ApplySize(inst, hit);
                    WriteOp(inst, hit);
                    RotateToHost(doc, inst, hit);
                    placed++;
                }
                catch
                {
                    failed++;
                }
            }
            tx.Commit();
        }
        catch (Exception ex)
        {
            if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
            return $"Failed: {ex.Message}";
        }

        return $"Placed {placed} opening(s) in the active document ({symbol.FamilyName}). Failed {failed}. Linked MEP uses GetTotalTransform + CreateTransformed.";
    }

    static void ApplySize(FamilyInstance inst, ClashHit hit)
    {
        var widthMm = Metric.FtToMm(hit.WidthFt);
        var heightMm = Metric.FtToMm(hit.HeightFt);
        var depthMm = Metric.FtToMm(hit.DepthFt);
        if (hit.Shape == "circle")
        {
            var r = Math.Max(widthMm, heightMm) / 2;
            SetLengthFt(inst, "Radius", Metric.MmToFt(r));
        }
        SetLengthFt(inst, "Width", hit.WidthFt);
        SetLengthFt(inst, "Height", hit.HeightFt);
        SetLengthFt(inst, "Depth", hit.DepthFt);
        _ = depthMm;
    }

    static void WriteOp(FamilyInstance inst, ClashHit hit)
    {
        SetOrComment(inst, "OP_SourceId", hit.MepId.ToString());
        SetOrComment(inst, "OP_HostId", hit.HostId.ToString());
        SetOrComment(inst, "OP_HostLinked", hit.HostLinked ? "true" : "false");
        SetOrComment(inst, "OP_Shape", hit.Shape);
        SetOrComment(inst, "OP_WidthMm", Metric.FtToMm(hit.WidthFt).ToString("F0", CultureInfo.InvariantCulture));
        SetOrComment(inst, "OP_HeightMm", Metric.FtToMm(hit.HeightFt).ToString("F0", CultureInfo.InvariantCulture));
        SetOrComment(inst, "OP_DepthMm", Metric.FtToMm(hit.DepthFt).ToString("F0", CultureInfo.InvariantCulture));
    }

    static void SetLengthFt(Element el, string name, double ft)
    {
        var p = el.LookupParameter(name);
        if (p == null || p.IsReadOnly || p.StorageType != StorageType.Double) return;
        try { p.Set(ft); } catch { }
    }

    static void SetOrComment(Element el, string name, string value)
    {
        if (RevitParse.TrySetParameter(el, name, value, out _)) return;
        var comments = el.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        if (comments == null || comments.IsReadOnly) return;
        var cur = comments.AsString() ?? "";
        var bit = name + "=" + value;
        comments.Set(string.IsNullOrWhiteSpace(cur) ? bit : cur + "; " + bit);
    }

    static void RotateToHost(Document doc, FamilyInstance inst, ClashHit hit)
    {
        if (hit.HostDir.GetLength() < 1e-9) return;
        var loc = (inst.Location as LocationPoint)?.Point ?? hit.Centroid;
        var axis = Line.CreateBound(loc, loc + XYZ.BasisZ);
        var angle = XYZ.BasisX.AngleOnPlaneTo(hit.HostDir, XYZ.BasisZ);
        if (Math.Abs(angle) < 1e-6) return;
        try { ElementTransformUtils.RotateElement(doc, inst.Id, axis, angle); }
        catch { }
    }
}
