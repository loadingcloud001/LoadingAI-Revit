using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using LoadingAI.Revit;
using LoadingAI.Tools;

namespace LoadingAI.Plugins.StructureCuts;

internal static class BeamSplit
{
    private static readonly double ZTolFt = Metric.MmToFt(30);
    private static readonly double XyPadFt = Metric.MmToFt(150);

    public static string Run(UIApplication uiApp, IReadOnlyList<string> beamIds, IReadOnlyList<string> columnIds, int limit, bool dryRun = false)
    {
        var uidoc = uiApp.ActiveUIDocument;
        var doc = uidoc?.Document;
        if (uidoc == null || doc == null) return "Please open a model first.";

        var selection = GetSelection(uidoc);
        var beams = Resolve(doc, beamIds, IsBeam);
        if (beams.Count == 0)
            beams = selection.Where(IsBeam).ToList();
        if (beams.Count == 0) return "Please select elements first.";

        var columns = Resolve(doc, columnIds, IsColumn);
        if (columns.Count == 0)
            columns = selection.Where(IsColumn).ToList();
        if (columns.Count == 0)
            columns = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_StructuralColumns)
                .WhereElementIsNotElementType()
                .ToElements()
                .Where(IsColumn)
                .ToList();
        if (columns.Count == 0) return "No columns found.";

        var stopped = beams.Count > limit;
        if (stopped) beams = beams.Take(limit).ToList();

        var split = 0;
        var segments = 0;
        var skipNoCurve = 0;
        var skipNoHit = 0;
        var skipFailed = 0;
        var paramSkipped = 0;

        Transaction? tx = dryRun ? null : new Transaction(doc, "LoadingAI for Revit: split beams by columns");
        tx?.Start();
        foreach (var beam in beams)
        {
            if (GetBeamCurve(beam) == null)
            {
                skipNoCurve++;
                continue;
            }
            var pars = FindSplitPoints(beam, columns);
            if (pars.Count == 0)
            {
                skipNoHit++;
                continue;
            }
            if (dryRun)
            {
                split++;
                segments += pars.Count + 1;
                continue;
            }
            var (ids, skipped) = SplitAtParams(doc, beam, pars);
            paramSkipped += skipped;
            if (ids.Count > 1)
            {
                split++;
                segments += ids.Count;
            }
            else
                skipFailed++;
        }
        tx?.Commit();

        var msg = dryRun
            ? $"Dry-run. Would split {split} beams into {segments} segments. No changes made."
            : $"Split {split} beams into {segments} segments.";
        msg += $" Skipped: {skipNoCurve} no curve, {skipNoHit} no column hit";
        if (skipFailed > 0) msg += $", {skipFailed} failed";
        msg += ".";
        if (!dryRun && split > 0) msg += " Instance parameters copied to new pieces.";
        if (!dryRun && paramSkipped > 0) msg += $" Param copy skipped: {paramSkipped}.";
        if (stopped) msg += $" Stopped at {limit}.";
        return msg;
    }

    private static List<double> FindSplitPoints(Element beam, IReadOnlyList<Element> columns)
    {
        var curve = GetBeamCurve(beam);
        if (curve == null) return new List<double>();

        var splitParams = new List<double>();
        var beamBb = beam.get_BoundingBox(null);
        var beamStart = curve.GetEndPoint(0);

        foreach (var column in columns)
        {
            var colBb = column.get_BoundingBox(null);
            if (beamBb != null && colBb != null)
            {
                if (beamBb.Max.Z < colBb.Min.Z - ZTolFt || colBb.Max.Z < beamBb.Min.Z - ZTolFt)
                    continue;
            }

            var center = ColumnCenterXy(column);
            if (center == null) continue;
            var colPoint = new XYZ(center.X, center.Y, beamStart.Z);
            var width = ColumnWidth(column);
            var proj = curve.Project(colPoint);
            if (proj == null) continue;
            if (colPoint.DistanceTo(proj.XYZPoint) > width / 2 + XyPadFt) continue;

            var startP = curve.GetEndParameter(0);
            var endP = curve.GetEndParameter(1);
            var span = endP - startP;
            if (Math.Abs(span) < 1e-9) continue;
            var normalized = (proj.Parameter - startP) / span;
            if (normalized > 0.02 && normalized < 0.98)
                splitParams.Add(normalized);
        }

        splitParams.Sort();
        var filtered = new List<double>();
        foreach (var p in splitParams)
        {
            if (filtered.Count == 0 || p - filtered[filtered.Count - 1] > 0.05)
                filtered.Add(p);
        }
        return filtered;
    }

    private static (List<ElementId> ids, int paramSkipped) SplitAtParams(Document doc, Element beam, IReadOnlyList<double> splitParams)
    {
        var created = new List<ElementId>();
        var paramSkipped = 0;
        var curve = GetBeamCurve(beam);
        if (curve == null || beam is not FamilyInstance fi) return (created, 0);

        var symbol = doc.GetElement(beam.GetTypeId()) as FamilySymbol;
        if (symbol == null) return (created, 0);
        if (!symbol.IsActive) symbol.Activate();

        var levelId = beam.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM)?.AsElementId();
        if (levelId == null || levelId == ElementId.InvalidElementId)
            levelId = beam.LevelId;
        var level = doc.GetElement(levelId) as Level;
        if (level == null) return (created, 0);

        var structuralType = fi.StructuralType;
        var startP = curve.GetEndParameter(0);
        var endP = curve.GetEndParameter(1);
        var span = endP - startP;
        var all = new List<double> { 0.0 };
        all.AddRange(splitParams);
        all.Add(1.0);

        for (var i = 0; i < all.Count - 1; i++)
        {
            var p1 = startP + all[i] * span;
            var p2 = startP + all[i + 1] * span;
            XYZ pt1, pt2;
            try
            {
                pt1 = curve.Evaluate(p1, false);
                pt2 = curve.Evaluate(p2, false);
            }
            catch { continue; }
            if (pt1.DistanceTo(pt2) < 1e-6) continue;

            Line line;
            try { line = Line.CreateBound(pt1, pt2); }
            catch { continue; }

            if (i == 0)
            {
                try
                {
                    ((LocationCurve)beam.Location).Curve = line;
                    created.Add(beam.Id);
                }
                catch { return (created, paramSkipped); }
            }
            else
            {
                try
                {
                    var neu = doc.Create.NewFamilyInstance(line, symbol, level, structuralType);
                    if (neu != null)
                    {
                        paramSkipped += ParamCopy.Apply(beam, neu);
                        created.Add(neu.Id);
                    }
                }
                catch { }
            }
        }
        return (created, paramSkipped);
    }

    private static Curve? GetBeamCurve(Element beam)
        => beam.Location is LocationCurve lc ? lc.Curve : null;

    private static XYZ? ColumnCenterXy(Element column)
    {
        var bb = column.get_BoundingBox(null);
        if (bb == null) return null;
        return new XYZ((bb.Min.X + bb.Max.X) / 2, (bb.Min.Y + bb.Max.Y) / 2, 0);
    }

    private static double ColumnWidth(Element column)
    {
        var bb = column.get_BoundingBox(null);
        if (bb == null) return 1.0;
        return Math.Max(bb.Max.X - bb.Min.X, bb.Max.Y - bb.Min.Y);
    }

    private static List<Element> Resolve(Document doc, IReadOnlyList<string> ids, Func<Element, bool> pred)
    {
        var list = new List<Element>();
        foreach (var s in ids)
        {
            try
            {
                var el = doc.GetElement(RevitParse.ParseEid(s));
                if (el != null && pred(el)) list.Add(el);
            }
            catch { }
        }
        return list;
    }

    private static List<Element> GetSelection(UIDocument uidoc)
        => uidoc.Selection.GetElementIds()
            .Select(id => uidoc.Document.GetElement(id))
            .Where(e => e != null)
            .Cast<Element>()
            .ToList();

    private static bool IsBeam(Element el)
        => el.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralFraming;

    private static bool IsColumn(Element el)
        => el.Category?.Id.Value == (long)BuiltInCategory.OST_StructuralColumns;
}
